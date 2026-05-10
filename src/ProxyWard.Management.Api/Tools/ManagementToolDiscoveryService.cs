using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.Management.Api.Tools;

public sealed class ManagementToolDiscoveryService
{
    private const string DefaultMcpProtocol = "2025-11-25";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ManagementApiOptions _options;
    private readonly SqlitePolicyStore _policyStore;
    private readonly ToolDefinitionExtractor _extractor = new();
    private readonly ToolFingerprinter _fingerprinter = new();

    public ManagementToolDiscoveryService(
        HttpClient httpClient,
        ManagementApiOptions options,
        SqlitePolicyStore policyStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
    }

    public async Task<ManagementToolDiscoveryResponse> DiscoverAsync(
        ManagementToolDiscoveryRequest? request,
        CancellationToken cancellationToken)
    {
        var serverId = NormalizeRequired(request?.ServerId, "serverId");
        var upstream = await ResolveUpstreamAsync(serverId, request?.Upstream, cancellationToken).ConfigureAwait(false);
        var responseBody = await RequestToolsListAsync(upstream, cancellationToken).ConfigureAwait(false);
        var extraction = _extractor.Extract(responseBody);
        if (!extraction.Success)
        {
            throw new ManagementToolDiscoveryException(
                "tool_discovery_response_invalid",
                "tools/list returned a response that could not be parsed as MCP tool metadata.");
        }

        var entries = extraction.Tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .GroupBy(tool => tool.Name!, StringComparer.Ordinal)
            .Select(group => SafeToolSchemaMetadata.CreateSnapshotEntry(
                group.First(),
                _fingerprinter,
                ToolSchemaDiffMetadataOptions.Default))
            .OrderBy(entry => entry.ToolName, StringComparer.Ordinal)
            .ToArray();

        using var store = new SqliteTrackedToolSchemaStore(_options.AuditDatabasePath);
        var snapshot = new ToolSchemaSnapshotInput(
            ServerId: serverId,
            UpstreamUrl: upstream.AbsoluteUri,
            McpProtocol: DefaultMcpProtocol,
            Tools: entries,
            PolicyVersion: null,
            SourceCorrelationId: $"mgmt-tools-{Guid.NewGuid():N}");
        var recorded = await store
            .RecordAsync(snapshot, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        return new ManagementToolDiscoveryResponse(
            ServerId: serverId,
            Upstream: upstream.AbsoluteUri,
            LatestVersion: recorded.Version,
            SnapshotHash: recorded.SnapshotHash,
            WasNewVersion: recorded.WasNewVersion,
            Tools: entries
                .Select(entry => new ManagementToolInventoryTool(
                    Name: entry.ToolName,
                    LatestVersion: recorded.Version,
                    DriftStatus: "clean",
                    Title: entry.Title,
                    Description: entry.Description,
                    NameHash: entry.Fingerprint.NameHash,
                    TitleHash: entry.Fingerprint.TitleHash,
                    DescriptionHash: entry.Fingerprint.DescriptionHash,
                    InputSchemaHash: entry.Fingerprint.InputSchemaHash,
                    OutputSchemaHash: entry.Fingerprint.OutputSchemaHash))
                .ToArray());
    }

    private async Task<Uri> ResolveUpstreamAsync(
        string serverId,
        string? requestedUpstream,
        CancellationToken cancellationToken)
    {
        var normalizedRequestedUpstream = NormalizeOptional(requestedUpstream);
        if (normalizedRequestedUpstream is not null)
        {
            return ParseUpstream(normalizedRequestedUpstream);
        }

        try
        {
            var snapshot = await _policyStore.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is not null
                && snapshot.Policy.Servers.TryGetValue(serverId, out var configuredServer))
            {
                return configuredServer.Upstream;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new ManagementToolDiscoveryRequestException(
                "tool_discovery_policy_unavailable",
                $"Policy database could not be read: {ex.Message}");
        }

        throw new ManagementToolDiscoveryRequestException(
            "tool_discovery_server_not_found",
            $"Server '{serverId}' is not in the persisted policy and no upstream was supplied.");
    }

    private async Task<byte[]> RequestToolsListAsync(Uri upstream, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, upstream);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "proxyward-dashboard-tools-discovery",
                method = "tools/list",
                @params = new { }
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new ManagementToolDiscoveryException(
                "tool_discovery_timeout",
                "tools/list discovery timed out.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ManagementToolDiscoveryException(
                "tool_discovery_transport_error",
                $"tools/list discovery failed: {ex.Message}",
                innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ManagementToolDiscoveryException(
                    "tool_discovery_upstream_failed",
                    $"tools/list returned HTTP {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }

            return body;
        }
    }

    private static Uri ParseUpstream(string upstream)
    {
        if (upstream.Contains("***@", StringComparison.Ordinal)
            || upstream.Contains("[masked]", StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagementToolDiscoveryRequestException(
                "tool_discovery_upstream_masked",
                "Masked upstream credentials cannot be used for discovery.");
        }

        if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ManagementToolDiscoveryRequestException(
                "tool_discovery_upstream_invalid",
                "upstream must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            throw new ManagementToolDiscoveryRequestException(
                "tool_discovery_request_invalid",
                $"{fieldName} is required.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

public sealed record ManagementToolDiscoveryRequest(
    string? ServerId,
    string? Upstream);

public sealed record ManagementToolDiscoveryResponse(
    string ServerId,
    string Upstream,
    int LatestVersion,
    string SnapshotHash,
    bool WasNewVersion,
    IReadOnlyList<ManagementToolInventoryTool> Tools);

public sealed class ManagementToolDiscoveryRequestException : Exception
{
    public ManagementToolDiscoveryRequestException(string error, string message)
        : base(message)
    {
        Error = error;
    }

    public string Error { get; }
}

public sealed class ManagementToolDiscoveryException : Exception
{
    public ManagementToolDiscoveryException(
        string error,
        string message,
        int? upstreamStatusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        UpstreamStatusCode = upstreamStatusCode;
    }

    public string Error { get; }

    public int? UpstreamStatusCode { get; }
}

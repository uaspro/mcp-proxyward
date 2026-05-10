using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Api.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Policy;

public sealed class ManagementPolicyApplyService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ManagementPolicyValidationService _validationService;
    private readonly IProxyControlClient _proxyControlClient;
    private readonly ManagementApiOptions _options;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public ManagementPolicyApplyService(
        ManagementApiOptions options,
        ManagementPolicyValidationService validationService,
        IProxyControlClient proxyControlClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));

        _databasePath = Path.GetFullPath(options.AuditDatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<ManagementPolicyApplyOutcome> ApplyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var proposal = await _validationService
            .ValidateProposalAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!proposal.Response.Valid || proposal.Policy is null)
        {
            return ManagementPolicyApplyOutcome.ValidationFailed(proposal.Response);
        }

        var yarpConfig = CreateYarpConfig(proposal.Policy);
        var previousStatus = await ReadCurrentStatusAsync(cancellationToken).ConfigureAwait(false);
        var yarpStatus = await ApplyYarpConfigAsync(yarpConfig, cancellationToken).ConfigureAwait(false);
        var appliedStatus = await ApplyPolicySnapshotAsync(proposal.Yaml, cancellationToken).ConfigureAwait(false);

        await WritePolicyApplyAuditAsync(
            previousStatus,
            appliedStatus,
            yarpStatus,
            proposal,
            cancellationToken).ConfigureAwait(false);

        return ManagementPolicyApplyOutcome.Applied(new ManagementPolicyApplyResponse(
            PreviousMode: previousStatus.Mode,
            Mode: appliedStatus.Mode,
            PreviousPolicyHash: previousStatus.PolicyVersion,
            PolicyHash: appliedStatus.PolicyVersion,
            ServerCount: appliedStatus.ServerCount,
            RouteVersion: appliedStatus.RouteVersion,
            Yarp: yarpStatus));
    }

    private async Task<ProxyControlStatus> ReadCurrentStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            throw new ManagementPolicyApplyException(
                "status",
                ex.Message,
                rollbackAttempted: false,
                rollbackApplied: false,
                ex);
        }
    }

    private async Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
        ProxyControlYarpConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient
                .ApplyYarpConfigAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            throw new ManagementPolicyApplyException(
                "yarp_config",
                ex.Message,
                rollbackAttempted: false,
                rollbackApplied: false,
                ex);
        }
    }

    private async Task<ProxyControlStatus> ApplyPolicySnapshotAsync(
        string yaml,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient
                .ApplyPolicySnapshotAsync(yaml, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            var rollback = await TryRollbackYarpAsync(cancellationToken).ConfigureAwait(false);
            throw new ManagementPolicyApplyException(
                "policy_snapshot",
                ex.Message,
                rollback.Attempted,
                rollback.Applied,
                ex);
        }
    }

    private async Task<(bool Attempted, bool Applied)> TryRollbackYarpAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.PolicyPath))
        {
            return (Attempted: false, Applied: false);
        }

        try
        {
            var yaml = await File.ReadAllTextAsync(_options.PolicyPath, cancellationToken).ConfigureAwait(false);
            var currentPolicy = ProxyWardPolicyLoader.Load(yaml);
            var rollbackConfig = CreateYarpConfig(currentPolicy);
            await _proxyControlClient.ApplyYarpConfigAsync(rollbackConfig, cancellationToken).ConfigureAwait(false);
            return (Attempted: true, Applied: true);
        }
        catch
        {
            return (Attempted: true, Applied: false);
        }
    }

    private async Task WritePolicyApplyAuditAsync(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ProxyControlYarpConfigStatus yarpStatus,
        ManagementPolicyValidationOutcome proposal,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var requestedBy = NormalizeText(proposal.RequestedBy) ?? "admin";
        var note = NormalizeText(proposal.Note);
        var correlationId = $"mgmt-{Guid.NewGuid():N}";
        var payloadJson = CreatePayloadJson(
            previousStatus,
            appliedStatus,
            yarpStatus,
            timestamp,
            requestedBy,
            note,
            correlationId);

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureAuditSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (
                timestamp_utc,
                event_type,
                mode,
                decision,
                server_id,
                method,
                tool_name,
                reasons,
                policy_version,
                correlation_id,
                request_bytes,
                duration_ms,
                payload_json
            ) VALUES (
                $timestamp_utc,
                $event_type,
                $mode,
                $decision,
                $server_id,
                $method,
                $tool_name,
                $reasons,
                $policy_version,
                $correlation_id,
                $request_bytes,
                $duration_ms,
                $payload_json
            );
            """;
        command.Parameters.AddWithValue("$timestamp_utc", timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_type", "policy_apply");
        command.Parameters.AddWithValue("$mode", "management");
        command.Parameters.AddWithValue("$decision", "allow");
        command.Parameters.AddWithValue("$server_id", "management");
        command.Parameters.AddWithValue("$method", "policy/apply");
        command.Parameters.AddWithValue("$tool_name", DBNull.Value);
        command.Parameters.AddWithValue("$reasons", "policy_apply_accepted");
        command.Parameters.AddWithValue("$policy_version", appliedStatus.PolicyVersion);
        command.Parameters.AddWithValue("$correlation_id", correlationId);
        command.Parameters.AddWithValue("$request_bytes", 0);
        command.Parameters.AddWithValue("$duration_ms", 0);
        command.Parameters.AddWithValue("$payload_json", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreatePayloadJson(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ProxyControlYarpConfigStatus yarpStatus,
        DateTimeOffset timestamp,
        string requestedBy,
        string? note,
        string correlationId)
    {
        var argumentSummary = new JsonObject
        {
            ["previousMode"] = NormalizeMode(previousStatus.Mode),
            ["mode"] = NormalizeMode(appliedStatus.Mode),
            ["previousPolicyHash"] = previousStatus.PolicyVersion,
            ["policyHash"] = appliedStatus.PolicyVersion,
            ["requestedBy"] = requestedBy,
            ["note"] = note,
            ["serverCount"] = appliedStatus.ServerCount,
            ["routeVersion"] = appliedStatus.RouteVersion,
            ["yarpRouteVersion"] = yarpStatus.RouteVersion,
            ["routeCount"] = yarpStatus.RouteCount,
            ["clusterCount"] = yarpStatus.ClusterCount
        };

        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["eventType"] = "policy_apply",
            ["mode"] = "management",
            ["decision"] = "allow",
            ["serverId"] = "management",
            ["method"] = "policy/apply",
            ["toolName"] = null,
            ["reasons"] = new JsonArray(JsonValue.Create("policy_apply_accepted")),
            ["policyVersion"] = appliedStatus.PolicyVersion,
            ["correlationId"] = correlationId,
            ["requestBytes"] = 0,
            ["durationMs"] = 0,
            ["batchSize"] = 0,
            ["batchIndex"] = null,
            ["argumentOverrideApplied"] = false,
            ["argumentSummary"] = argumentSummary
        };

        return payload.ToJsonString(CompactJsonOptions);
    }

    private static ProxyControlYarpConfigRequest CreateYarpConfig(ProxyWardPolicy policy) =>
        new(
            Routes: policy.Servers.Values
                .SelectMany(CreateRoutes)
                .ToArray(),
            Clusters: policy.Servers.Values
                .Select(CreateCluster)
                .ToArray());

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateRoutes(ServerPolicy server)
    {
        var routePrefix = NormalizeRoutePrefix(server.Route);
        var transforms = CreatePathTransforms(routePrefix, server.Upstream.AbsolutePath);

        yield return new ProxyControlYarpRouteRequest(
            RouteId: $"{server.Id}-exact",
            ClusterId: server.Id,
            Order: 0,
            Match: new ProxyControlYarpRouteMatchRequest(routePrefix),
            Transforms: transforms);

        yield return new ProxyControlYarpRouteRequest(
            RouteId: $"{server.Id}-catch-all",
            ClusterId: server.Id,
            Order: 1,
            Match: new ProxyControlYarpRouteMatchRequest($"{routePrefix}/{{**catchAll}}"),
            Transforms: transforms);
    }

    private static ProxyControlYarpClusterRequest CreateCluster(ServerPolicy server) =>
        new(
            ClusterId: server.Id,
            Destinations: new Dictionary<string, ProxyControlYarpDestinationRequest>(StringComparer.Ordinal)
            {
                ["primary"] = new(CreateDestinationAddress(server.Upstream))
            });

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreatePathTransforms(
        string routePrefix,
        string upstreamPath)
    {
        var transforms = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PathRemovePrefix"] = routePrefix
            }
        };

        if (!string.IsNullOrWhiteSpace(upstreamPath) && upstreamPath != "/")
        {
            transforms.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PathPrefix"] = upstreamPath.TrimEnd('/')
            });
        }

        return transforms;
    }

    private static string CreateDestinationAddress(Uri upstream)
    {
        var builder = new UriBuilder(upstream)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeRoutePrefix(string route)
    {
        var normalized = route.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(mode, "enforce", StringComparison.OrdinalIgnoreCase)
            ? "enforce"
            : "audit";

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAuditSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                mode TEXT NOT NULL,
                decision TEXT NOT NULL,
                server_id TEXT NOT NULL,
                method TEXT NULL,
                tool_name TEXT NULL,
                reasons TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                request_bytes INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_events_timestamp ON audit_events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_audit_events_decision ON audit_events(decision);
            CREATE INDEX IF NOT EXISTS idx_audit_events_server_id ON audit_events(server_id);
            CREATE INDEX IF NOT EXISTS idx_audit_events_method ON audit_events(method);
            CREATE INDEX IF NOT EXISTS idx_audit_events_tool_name ON audit_events(tool_name);
            CREATE INDEX IF NOT EXISTS idx_audit_events_reasons ON audit_events(reasons);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ManagementPolicyApplyOutcome(
    ManagementPolicyApplyResponse? Response,
    ManagementPolicyValidationResponse? ValidationFailure)
{
    public bool IsApplied => Response is not null;

    public static ManagementPolicyApplyOutcome Applied(ManagementPolicyApplyResponse response) =>
        new(response, ValidationFailure: null);

    public static ManagementPolicyApplyOutcome ValidationFailed(ManagementPolicyValidationResponse validation) =>
        new(Response: null, validation);
}

public sealed record ManagementPolicyApplyResponse(
    string PreviousMode,
    string Mode,
    string PreviousPolicyHash,
    string PolicyHash,
    int ServerCount,
    int? RouteVersion,
    ProxyControlYarpConfigStatus Yarp);

public sealed class ManagementPolicyApplyException : Exception
{
    public ManagementPolicyApplyException(
        string phase,
        string message,
        bool rollbackAttempted,
        bool rollbackApplied,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        RollbackAttempted = rollbackAttempted;
        RollbackApplied = rollbackApplied;
    }

    public string Phase { get; }

    public bool RollbackAttempted { get; }

    public bool RollbackApplied { get; }
}

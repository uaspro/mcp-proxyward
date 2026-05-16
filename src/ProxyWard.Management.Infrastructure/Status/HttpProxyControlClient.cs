using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Status;

namespace ProxyWard.Management.Infrastructure.Status;

public sealed class HttpProxyControlClient : IProxyControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ManagementApiOptions _options;

    public HttpProxyControlClient(HttpClient httpClient, ManagementApiOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ProxyControlProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProxyControlToken))
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Unknown,
                "control token not configured",
                Details: null);
        }

        try
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);

            return new ProxyControlProbeResult(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: status.ToDetails());
        }
        catch (ProxyControlClientException ex) when (ex.StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Unhealthy,
                "control token rejected",
                Details: null);
        }
        catch (ProxyControlClientException ex) when (ex.StatusCode is not null)
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                $"proxy returned status {ex.StatusCode}",
                Details: null);
        }
        catch (ProxyControlClientException ex) when (string.Equals(ex.Error, "proxy_control_malformed_response", StringComparison.Ordinal))
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy returned malformed body",
                Details: null);
        }
        catch (ProxyControlClientException ex) when (string.Equals(ex.Error, "proxy_control_timeout", StringComparison.Ordinal))
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy probe timed out",
                Details: null);
        }
        catch (ProxyControlClientException ex) when (string.Equals(ex.Error, "proxy_control_transport_error", StringComparison.Ordinal))
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy probe failed (transport error)",
                Details: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy probe failed (unexpected error)",
                Details: null);
        }
    }

    public async Task<ProxyControlStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "control/status");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProxyControlStatus> ApplyModeAsync(string mode, CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Patch, "control/mode");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { mode }, JsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProxyControlStatus> ApplyPolicySnapshotAsync(
        string yaml,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Put, "control/policy-snapshot");
        request.Content = new StringContent(yaml, Encoding.UTF8, "application/x-yaml");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
        ProxyControlYarpConfigRequest requestBody,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Put, "control/yarp-config");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadYarpStatusAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string uri)
    {
        if (string.IsNullOrWhiteSpace(_options.ProxyControlToken))
        {
            throw new ProxyControlClientException(
                "Proxy control token is not configured.",
                "proxy_control_token_missing");
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ProxyControlToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                throw new ProxyControlClientException(
                    $"Proxy control returned status {statusCode}.",
                    "proxy_control_request_failed",
                    statusCode);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control request timed out.",
                "proxy_control_timeout",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control request failed.",
                "proxy_control_transport_error",
                innerException: ex);
        }
    }

    private static async Task<ProxyControlStatus> ReadStatusAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = document.RootElement;
            var mode = ReadRequiredString(root, "mode");
            var policyVersion = ReadRequiredString(root, "policyVersion");
            var serverCount = root.TryGetProperty("serverCount", out var serverCountElement)
                && serverCountElement.TryGetInt32(out var parsedServerCount)
                    ? parsedServerCount
                    : throw new JsonException("serverCount is required.");
            var routeVersion = root.TryGetProperty("routeVersion", out var routeVersionElement)
                && routeVersionElement.TryGetInt32(out var parsedRouteVersion)
                    ? parsedRouteVersion
                    : (int?)null;

            return new ProxyControlStatus(mode, policyVersion, serverCount, routeVersion);
        }
        catch (JsonException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control returned a malformed response.",
                "proxy_control_malformed_response",
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control response read failed.",
                "proxy_control_transport_error",
                innerException: ex);
        }
    }

    private static string ReadRequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JsonException($"{propertyName} is required.");

    private static async Task<ProxyControlYarpConfigStatus> ReadYarpStatusAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = document.RootElement;
            return new ProxyControlYarpConfigStatus(
                RouteVersion: ReadRequiredInt(root, "routeVersion"),
                RouteCount: ReadRequiredInt(root, "routeCount"),
                ClusterCount: ReadRequiredInt(root, "clusterCount"));
        }
        catch (JsonException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control returned a malformed response.",
                "proxy_control_malformed_response",
                innerException: ex);
        }
        catch (IOException ex)
        {
            throw new ProxyControlClientException(
                "Proxy control response read failed.",
                "proxy_control_transport_error",
                innerException: ex);
        }
    }

    private static int ReadRequiredInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : throw new JsonException($"{propertyName} is required.");
}

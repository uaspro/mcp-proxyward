namespace ProxyWard.Management.Application.Status;

public static class ComponentStatusValues
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Unhealthy = "unhealthy";
    public const string Unknown = "unknown";
}

public sealed record ComponentReport(
    string Status,
    string? Notes,
    IReadOnlyDictionary<string, object?>? Details);

public sealed record StatusComponents(
    ComponentReport ManagementApi,
    ComponentReport ProxyControl,
    ComponentReport AuditDb,
    ComponentReport SchemaLock,
    ComponentReport Telemetry);

public sealed record StatusResponse(
    string Status,
    string Service,
    StatusComponents Components);

public sealed record ProxyControlProbeResult(
    string Status,
    string? Notes,
    IReadOnlyDictionary<string, object?>? Details);

public sealed record ProxyControlStatus(
    string Mode,
    string PolicyVersion,
    int ServerCount,
    int? RouteVersion)
{
    public IReadOnlyDictionary<string, object?> ToDetails() =>
        new Dictionary<string, object?>
        {
            ["mode"] = Mode,
            ["policyVersion"] = PolicyVersion,
            ["serverCount"] = ServerCount,
            ["routeVersion"] = RouteVersion
        };
}

public sealed record ProxyControlYarpConfigRequest(
    IReadOnlyList<ProxyControlYarpRouteRequest> Routes,
    IReadOnlyList<ProxyControlYarpClusterRequest> Clusters);

public sealed record ProxyControlYarpRouteRequest(
    string RouteId,
    string ClusterId,
    int? Order,
    ProxyControlYarpRouteMatchRequest Match,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Transforms);

public sealed record ProxyControlYarpRouteMatchRequest(string Path);

public sealed record ProxyControlYarpClusterRequest(
    string ClusterId,
    IReadOnlyDictionary<string, ProxyControlYarpDestinationRequest> Destinations);

public sealed record ProxyControlYarpDestinationRequest(string Address);

public sealed record ProxyControlYarpConfigStatus(
    int RouteVersion,
    int RouteCount,
    int ClusterCount);

public sealed class ProxyControlClientException : Exception
{
    public ProxyControlClientException(string message, string error, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public string Error { get; }

    public int? StatusCode { get; }
}

namespace ProxyWard.Management.Application.Status;

public sealed class ManagementStatusService
{
    private const string ServiceName = "MCP ProxyWard Management API";
    private const string TelemetrySource = "audit-db";

    private readonly IManagementStatusStoreProbe _storeProbe;
    private readonly IProxyControlClient _proxyControlClient;

    public ManagementStatusService(
        IManagementStatusStoreProbe storeProbe,
        IProxyControlClient proxyControlClient)
    {
        _storeProbe = storeProbe ?? throw new ArgumentNullException(nameof(storeProbe));
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));
    }

    public async Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var managementApi = new ComponentReport(ComponentStatusValues.Healthy, Notes: null, Details: null);

        var (auditDb, schemaLock) = await _storeProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        ProxyControlProbeResult probeResult;
        try
        {
            probeResult = await _proxyControlClient.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probeResult = new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy probe failed (unexpected error)",
                Details: null);
        }

        var proxyControl = new ComponentReport(probeResult.Status, probeResult.Notes, probeResult.Details);

        var telemetry = new ComponentReport(
            auditDb.Status,
            Notes: null,
            Details: new Dictionary<string, object?> { ["source"] = TelemetrySource });

        var components = new StatusComponents(managementApi, proxyControl, auditDb, schemaLock, telemetry);
        var topStatus = AggregateTopStatus(components);

        return new StatusResponse(topStatus, ServiceName, components);
    }

    private static string AggregateTopStatus(StatusComponents components)
    {
        if (components.AuditDb.Status == ComponentStatusValues.Unhealthy)
        {
            return ComponentStatusValues.Unhealthy;
        }

        if (HasIssue(components.ManagementApi)
            || HasIssue(components.ProxyControl)
            || HasIssue(components.AuditDb)
            || HasIssue(components.SchemaLock)
            || HasIssue(components.Telemetry))
        {
            return ComponentStatusValues.Degraded;
        }

        return ComponentStatusValues.Healthy;
    }

    private static bool HasIssue(ComponentReport report) =>
        report.Status == ComponentStatusValues.Unhealthy || report.Status == ComponentStatusValues.Degraded;
}

public interface IManagementStatusStoreProbe
{
    Task<(ComponentReport AuditDb, ComponentReport SchemaLock)> ProbeAsync(CancellationToken cancellationToken);
}

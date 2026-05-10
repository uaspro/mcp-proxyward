using Microsoft.Data.Sqlite;

namespace ProxyWard.Management.Api.Status;

public sealed class ManagementStatusService
{
    private const string ServiceName = "MCP ProxyWard Management API";
    private const string TelemetrySource = "audit-db";

    private readonly ManagementApiOptions _options;
    private readonly IProxyControlClient _proxyControlClient;

    public ManagementStatusService(
        ManagementApiOptions options,
        IProxyControlClient proxyControlClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));
    }

    public async Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var managementApi = new ComponentReport(ComponentStatusValues.Healthy, Notes: null, Details: null);

        var (auditDb, schemaLock) = await ProbeSqliteAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task<(ComponentReport AuditDb, ComponentReport SchemaLock)> ProbeSqliteAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection? connection = null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(_options.AuditDatabasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT 1;";
                await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow disposal failure so the audit-DB report is what surfaces */ }
            }
            return (
                new ComponentReport(
                    ComponentStatusValues.Unhealthy,
                    Notes: "audit DB unreachable",
                    Details: new Dictionary<string, object?> { ["sqlitePath"] = _options.AuditDatabasePath }),
                new ComponentReport(
                    ComponentStatusValues.Unknown,
                    Notes: "audit DB unreachable",
                    Details: null));
        }

        var auditDb = new ComponentReport(
            ComponentStatusValues.Healthy,
            Notes: null,
            Details: new Dictionary<string, object?> { ["sqlitePath"] = _options.AuditDatabasePath });

        ComponentReport schemaLock;
        try
        {
            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = "SELECT COUNT(*) FROM tool_schema_versions;";
            var raw = await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var count = raw is null ? 0L : Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
            schemaLock = new ComponentReport(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: new Dictionary<string, object?> { ["trackedSnapshotCount"] = count });
        }
        catch (SqliteException ex) when (IsTableMissing(ex))
        {
            schemaLock = new ComponentReport(
                ComponentStatusValues.Unknown,
                Notes: "tool_schema_versions table not initialized",
                Details: null);
        }
        catch (SqliteException)
        {
            schemaLock = new ComponentReport(
                ComponentStatusValues.Unhealthy,
                Notes: "schema-lock read failed",
                Details: null);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        return (auditDb, schemaLock);
    }

    private static bool IsTableMissing(SqliteException ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

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

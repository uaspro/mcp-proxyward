using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Status;

namespace ProxyWard.Management.Infrastructure.Status;

public sealed class SqliteManagementStatusStoreProbe : IManagementStatusStoreProbe
{
    private readonly ManagementApiOptions _options;

    public SqliteManagementStatusStoreProbe(ManagementApiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<(ComponentReport AuditDb, ComponentReport SchemaLock)> ProbeAsync(CancellationToken cancellationToken)
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
                catch { /* keep the audit-DB report as the visible failure */ }
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
            var count = raw is null ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
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
}

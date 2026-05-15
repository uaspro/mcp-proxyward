using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Status;

namespace ProxyWard.Management.Infrastructure.Status;

public sealed class SqliteManagementStatusStoreProbe : IManagementStatusStoreProbe
{
    private const int BusyTimeoutMilliseconds = 5000;
    private const int CommandTimeoutSeconds = 2;

    private readonly ManagementApiOptions _options;

    public SqliteManagementStatusStoreProbe(ManagementApiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<(ComponentReport PersistenceDb, ComponentReport SchemaLock)> ProbeAsync(CancellationToken cancellationToken)
    {
        var database = _options.EffectivePersistenceDatabase;
        SqliteConnection? connection = null;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(database.SqlitePath!),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = CommandTimeoutSeconds
            }.ToString();

            connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandTimeout = CommandTimeoutSeconds;
                pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var probe = connection.CreateCommand())
            {
                probe.CommandTimeout = CommandTimeoutSeconds;
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
                    Notes: "persistence DB unreachable",
                    Details: PersistenceDetails(database)),
                new ComponentReport(
                    ComponentStatusValues.Unknown,
                    Notes: "persistence DB unreachable",
                    Details: null));
        }

        var persistenceDb = new ComponentReport(
            ComponentStatusValues.Healthy,
            Notes: null,
            Details: PersistenceDetails(database));
        var openedConnection = connection
            ?? throw new InvalidOperationException("SQLite connection was not opened.");

        ComponentReport schemaLock;
        try
        {
            if (!await TableExistsAsync(openedConnection, "tool_schema_versions", cancellationToken).ConfigureAwait(false))
            {
                schemaLock = new ComponentReport(
                    ComponentStatusValues.Unknown,
                    Notes: "tool_schema_versions table not initialized",
                    Details: null);

                return (persistenceDb, schemaLock);
            }

            await using var schemaCommand = openedConnection.CreateCommand();
            schemaCommand.CommandTimeout = CommandTimeoutSeconds;
            schemaCommand.CommandText = "SELECT COUNT(*) FROM tool_schema_versions;";
            var raw = await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var count = raw is null ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            schemaLock = new ComponentReport(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: new Dictionary<string, object?> { ["trackedSnapshotCount"] = count });
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
            await openedConnection.DisposeAsync().ConfigureAwait(false);
        }

        return (persistenceDb, schemaLock);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static IReadOnlyDictionary<string, object?> PersistenceDetails(
        ProxyWard.Core.Persistence.PersistenceDatabaseOptions database) =>
        new Dictionary<string, object?>
        {
            ["provider"] = database.ProviderName,
            ["source"] = database.SourceDescription
        };
}

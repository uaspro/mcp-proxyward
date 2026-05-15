using System.Globalization;
using Npgsql;
using ProxyWard.Management.Application.Status;

namespace ProxyWard.Management.Infrastructure.Status;

public sealed class PostgresManagementStatusStoreProbe : IManagementStatusStoreProbe, IAsyncDisposable, IDisposable
{
    private const int CommandTimeoutSeconds = 2;

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgresManagementStatusStoreProbe(string connectionString)
        : this(CreateDataSource(connectionString), ownsDataSource: true)
    {
    }

    public PostgresManagementStatusStoreProbe(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresManagementStatusStoreProbe(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public async Task<(ComponentReport PersistenceDb, ComponentReport SchemaLock)> ProbeAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var probe = connection.CreateCommand();
            probe.CommandTimeout = CommandTimeoutSeconds;
            probe.CommandText = "SELECT 1;";
            await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }

            return (
                new ComponentReport(
                    ComponentStatusValues.Unhealthy,
                    Notes: "persistence DB unreachable",
                    Details: PersistenceDetails()),
                new ComponentReport(
                    ComponentStatusValues.Unknown,
                    Notes: "persistence DB unreachable",
                    Details: null));
        }

        var persistenceDb = new ComponentReport(
            ComponentStatusValues.Healthy,
            Notes: null,
            Details: PersistenceDetails());

        ComponentReport schemaLock;
        try
        {
            if (!await TableExistsAsync(connection, "tool_schema_versions", cancellationToken).ConfigureAwait(false))
            {
                schemaLock = new ComponentReport(
                    ComponentStatusValues.Unknown,
                    Notes: "tool_schema_versions table not initialized",
                    Details: null);

                return (persistenceDb, schemaLock);
            }

            await using var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandTimeout = CommandTimeoutSeconds;
            schemaCommand.CommandText = "SELECT COUNT(*) FROM tool_schema_versions;";
            var raw = await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var count = raw is null ? 0L : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            schemaLock = new ComponentReport(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: new Dictionary<string, object?> { ["trackedSnapshotCount"] = count });
        }
        catch (NpgsqlException)
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

        return (persistenceDb, schemaLock);
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL;";
        command.Parameters.AddWithValue("table_name", tableName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static IReadOnlyDictionary<string, object?> PersistenceDetails() =>
        new Dictionary<string, object?>
        {
            ["provider"] = "postgres",
            ["source"] = "postgresql:#proxyward"
        };

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }

        return NpgsqlDataSource.Create(connectionString);
    }
}

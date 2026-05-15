using System.Text.Json;
using Npgsql;
using ProxyWard.Management.Application.Tools;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.Management.Infrastructure.Tools;

public sealed class PostgresManagementToolInventoryRepository : IManagementToolInventoryRepository, IAsyncDisposable, IDisposable
{
    private static readonly IReadOnlyDictionary<string, int> DriftStatusPriority =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["clean"] = 0,
            ["approved"] = 1,
            ["rejected"] = 2,
            ["blocked"] = 3,
            ["pending"] = 4
        };

    private readonly NpgsqlDataSource _dataSource;
    private readonly IPolicyStore _policyStore;
    private readonly bool _ownsDataSource;
    private bool _disposed;

    public PostgresManagementToolInventoryRepository(
        string connectionString,
        IPolicyStore policyStore)
        : this(CreateDataSource(connectionString), policyStore, ownsDataSource: true)
    {
    }

    public PostgresManagementToolInventoryRepository(
        NpgsqlDataSource dataSource,
        IPolicyStore policyStore)
        : this(dataSource, policyStore, ownsDataSource: false)
    {
    }

    private PostgresManagementToolInventoryRepository(
        NpgsqlDataSource dataSource,
        IPolicyStore policyStore,
        bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
        _ownsDataSource = ownsDataSource;
    }

    public async Task<ManagementToolInventoryResponse> GetAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var servers = new SortedDictionary<string, ManagementToolInventoryServer>(StringComparer.Ordinal);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (await TableExistsAsync(connection, "tool_schema_versions", cancellationToken).ConfigureAwait(false))
        {
            var driftStatuses = await ReadDriftStatusesAsync(connection, cancellationToken).ConfigureAwait(false);
            foreach (var server in await ReadLatestSchemaHistoryAsync(
                connection,
                driftStatuses,
                cancellationToken).ConfigureAwait(false))
            {
                servers[server.ServerId] = server;
            }
        }

        foreach (var serverId in await ReadConfiguredServerIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!servers.ContainsKey(serverId))
            {
                servers[serverId] = new ManagementToolInventoryServer(
                    ServerId: serverId,
                    LatestVersion: null,
                    DriftStatus: "unobserved",
                    Tools: []);
            }
        }

        return new ManagementToolInventoryResponse(servers.Values.ToArray());
    }

    private static async Task<IReadOnlyList<ManagementToolInventoryServer>> ReadLatestSchemaHistoryAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<DriftKey, string> driftStatuses,
        CancellationToken cancellationToken)
    {
        var servers = new List<ManagementToolInventoryServer>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current.server_id, current.version, current.fingerprints
            FROM tool_schema_versions current
            INNER JOIN (
                SELECT server_id, MAX(version) AS latest_version
                FROM tool_schema_versions
                GROUP BY server_id
            ) latest
              ON latest.server_id = current.server_id
             AND latest.latest_version = current.version
            ORDER BY current.server_id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var serverId = reader.GetString(0);
            var latestVersion = reader.GetInt32(1);
            var tools = ParseTools(serverId, latestVersion, reader.GetString(2), driftStatuses);
            var serverStatus = tools.Count == 0
                ? "clean"
                : tools.Select(tool => tool.DriftStatus).Aggregate("clean", SelectHigherPriorityStatus);

            servers.Add(new ManagementToolInventoryServer(
                ServerId: serverId,
                LatestVersion: latestVersion,
                DriftStatus: serverStatus,
                Tools: tools));
        }

        return servers;
    }

    private static async Task<IReadOnlyDictionary<DriftKey, string>> ReadDriftStatusesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "schema_drift_reviews", cancellationToken).ConfigureAwait(false))
        {
            return new Dictionary<DriftKey, string>();
        }

        var statuses = new Dictionary<DriftKey, string>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, tool_name, status
            FROM schema_drift_reviews;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new DriftKey(reader.GetString(0), reader.GetString(1));
            var status = NormalizeDriftStatus(reader.GetString(2));
            statuses[key] = statuses.TryGetValue(key, out var existing)
                ? SelectHigherPriorityStatus(existing, status)
                : status;
        }

        return statuses;
    }

    private async Task<IReadOnlyCollection<string>> ReadConfiguredServerIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _policyStore.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            return (snapshot?.Policy.Servers.Keys ?? [])
                .OrderBy(serverId => serverId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ManagementToolInventoryTool> ParseTools(
        string serverId,
        int latestVersion,
        string fingerprintsJson,
        IReadOnlyDictionary<DriftKey, string> driftStatuses)
    {
        using var document = JsonDocument.Parse(fingerprintsJson);
        if (!document.RootElement.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var tools = new List<ManagementToolInventoryTool>();
        foreach (var element in toolsElement.EnumerateArray())
        {
            var name = ReadOptionalString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = new DriftKey(serverId, name);
            var driftStatus = driftStatuses.TryGetValue(key, out var status) ? status : "clean";
            tools.Add(new ManagementToolInventoryTool(
                Name: name,
                LatestVersion: latestVersion,
                DriftStatus: driftStatus,
                Title: ReadOptionalString(element, "title"),
                Description: ReadOptionalString(element, "description"),
                NameHash: ReadOptionalString(element, "nameHash"),
                TitleHash: ReadOptionalString(element, "titleHash"),
                DescriptionHash: ReadOptionalString(element, "descriptionHash"),
                InputSchemaHash: ReadOptionalString(element, "inputSchemaHash"),
                OutputSchemaHash: ReadOptionalString(element, "outputSchemaHash")));
        }

        return tools
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.GetString();
    }

    private static string NormalizeDriftStatus(string status) =>
        status.Trim().ToLowerInvariant() switch
        {
            "pending" => "pending",
            "blocked" => "blocked",
            "rejected" => "rejected",
            "approved" => "approved",
            _ => "clean"
        };

    private static string SelectHigherPriorityStatus(string left, string right)
    {
        var leftPriority = DriftStatusPriority.TryGetValue(left, out var parsedLeft) ? parsedLeft : 0;
        var rightPriority = DriftStatusPriority.TryGetValue(right, out var parsedRight) ? parsedRight : 0;
        return rightPriority > leftPriority ? right : left;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL;";
        command.Parameters.AddWithValue("table_name", tableName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgresManagementToolInventoryRepository));
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

    private readonly record struct DriftKey(string ServerId, string ToolName);
}

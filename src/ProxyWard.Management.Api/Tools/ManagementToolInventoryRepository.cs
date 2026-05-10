using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Tools;

public sealed class ManagementToolInventoryRepository
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

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string _policyPath;

    public ManagementToolInventoryRepository(ManagementApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _databasePath = Path.GetFullPath(options.AuditDatabasePath);
        _policyPath = options.PolicyPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<ManagementToolInventoryResponse> GetAsync(CancellationToken cancellationToken)
    {
        var servers = new SortedDictionary<string, ManagementToolInventoryServer>(StringComparer.Ordinal);

        if (File.Exists(_databasePath))
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

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
        SqliteConnection connection,
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
        SqliteConnection connection,
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
        if (!File.Exists(_policyPath))
        {
            return [];
        }

        try
        {
            var yaml = await File.ReadAllTextAsync(_policyPath, cancellationToken).ConfigureAwait(false);
            var policy = ProxyWardPolicyLoader.Load(yaml);
            return policy.Servers.Keys
                .OrderBy(serverId => serverId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or PolicyValidationException or UnauthorizedAccessException)
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

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

    private readonly record struct DriftKey(string ServerId, string ToolName);
}

public sealed record ManagementToolInventoryResponse(
    IReadOnlyList<ManagementToolInventoryServer> Servers);

public sealed record ManagementToolInventoryServer(
    string ServerId,
    int? LatestVersion,
    string DriftStatus,
    IReadOnlyList<ManagementToolInventoryTool> Tools);

public sealed record ManagementToolInventoryTool(
    string Name,
    int LatestVersion,
    string DriftStatus,
    string? Title,
    string? Description,
    string? NameHash,
    string? TitleHash,
    string? DescriptionHash,
    string? InputSchemaHash,
    string? OutputSchemaHash);

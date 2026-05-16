using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application.Audit;

namespace ProxyWard.Management.Infrastructure.Audit;

public sealed class ManagementAuditEventRepository : IManagementAuditEventRepository
{
    private readonly string _sqlitePath;
    private readonly string _connectionString;
    private readonly ManagementAuditReadOptions _options;

    public ManagementAuditEventRepository(
        string sqlitePath,
        ManagementAuditReadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new ArgumentException("sqlitePath is required.", nameof(sqlitePath));
        }

        _options = options ?? new ManagementAuditReadOptions();
        if (_options.MaxPageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPageSize must be greater than zero.");
        }

        if (_options.MaxExportRowCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxExportRowCount must be greater than zero.");
        }

        _sqlitePath = Path.GetFullPath(sqlitePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<ManagementAuditEventPage> QueryAsync(
        ManagementAuditEventQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = NormalizePageSize(query.PageSize);
        var offset = Math.Max(0, query.Offset);
        var filter = BuildFilter(query);

        if (!File.Exists(_sqlitePath))
        {
            return EmptyPage(offset, pageSize);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteAuditSchema.ConfigureReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!await AuditEventsTableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return EmptyPage(offset, pageSize);
        }

        var totalCount = await CountAsync(connection, filter, cancellationToken).ConfigureAwait(false);
        var items = await ReadItemsAsync(connection, filter, offset, pageSize, cancellationToken).ConfigureAwait(false);

        return new ManagementAuditEventPage(offset, pageSize, totalCount, items);
    }

    public async IAsyncEnumerable<ManagementAuditEventItem> StreamAsync(
        ManagementAuditEventQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rowCap = _options.MaxExportRowCount;
        var filter = BuildFilter(query);

        if (!File.Exists(_sqlitePath))
        {
            yield break;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteAuditSchema.ConfigureReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!await AuditEventsTableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            yield break;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                id,
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
            FROM audit_events
            {filter.WhereClause}
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit;
            """;
        AddParameters(command, filter.Parameters);
        command.Parameters.AddWithValue("$limit", rowCap);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadItem(reader);
        }
    }

    public async Task<ManagementAuditEventItem?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return null;
        }

        if (!File.Exists(_sqlitePath))
        {
            return null;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteAuditSchema.ConfigureReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!await AuditEventsTableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var filter = new SqlFilter(
            " WHERE id = $id",
            [new SqlParameterValue("$id", id)]);
        var items = await ReadItemsAsync(connection, filter, offset: 0, pageSize: 1, cancellationToken).ConfigureAwait(false);
        return items.Count == 0 ? null : items[0];
    }

    private static ManagementAuditEventPage EmptyPage(int offset, int pageSize) =>
        new(offset, pageSize, TotalCount: 0, Items: []);

    private int NormalizePageSize(int requestedPageSize)
    {
        var requested = requestedPageSize <= 0 ? 50 : requestedPageSize;
        return Math.Min(requested, _options.MaxPageSize);
    }

    private static async Task<bool> AuditEventsTableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'audit_events'
            LIMIT 1;
            """;

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqlFilter filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM audit_events{filter.WhereClause};";
        AddParameters(command, filter.Parameters);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<ManagementAuditEventItem>> ReadItemsAsync(
        SqliteConnection connection,
        SqlFilter filter,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                id,
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
            FROM audit_events
            {filter.WhereClause}
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddParameters(command, filter.Parameters);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<ManagementAuditEventItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static ManagementAuditEventItem ReadItem(SqliteDataReader reader)
    {
        return new ManagementAuditEventItem(
            Id: reader.GetInt64(0),
            TimestampUtc: ReadTimestampOrUnixEpoch(reader, 1),
            EventType: ReadTextOrFallback(reader, 2, "unknown"),
            Mode: ReadTextOrFallback(reader, 3, "unknown"),
            Decision: ReadTextOrFallback(reader, 4, "unknown"),
            ServerId: ReadTextOrFallback(reader, 5, "unknown"),
            Method: ReadOptionalText(reader, 6),
            ToolName: ReadOptionalText(reader, 7),
            Reasons: SplitReasons(ReadOptionalText(reader, 8)),
            PolicyVersion: ReadTextOrFallback(reader, 9, "unknown"),
            CorrelationId: ReadTextOrFallback(reader, 10, "unknown"),
            RequestBytes: ReadInt64OrZero(reader, 11),
            DurationMs: ReadInt64OrZero(reader, 12),
            ArgumentSummary: ReadArgumentSummary(ReadOptionalText(reader, 13)));
    }

    private static IReadOnlyList<string> SplitReasons(string? reasons) =>
        string.IsNullOrWhiteSpace(reasons)
            ? []
            : reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadOptionalText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string ReadTextOrFallback(SqliteDataReader reader, int ordinal, string fallback) =>
        ReadOptionalText(reader, ordinal) ?? fallback;

    private static long ReadInt64OrZero(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0L : reader.GetInt64(ordinal);

    private static DateTimeOffset ReadTimestampOrUnixEpoch(SqliteDataReader reader, int ordinal)
    {
        var raw = ReadOptionalText(reader, ordinal);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;
    }

    private static JsonNode? ReadArgumentSummary(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonNode.Parse(payloadJson);
            return payload?["argumentSummary"]?.DeepClone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SqlFilter BuildFilter(ManagementAuditEventQuery query)
    {
        var where = new List<string>();
        var parameters = new List<SqlParameterValue>();

        AddDateFilter(where, parameters, "timestamp_utc", ">=", "$from_utc", query.FromUtc);
        AddDateFilter(where, parameters, "timestamp_utc", "<=", "$to_utc", query.ToUtc);
        AddTextFilter(where, parameters, "decision", "$decision", query.Decision);
        AddTextFilter(where, parameters, "server_id", "$server_id", query.ServerId);
        AddTextFilter(where, parameters, "method", "$method", query.Method);
        AddTextFilter(where, parameters, "tool_name", "$tool_name", query.ToolName);
        AddTextFilter(where, parameters, "correlation_id", "$correlation_id", query.CorrelationId);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            where.Add("""
                (
                    payload_json LIKE $search
                    OR reasons LIKE $search
                    OR server_id LIKE $search
                    OR method LIKE $search
                    OR tool_name LIKE $search
                    OR correlation_id LIKE $search
                )
                """);
            parameters.Add(new SqlParameterValue("$search", $"%{query.SearchText.Trim()}%"));
        }

        return new SqlFilter(
            where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where),
            parameters);
    }

    private static void AddDateFilter(
        List<string> where,
        List<SqlParameterValue> parameters,
        string column,
        string op,
        string parameterName,
        DateTimeOffset? value)
    {
        if (value is null)
        {
            return;
        }

        where.Add($"{column} {op} {parameterName}");
        parameters.Add(new SqlParameterValue(
            parameterName,
            value.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)));
    }

    private static void AddTextFilter(
        List<string> where,
        List<SqlParameterValue> parameters,
        string column,
        string parameterName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        where.Add($"{column} = {parameterName}");
        parameters.Add(new SqlParameterValue(parameterName, value.Trim()));
    }

    private static void AddParameters(
        SqliteCommand command,
        IReadOnlyList<SqlParameterValue> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private sealed record SqlFilter(
        string WhereClause,
        IReadOnlyList<SqlParameterValue> Parameters);

    private sealed record SqlParameterValue(string Name, object Value);
}

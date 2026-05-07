using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ProxyWard.Management.Api.Audit;

public sealed record ManagementAuditReadOptions(
    int MaxPageSize = 200,
    int MaxExportRowCount = 50_000,
    int MaxOverviewSampleSize = 100_000);

public sealed record ManagementAuditEventQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Decision = null,
    string? ServerId = null,
    string? Method = null,
    string? ToolName = null,
    string? CorrelationId = null,
    string? SearchText = null,
    int Offset = 0,
    int PageSize = 50);

public sealed record ManagementAuditEventPage(
    int Offset,
    int PageSize,
    long TotalCount,
    IReadOnlyList<ManagementAuditEventItem> Items);

public sealed record ManagementAuditEventItem(
    long Id,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Mode,
    string Decision,
    string ServerId,
    string? Method,
    string? ToolName,
    IReadOnlyList<string> Reasons,
    string PolicyVersion,
    string CorrelationId,
    long RequestBytes,
    long DurationMs,
    JsonNode? ArgumentSummary);

public sealed class ManagementAuditEventRepository
{
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

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sqlitePath),
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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

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

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        var filter = new SqlFilter(
            " WHERE id = $id",
            [new SqlParameterValue("$id", id)]);
        var items = await ReadItemsAsync(connection, filter, offset: 0, pageSize: 1, cancellationToken).ConfigureAwait(false);
        return items.Count == 0 ? null : items[0];
    }

    private int NormalizePageSize(int requestedPageSize)
    {
        var requested = requestedPageSize <= 0 ? 50 : requestedPageSize;
        return Math.Min(requested, _options.MaxPageSize);
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        var payload = JsonNode.Parse(reader.GetString(13));
        var argumentSummary = payload?["argumentSummary"]?.DeepClone();

        return new ManagementAuditEventItem(
            Id: reader.GetInt64(0),
            TimestampUtc: DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            EventType: reader.GetString(2),
            Mode: reader.GetString(3),
            Decision: reader.GetString(4),
            ServerId: reader.GetString(5),
            Method: reader.IsDBNull(6) ? null : reader.GetString(6),
            ToolName: reader.IsDBNull(7) ? null : reader.GetString(7),
            Reasons: SplitReasons(reader.GetString(8)),
            PolicyVersion: reader.GetString(9),
            CorrelationId: reader.GetString(10),
            RequestBytes: reader.GetInt64(11),
            DurationMs: reader.GetInt64(12),
            ArgumentSummary: argumentSummary);
    }

    private static IReadOnlyList<string> SplitReasons(string reasons) =>
        reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

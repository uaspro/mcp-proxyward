using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Api.Audit;

namespace ProxyWard.Management.Api.Drift;

public sealed record ManagementSchemaDriftQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Status = null,
    string? ServerId = null,
    string? ToolName = null,
    int Offset = 0,
    int PageSize = 50);

public sealed record ManagementSchemaDriftWindow(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public sealed record ManagementSchemaDriftPage(
    int Offset,
    int PageSize,
    long TotalCount,
    ManagementSchemaDriftWindow Window,
    IReadOnlyList<ManagementSchemaDriftItem> Items);

public sealed record ManagementSchemaDriftItem(
    long Id,
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    string Status,
    IReadOnlyList<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote,
    long ImpactCount,
    bool HasDiffMetadata,
    string DiffMode);

public sealed record ManagementSchemaDriftDetail(
    long Id,
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    string Status,
    IReadOnlyList<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote,
    long ImpactCount,
    bool HasDiffMetadata,
    string DiffMode,
    ManagementSchemaDriftDiff Diff);

public sealed record ManagementSchemaDriftDiff(
    string? BeforeJson,
    string? AfterJson,
    string BeforeHash,
    string AfterHash,
    DateTimeOffset? CreatedAtUtc,
    string Mode);

public sealed class ManagementSchemaDriftRepository
{
    public const string DiffModeMetadata = "metadata";
    public const string DiffModeHash = "hash";
    public const string UnavailableHash = "unavailable";

    private readonly string _connectionString;
    private readonly ManagementAuditReadOptions _options;

    public ManagementSchemaDriftRepository(
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

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sqlitePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<ManagementSchemaDriftPage> QueryAsync(
        ManagementSchemaDriftQuery query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeQuery(query);
        var pageSize = NormalizePageSize(normalizedQuery.PageSize);
        var offset = Math.Max(0, normalizedQuery.Offset);
        var filter = BuildFilter(normalizedQuery);
        var impactFilter = BuildImpactFilter(normalizedQuery);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        var diffMetadataTableExists = await TableExistsAsync(
            connection,
            "tool_schema_diff_metadata",
            cancellationToken).ConfigureAwait(false);
        var totalCount = await CountAsync(connection, filter, cancellationToken).ConfigureAwait(false);
        var items = await ReadItemsAsync(
            connection,
            filter,
            impactFilter,
            diffMetadataTableExists,
            offset,
            pageSize,
            cancellationToken).ConfigureAwait(false);

        return new ManagementSchemaDriftPage(
            offset,
            pageSize,
            totalCount,
            new ManagementSchemaDriftWindow(normalizedQuery.FromUtc, normalizedQuery.ToUtc),
            items);
    }

    public async Task<ManagementSchemaDriftDetail?> GetByIdAsync(
        long id,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return null;
        }

        var query = new ManagementSchemaDriftQuery(FromUtc: fromUtc, ToUtc: toUtc);
        var impactFilter = BuildImpactFilter(query);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        var diffMetadataTableExists = await TableExistsAsync(
            connection,
            "tool_schema_diff_metadata",
            cancellationToken).ConfigureAwait(false);

        var diffJoin = diffMetadataTableExists
            ? "LEFT JOIN tool_schema_diff_metadata d ON d.drift_review_id = r.id"
            : string.Empty;
        var diffColumns = diffMetadataTableExists
            ? """
                d.id,
                d.before_json,
                d.after_json,
                d.before_hash,
                d.after_hash,
                d.created_at_utc,
                """
            : """
                NULL AS diff_id,
                NULL AS before_json,
                NULL AS after_json,
                NULL AS before_hash,
                NULL AS after_hash,
                NULL AS diff_created_at_utc,
                """;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                r.id,
                r.server_id,
                r.tool_name,
                r.field_name,
                r.from_version,
                r.to_version,
                r.status,
                r.reasons,
                r.policy_version,
                r.detected_at_utc,
                r.reviewed_at_utc,
                r.reviewed_by,
                r.review_note,
                {diffColumns}
                (
                    SELECT COUNT(*)
                    FROM schema_drift_reviews impact
                    WHERE impact.server_id = r.server_id
                      AND impact.tool_name = r.tool_name
                      {impactFilter.WhereClause}
                ) AS impact_count
            FROM schema_drift_reviews r
            {diffJoin}
            WHERE r.id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        AddParameters(command, impactFilter.Parameters);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadDetail(reader);
    }

    private int NormalizePageSize(int requestedPageSize)
    {
        var requested = requestedPageSize <= 0 ? 50 : requestedPageSize;
        return Math.Min(requested, _options.MaxPageSize);
    }

    private static ManagementSchemaDriftQuery NormalizeQuery(ManagementSchemaDriftQuery query) =>
        query with
        {
            Status = NormalizeText(query.Status),
            ServerId = NormalizeText(query.ServerId),
            ToolName = NormalizeText(query.ToolName)
        };

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqlFilter filter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM schema_drift_reviews r{filter.WhereClause};";
        AddParameters(command, filter.Parameters);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<ManagementSchemaDriftItem>> ReadItemsAsync(
        SqliteConnection connection,
        SqlFilter filter,
        SqlFilter impactFilter,
        bool diffMetadataTableExists,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var diffJoin = diffMetadataTableExists
            ? "LEFT JOIN tool_schema_diff_metadata d ON d.drift_review_id = r.id"
            : string.Empty;
        var diffColumns = diffMetadataTableExists
            ? """
                d.id,
                d.before_json,
                d.after_json,
                d.before_hash,
                d.after_hash,
                d.created_at_utc,
                """
            : """
                NULL AS diff_id,
                NULL AS before_json,
                NULL AS after_json,
                NULL AS before_hash,
                NULL AS after_hash,
                NULL AS diff_created_at_utc,
                """;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                r.id,
                r.server_id,
                r.tool_name,
                r.field_name,
                r.from_version,
                r.to_version,
                r.status,
                r.reasons,
                r.policy_version,
                r.detected_at_utc,
                r.reviewed_at_utc,
                r.reviewed_by,
                r.review_note,
                {diffColumns}
                (
                    SELECT COUNT(*)
                    FROM schema_drift_reviews impact
                    WHERE impact.server_id = r.server_id
                      AND impact.tool_name = r.tool_name
                      {impactFilter.WhereClause}
                ) AS impact_count
            FROM schema_drift_reviews r
            {diffJoin}
            {filter.WhereClause}
            ORDER BY r.detected_at_utc DESC, r.id DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddParameters(command, filter.Parameters);
        AddParameters(command, impactFilter.Parameters);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<ManagementSchemaDriftItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static ManagementSchemaDriftItem ReadItem(SqliteDataReader reader)
    {
        var hasDiffMetadata = !reader.IsDBNull(13);
        var beforeJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        var afterJson = reader.IsDBNull(15) ? null : reader.GetString(15);
        var diffMode = beforeJson is not null || afterJson is not null
            ? DiffModeMetadata
            : DiffModeHash;

        return new ManagementSchemaDriftItem(
            Id: reader.GetInt64(0),
            ServerId: reader.GetString(1),
            ToolName: reader.GetString(2),
            FieldName: reader.GetString(3),
            FromVersion: reader.GetInt32(4),
            ToVersion: reader.GetInt32(5),
            Status: reader.GetString(6),
            Reasons: SplitReasons(reader.GetString(7)),
            PolicyVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            DetectedAtUtc: ParseTimestamp(reader.GetString(9)),
            ReviewedAtUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
            ReviewedBy: reader.IsDBNull(11) ? null : reader.GetString(11),
            ReviewNote: reader.IsDBNull(12) ? null : reader.GetString(12),
            ImpactCount: reader.GetInt64(19),
            HasDiffMetadata: hasDiffMetadata,
            DiffMode: diffMode);
    }

    private static ManagementSchemaDriftDetail ReadDetail(SqliteDataReader reader)
    {
        var item = ReadItem(reader);
        var beforeJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        var afterJson = reader.IsDBNull(15) ? null : reader.GetString(15);
        var beforeHash = reader.IsDBNull(16) ? UnavailableHash : reader.GetString(16);
        var afterHash = reader.IsDBNull(17) ? UnavailableHash : reader.GetString(17);
        var createdAtUtc = reader.IsDBNull(18)
            ? (DateTimeOffset?)null
            : ParseTimestamp(reader.GetString(18));

        return new ManagementSchemaDriftDetail(
            item.Id,
            item.ServerId,
            item.ToolName,
            item.FieldName,
            item.FromVersion,
            item.ToVersion,
            item.Status,
            item.Reasons,
            item.PolicyVersion,
            item.DetectedAtUtc,
            item.ReviewedAtUtc,
            item.ReviewedBy,
            item.ReviewNote,
            item.ImpactCount,
            item.HasDiffMetadata,
            item.DiffMode,
            new ManagementSchemaDriftDiff(
                beforeJson,
                afterJson,
                beforeHash,
                afterHash,
                createdAtUtc,
                item.DiffMode));
    }

    private static IReadOnlyList<string> SplitReasons(string reasons) =>
        reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DateTimeOffset ParseTimestamp(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqlFilter BuildFilter(ManagementSchemaDriftQuery query)
    {
        var where = new List<string>();
        var parameters = new List<SqlParameterValue>();

        AddDateFilter(where, parameters, "r.detected_at_utc", ">=", "$from_utc", query.FromUtc);
        AddDateFilter(where, parameters, "r.detected_at_utc", "<=", "$to_utc", query.ToUtc);
        AddTextFilter(where, parameters, "r.status", "$status", query.Status);
        AddTextFilter(where, parameters, "r.server_id", "$server_id", query.ServerId);
        AddTextFilter(where, parameters, "r.tool_name", "$tool_name", query.ToolName);

        return new SqlFilter(
            where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where),
            parameters);
    }

    private static SqlFilter BuildImpactFilter(ManagementSchemaDriftQuery query)
    {
        var where = new List<string>();
        var parameters = new List<SqlParameterValue>();

        AddDateFilter(where, parameters, "impact.detected_at_utc", ">=", "$impact_from_utc", query.FromUtc);
        AddDateFilter(where, parameters, "impact.detected_at_utc", "<=", "$impact_to_utc", query.ToUtc);

        return new SqlFilter(
            where.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", where),
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

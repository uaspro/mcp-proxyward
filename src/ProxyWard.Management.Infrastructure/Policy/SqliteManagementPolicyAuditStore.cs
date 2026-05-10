using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Policy;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class SqliteManagementPolicyAuditStore : IManagementPolicyAuditStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteManagementPolicyAuditStore(ManagementApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _databasePath = Path.GetFullPath(options.AuditDatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<IReadOnlyList<ManagementPolicyModeImpactItem>> ReadModeImpactAsync(
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken)
    {
        var affected = new Dictionary<ImpactKey, ImpactAccumulator>();
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(readConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (await TableExistsAsync(connection, "audit_events", cancellationToken).ConfigureAwait(false))
        {
            await ReadWouldBlockImpactAsync(connection, window, affected, cancellationToken).ConfigureAwait(false);
        }

        if (await TableExistsAsync(connection, "schema_drift_reviews", cancellationToken).ConfigureAwait(false))
        {
            await ReadDriftImpactAsync(connection, window, affected, cancellationToken).ConfigureAwait(false);
        }

        return affected.Values
            .Select(item => item.ToItem())
            .OrderBy(item => item.ServerId, StringComparer.Ordinal)
            .ThenBy(item => item.ToolName ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task WriteAsync(
        ManagementPolicyAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureAuditSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (
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
            ) VALUES (
                $timestamp_utc,
                $event_type,
                $mode,
                $decision,
                $server_id,
                $method,
                $tool_name,
                $reasons,
                $policy_version,
                $correlation_id,
                $request_bytes,
                $duration_ms,
                $payload_json
            );
            """;
        command.Parameters.AddWithValue("$timestamp_utc", FormatTimestamp(auditEvent.TimestampUtc));
        command.Parameters.AddWithValue("$event_type", auditEvent.EventType);
        command.Parameters.AddWithValue("$mode", "management");
        command.Parameters.AddWithValue("$decision", "allow");
        command.Parameters.AddWithValue("$server_id", "management");
        command.Parameters.AddWithValue("$method", auditEvent.Method);
        command.Parameters.AddWithValue("$tool_name", DBNull.Value);
        command.Parameters.AddWithValue("$reasons", auditEvent.Reasons);
        command.Parameters.AddWithValue("$policy_version", auditEvent.PolicyVersion);
        command.Parameters.AddWithValue("$correlation_id", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("$request_bytes", 0);
        command.Parameters.AddWithValue("$duration_ms", 0);
        command.Parameters.AddWithValue("$payload_json", auditEvent.PayloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadWouldBlockImpactAsync(
        SqliteConnection connection,
        ManagementPolicyImpactWindow window,
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, tool_name, COUNT(*), GROUP_CONCAT(reasons, ',')
            FROM audit_events
            WHERE decision = 'would_block'
              AND timestamp_utc >= $from_utc
              AND timestamp_utc <= $to_utc
            GROUP BY server_id, tool_name;
            """;
        AddWindowParameters(command, window);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new ImpactKey(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            var accumulator = GetOrAdd(affected, key);
            accumulator.WouldBlockCount += reader.GetInt64(2);
            accumulator.AddReasons(reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    private static async Task ReadDriftImpactAsync(
        SqliteConnection connection,
        ManagementPolicyImpactWindow window,
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                server_id,
                tool_name,
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END) AS pending_count,
                COUNT(*) AS unapproved_count,
                GROUP_CONCAT(reasons, ',')
            FROM schema_drift_reviews
            WHERE status IN ('pending', 'rejected', 'blocked')
              AND detected_at_utc >= $from_utc
              AND detected_at_utc <= $to_utc
            GROUP BY server_id, tool_name;
            """;
        AddWindowParameters(command, window);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new ImpactKey(reader.GetString(0), reader.GetString(1));
            var accumulator = GetOrAdd(affected, key);
            accumulator.PendingDriftCount += reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            accumulator.UnapprovedDriftCount += reader.GetInt64(3);
            accumulator.AddReasons(reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    private static ImpactAccumulator GetOrAdd(
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        ImpactKey key)
    {
        if (!affected.TryGetValue(key, out var accumulator))
        {
            accumulator = new ImpactAccumulator(key.ServerId, key.ToolName);
            affected[key] = accumulator;
        }

        return accumulator;
    }

    private static async Task ConfigureReadConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
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

    private static async Task EnsureAuditSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                mode TEXT NOT NULL,
                decision TEXT NOT NULL,
                server_id TEXT NOT NULL,
                method TEXT NULL,
                tool_name TEXT NULL,
                reasons TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                request_bytes INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_events_timestamp ON audit_events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_audit_events_decision ON audit_events(decision);
            CREATE INDEX IF NOT EXISTS idx_audit_events_server_id ON audit_events(server_id);
            CREATE INDEX IF NOT EXISTS idx_audit_events_method ON audit_events(method);
            CREATE INDEX IF NOT EXISTS idx_audit_events_tool_name ON audit_events(tool_name);
            CREATE INDEX IF NOT EXISTS idx_audit_events_reasons ON audit_events(reasons);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddWindowParameters(SqliteCommand command, ManagementPolicyImpactWindow window)
    {
        command.Parameters.AddWithValue("$from_utc", FormatTimestamp(window.FromUtc));
        command.Parameters.AddWithValue("$to_utc", FormatTimestamp(window.ToUtc));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private sealed record ImpactKey(string ServerId, string? ToolName);

    private sealed class ImpactAccumulator
    {
        private readonly SortedSet<string> _reasons = new(StringComparer.Ordinal);

        public ImpactAccumulator(string serverId, string? toolName)
        {
            ServerId = serverId;
            ToolName = toolName;
        }

        public string ServerId { get; }

        public string? ToolName { get; }

        public long WouldBlockCount { get; set; }

        public long PendingDriftCount { get; set; }

        public long UnapprovedDriftCount { get; set; }

        public void AddReasons(string? reasons)
        {
            if (string.IsNullOrWhiteSpace(reasons))
            {
                return;
            }

            foreach (var reason in reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _reasons.Add(reason);
            }
        }

        public ManagementPolicyModeImpactItem ToItem() =>
            new(
                ServerId,
                ToolName,
                WouldBlockCount,
                PendingDriftCount,
                UnapprovedDriftCount,
                _reasons.ToArray());
    }
}

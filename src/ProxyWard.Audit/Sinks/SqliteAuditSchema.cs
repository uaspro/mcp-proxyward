using Microsoft.Data.Sqlite;

namespace ProxyWard.Audit.Sinks;

public static class SqliteAuditSchema
{
    public const int DefaultBusyTimeoutMilliseconds = 5000;

    public static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $$"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout={{busyTimeoutMilliseconds}};
            """;

        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task ConfigureReadConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout={busyTimeoutMilliseconds};";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

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
}

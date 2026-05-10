using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public sealed class SqliteAuditSink : IAuditSink, IBatchedAuditSink, IDisposable
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);
    private SqliteConnection? _connection;
    private bool _schemaInitialized;
    private bool _disposed;

    public SqliteAuditSink(string sqlitePath)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new ArgumentException("sqlitePath is required.", nameof(sqlitePath));
        }

        var fullPath = Path.GetFullPath(sqlitePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteAuditSink));
        }

        ArgumentNullException.ThrowIfNull(auditEvent);

        await WriteBatchCoreAsync([auditEvent], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteBatchAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteAuditSink));
        }

        ArgumentNullException.ThrowIfNull(auditEvents);
        if (auditEvents.Count == 0)
        {
            return;
        }

        foreach (var auditEvent in auditEvents)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
        }

        await WriteBatchCoreAsync(auditEvents, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteBatchCoreAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var auditEvent in auditEvents)
            {
                await using var insert = CreateInsertCommand(connection, transaction, auditEvent);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _writeGate.Dispose();
    }

    private async Task<SqliteConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_schemaInitialized)
        {
            await ConfigureConnectionAsync(_connection, cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(_connection, cancellationToken).ConfigureAwait(false);
            _schemaInitialized = true;
        }

        return _connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;

        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        AuditEvent auditEvent)
    {
        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = """
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

        insert.Parameters.AddWithValue("$timestamp_utc", auditEvent.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$event_type", auditEvent.EventType);
        insert.Parameters.AddWithValue("$mode", auditEvent.Mode);
        insert.Parameters.AddWithValue("$decision", FormatDecision(auditEvent.Decision));
        insert.Parameters.AddWithValue("$server_id", auditEvent.ServerId);
        insert.Parameters.AddWithValue("$method", (object?)auditEvent.Method ?? DBNull.Value);
        insert.Parameters.AddWithValue("$tool_name", (object?)auditEvent.ToolName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$reasons", string.Join(',', auditEvent.Reasons));
        insert.Parameters.AddWithValue("$policy_version", auditEvent.PolicyVersion);
        insert.Parameters.AddWithValue("$correlation_id", auditEvent.CorrelationId);
        insert.Parameters.AddWithValue("$request_bytes", auditEvent.RequestBytes);
        insert.Parameters.AddWithValue("$duration_ms", auditEvent.DurationMs);
        insert.Parameters.AddWithValue("$payload_json", SerializePayload(auditEvent));
        return insert;
    }

    private static string SerializePayload(AuditEvent auditEvent)
    {
        var payload = new JsonObject
        {
            ["timestamp"] = auditEvent.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["eventType"] = auditEvent.EventType,
            ["mode"] = auditEvent.Mode,
            ["decision"] = FormatDecision(auditEvent.Decision),
            ["serverId"] = auditEvent.ServerId,
            ["method"] = auditEvent.Method,
            ["toolName"] = auditEvent.ToolName,
            ["reasons"] = new JsonArray(auditEvent.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["policyVersion"] = auditEvent.PolicyVersion,
            ["correlationId"] = auditEvent.CorrelationId,
            ["requestBytes"] = auditEvent.RequestBytes,
            ["durationMs"] = auditEvent.DurationMs,
            ["batchSize"] = auditEvent.BatchSize,
            ["batchIndex"] = auditEvent.BatchIndex,
            ["argumentOverrideApplied"] = auditEvent.ArgumentOverrideApplied,
            ["argumentSummary"] = auditEvent.ArgumentSummary?.DeepClone()
        };

        return payload.ToJsonString(PayloadJsonOptions);
    }

    private static string FormatDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Allow => "allow",
            AuditDecision.Warn => "warn",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Block => "block",
            _ => "unknown"
        };
}

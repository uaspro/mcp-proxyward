using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public sealed class SqliteAuditSink : IAuditSink, IBatchedAuditSink, IDisposable
{
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
            await SqliteAuditSchema.ConfigureWriteConnectionAsync(_connection, cancellationToken).ConfigureAwait(false);
            await SqliteAuditSchema.EnsureSchemaAsync(_connection, cancellationToken).ConfigureAwait(false);
            _schemaInitialized = true;
        }

        return _connection;
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

        insert.Parameters.AddWithValue("$timestamp_utc", AuditEventPersistence.FormatTimestamp(auditEvent.Timestamp));
        insert.Parameters.AddWithValue("$event_type", auditEvent.EventType);
        insert.Parameters.AddWithValue("$mode", auditEvent.Mode);
        insert.Parameters.AddWithValue("$decision", AuditEventPersistence.FormatDecision(auditEvent.Decision));
        insert.Parameters.AddWithValue("$server_id", auditEvent.ServerId);
        insert.Parameters.AddWithValue("$method", (object?)auditEvent.Method ?? DBNull.Value);
        insert.Parameters.AddWithValue("$tool_name", (object?)auditEvent.ToolName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$reasons", AuditEventPersistence.FormatReasons(auditEvent.Reasons));
        insert.Parameters.AddWithValue("$policy_version", auditEvent.PolicyVersion);
        insert.Parameters.AddWithValue("$correlation_id", auditEvent.CorrelationId);
        insert.Parameters.AddWithValue("$request_bytes", auditEvent.RequestBytes);
        insert.Parameters.AddWithValue("$duration_ms", auditEvent.DurationMs);
        insert.Parameters.AddWithValue("$payload_json", AuditEventPersistence.SerializePayload(auditEvent));
        return insert;
    }
}

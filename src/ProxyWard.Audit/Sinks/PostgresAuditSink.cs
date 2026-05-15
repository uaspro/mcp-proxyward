using Npgsql;
using NpgsqlTypes;
using ProxyWard.Audit.Events;
using ProxyWard.Core.Persistence;

namespace ProxyWard.Audit.Sinks;

public sealed class PostgresAuditSink : IAuditSink, IBatchedAuditSink, IPersistenceSchemaInitializer, IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    private readonly bool _ownsDataSource;
    private bool _schemaInitialized;
    private bool _disposed;

    public PostgresAuditSink(string connectionString)
        : this(CreateDataSource(connectionString), ownsDataSource: true)
    {
    }

    public PostgresAuditSink(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresAuditSink(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(auditEvent);

        await WriteBatchCoreAsync([auditEvent], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteBatchAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
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
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var auditEvent in auditEvents)
        {
            await using var insert = CreateInsertCommand(connection, transaction, auditEvent);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_schemaInitialized)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaInitialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await PostgresAuditSchema.EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            _schemaInitialized = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static NpgsqlCommand CreateInsertCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEvent auditEvent)
    {
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
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
                @timestamp_utc,
                @event_type,
                @mode,
                @decision,
                @server_id,
                @method,
                @tool_name,
                @reasons,
                @policy_version,
                @correlation_id,
                @request_bytes,
                @duration_ms,
                @payload_json
            );
            """;

        insert.Parameters.AddWithValue("timestamp_utc", AuditEventPersistence.FormatTimestamp(auditEvent.Timestamp));
        insert.Parameters.AddWithValue("event_type", auditEvent.EventType);
        insert.Parameters.AddWithValue("mode", auditEvent.Mode);
        insert.Parameters.AddWithValue("decision", AuditEventPersistence.FormatDecision(auditEvent.Decision));
        insert.Parameters.AddWithValue("server_id", auditEvent.ServerId);
        insert.Parameters.AddWithValue("method", (object?)auditEvent.Method ?? DBNull.Value);
        insert.Parameters.AddWithValue("tool_name", (object?)auditEvent.ToolName ?? DBNull.Value);
        insert.Parameters.AddWithValue("reasons", AuditEventPersistence.FormatReasons(auditEvent.Reasons));
        insert.Parameters.AddWithValue("policy_version", auditEvent.PolicyVersion);
        insert.Parameters.AddWithValue("correlation_id", auditEvent.CorrelationId);
        insert.Parameters.AddWithValue("request_bytes", auditEvent.RequestBytes);
        insert.Parameters.AddWithValue("duration_ms", auditEvent.DurationMs);
        insert.Parameters.AddWithValue(
            "payload_json",
            NpgsqlDbType.Jsonb,
            AuditEventPersistence.SerializePayload(auditEvent));
        return insert;
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

        _schemaGate.Dispose();
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

        _schemaGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgresAuditSink));
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

using System.Globalization;
using Npgsql;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application.Policy;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class PostgresManagementPolicyAuditStore : IManagementPolicyAuditStore, IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private bool _disposed;

    public PostgresManagementPolicyAuditStore(string connectionString)
        : this(CreateDataSource(connectionString), ownsDataSource: true)
    {
    }

    public PostgresManagementPolicyAuditStore(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresManagementPolicyAuditStore(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public async Task<IReadOnlyList<ManagementPolicyModeImpactItem>> ReadModeImpactAsync(
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var affected = new Dictionary<ImpactKey, ImpactAccumulator>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresAuditSchema.EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

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
        ThrowIfDisposed();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresAuditSchema.EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

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
                CAST(@payload_json AS jsonb)
            );
            """;
        command.Parameters.AddWithValue("timestamp_utc", FormatTimestamp(auditEvent.TimestampUtc));
        command.Parameters.AddWithValue("event_type", auditEvent.EventType);
        command.Parameters.AddWithValue("mode", "management");
        command.Parameters.AddWithValue("decision", "allow");
        command.Parameters.AddWithValue("server_id", "management");
        command.Parameters.AddWithValue("method", auditEvent.Method);
        command.Parameters.AddWithValue("tool_name", DBNull.Value);
        command.Parameters.AddWithValue("reasons", auditEvent.Reasons);
        command.Parameters.AddWithValue("policy_version", auditEvent.PolicyVersion);
        command.Parameters.AddWithValue("correlation_id", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("request_bytes", 0L);
        command.Parameters.AddWithValue("duration_ms", auditEvent.DurationMs);
        command.Parameters.AddWithValue("payload_json", auditEvent.PayloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadWouldBlockImpactAsync(
        NpgsqlConnection connection,
        ManagementPolicyImpactWindow window,
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, tool_name, COUNT(*), string_agg(reasons, ',')
            FROM audit_events
            WHERE decision = 'would_block'
              AND timestamp_utc >= @from_utc
              AND timestamp_utc <= @to_utc
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
        NpgsqlConnection connection,
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
                string_agg(reasons, ',')
            FROM schema_drift_reviews
            WHERE status IN ('pending', 'rejected', 'blocked')
              AND detected_at_utc >= @from_utc
              AND detected_at_utc <= @to_utc
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

    private static void AddWindowParameters(NpgsqlCommand command, ManagementPolicyImpactWindow window)
    {
        command.Parameters.AddWithValue("from_utc", FormatTimestamp(window.FromUtc));
        command.Parameters.AddWithValue("to_utc", FormatTimestamp(window.ToUtc));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

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
            throw new ObjectDisposedException(nameof(PostgresManagementPolicyAuditStore));
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

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ProxyWard.Locking.Persistence;

public sealed class SqliteSchemaDriftReviewStore : ISchemaDriftReviewStore, IDisposable
{
    private const int SqliteBusyErrorCode = 5;
    private const int SqliteLockedErrorCode = 6;
    private const int SqliteReadonlyErrorCode = 8;
    private const int SqliteIoErrorCode = 10;

    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;
    private readonly SqliteOpenMode _openMode;
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);
    private SqliteConnection? _connection;
    private bool _schemaInitialized;
    private bool _disposed;

    public SqliteSchemaDriftReviewStore(
        string sqlitePath,
        int busyTimeoutMilliseconds = 5000,
        SqliteOpenMode openMode = SqliteOpenMode.ReadWriteCreate)
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

        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _openMode = openMode;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = openMode,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000.0))
        }.ToString();
    }

    public async ValueTask<DriftReviewRecordResult> RecordObservationAsync(
        DriftReviewObservation observation,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteSchemaDriftReviewStore));
        }

        ArgumentNullException.ThrowIfNull(observation);
        ValidateObservation(observation);

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_openMode == SqliteOpenMode.ReadOnly)
                {
                    throw new SchemaLockWriteFailedException(
                        SchemaLockWriteFailureReasons.DbReadonly,
                        $"Drift review write failed for server '{observation.ServerId}': database was opened read-only.",
                        new InvalidOperationException("The drift review database was opened read-only."));
                }

                var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

                var detectedAtIso = observation.DetectedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                var reasonsCsv = string.Join(",", observation.Reasons);

                int affected;
                await using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = """
                        INSERT INTO schema_drift_reviews (
                            server_id, tool_name, field_name,
                            from_version, to_version,
                            status, reasons, policy_version, detected_at_utc
                        ) VALUES (
                            $server_id, $tool_name, $field_name,
                            $from_version, $to_version,
                            $status, $reasons, $policy_version, $detected_at_utc
                        )
                        ON CONFLICT(server_id, tool_name, field_name, from_version, to_version) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("$server_id", observation.ServerId);
                    insert.Parameters.AddWithValue("$tool_name", observation.ToolName);
                    insert.Parameters.AddWithValue("$field_name", observation.FieldName);
                    insert.Parameters.AddWithValue("$from_version", observation.FromVersion);
                    insert.Parameters.AddWithValue("$to_version", observation.ToVersion);
                    insert.Parameters.AddWithValue("$status", "pending");
                    insert.Parameters.AddWithValue("$reasons", reasonsCsv);
                    insert.Parameters.AddWithValue("$policy_version", (object?)observation.PolicyVersion ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$detected_at_utc", detectedAtIso);

                    affected = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var row = await LoadByKeyAsync(connection, observation, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    throw new InvalidOperationException(
                        $"Drift review row for server '{observation.ServerId}' tool '{observation.ToolName}' could not be loaded after insert.");
                }

                return new DriftReviewRecordResult(row, WasNewlyCreated: affected > 0);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (SqliteException ex) when (IsOperationalWriteFailure(ex))
        {
            throw new SchemaLockWriteFailedException(
                MapFailureReason(ex),
                $"Drift review write failed for server '{observation.ServerId}': {ex.Message}",
                ex);
        }
    }

    public async ValueTask<IReadOnlyList<DriftReviewRow>> GetByServerAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteSchemaDriftReviewStore));
        }

        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("serverId is required.", nameof(serverId));
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT id, server_id, tool_name, field_name, from_version, to_version,
                       status, reasons, policy_version, detected_at_utc,
                       reviewed_at_utc, reviewed_by, review_note
                FROM schema_drift_reviews
                WHERE server_id = $server_id
                ORDER BY detected_at_utc DESC, id DESC;
                """;
            select.Parameters.AddWithValue("$server_id", serverId);

            var rows = new List<DriftReviewRow>();
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader));
            }

            return rows;
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

    private static void ValidateObservation(DriftReviewObservation observation)
    {
        if (string.IsNullOrWhiteSpace(observation.ServerId))
        {
            throw new ArgumentException("ServerId is required.", nameof(observation));
        }

        if (string.IsNullOrWhiteSpace(observation.ToolName))
        {
            throw new ArgumentException("ToolName is required.", nameof(observation));
        }

        if (string.IsNullOrWhiteSpace(observation.FieldName))
        {
            throw new ArgumentException("FieldName is required.", nameof(observation));
        }

        if (observation.Reasons is null || observation.Reasons.Count == 0)
        {
            throw new ArgumentException("Reasons must contain at least one entry.", nameof(observation));
        }

        if (observation.Reasons.Any(reason => string.IsNullOrWhiteSpace(reason) || reason.Contains(',', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Reasons must be non-empty comma-free reason codes.", nameof(observation));
        }

        if (observation.FromVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "FromVersion must be greater than or equal to zero.");
        }

        if (observation.ToVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "ToVersion must be greater than or equal to one.");
        }

        if (observation.ToVersion <= observation.FromVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                "ToVersion must be strictly greater than FromVersion.");
        }
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
            await ConfigureConnectionAsync(_connection, _busyTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(_connection, cancellationToken).ConfigureAwait(false);
            _schemaInitialized = true;
        }

        return _connection;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        int busyTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $$"""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout={{busyTimeoutMilliseconds}};
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_drift_reviews (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                server_id             TEXT    NOT NULL,
                tool_name             TEXT    NOT NULL,
                field_name            TEXT    NOT NULL,
                from_version          INTEGER NOT NULL,
                to_version            INTEGER NOT NULL,
                status                TEXT    NOT NULL,
                reasons               TEXT    NOT NULL,
                policy_version        TEXT    NULL,
                detected_at_utc       TEXT    NOT NULL,
                reviewed_at_utc       TEXT    NULL,
                reviewed_by           TEXT    NULL,
                review_note           TEXT    NULL,
                UNIQUE(server_id, tool_name, field_name, from_version, to_version)
            );

            CREATE INDEX IF NOT EXISTS idx_schema_drift_reviews_server_detected
                ON schema_drift_reviews(server_id, detected_at_utc DESC);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DriftReviewRow?> LoadByKeyAsync(
        SqliteConnection connection,
        DriftReviewObservation observation,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, server_id, tool_name, field_name, from_version, to_version,
                   status, reasons, policy_version, detected_at_utc,
                   reviewed_at_utc, reviewed_by, review_note
            FROM schema_drift_reviews
            WHERE server_id = $server_id
              AND tool_name = $tool_name
              AND field_name = $field_name
              AND from_version = $from_version
              AND to_version = $to_version
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$server_id", observation.ServerId);
        select.Parameters.AddWithValue("$tool_name", observation.ToolName);
        select.Parameters.AddWithValue("$field_name", observation.FieldName);
        select.Parameters.AddWithValue("$from_version", observation.FromVersion);
        select.Parameters.AddWithValue("$to_version", observation.ToVersion);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRow(reader);
    }

    private static DriftReviewRow ReadRow(SqliteDataReader reader) =>
        new(
            Id: reader.GetInt64(0),
            ServerId: reader.GetString(1),
            ToolName: reader.GetString(2),
            FieldName: reader.GetString(3),
            FromVersion: reader.GetInt32(4),
            ToVersion: reader.GetInt32(5),
            Status: ParseStatus(reader.GetString(6)),
            Reasons: SplitReasons(reader.GetString(7)),
            PolicyVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            DetectedAtUtc: ParseTimestamp(reader.GetString(9)),
            ReviewedAtUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
            ReviewedBy: reader.IsDBNull(11) ? null : reader.GetString(11),
            ReviewNote: reader.IsDBNull(12) ? null : reader.GetString(12));

    private static DriftReviewStatus ParseStatus(string raw) => raw switch
    {
        "pending" => DriftReviewStatus.Pending,
        "approved" => DriftReviewStatus.Approved,
        "rejected" => DriftReviewStatus.Rejected,
        "blocked" => DriftReviewStatus.Blocked,
        _ => throw new InvalidOperationException($"Unknown drift review status: '{raw}'.")
    };

    private static IReadOnlyList<string> SplitReasons(string reasons) =>
        reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DateTimeOffset ParseTimestamp(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private static bool IsOperationalWriteFailure(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteBusyErrorCode
            or SqliteLockedErrorCode
            or SqliteReadonlyErrorCode
            or SqliteIoErrorCode;

    private static string MapFailureReason(SqliteException ex) =>
        ex.SqliteErrorCode switch
        {
            SqliteBusyErrorCode or SqliteLockedErrorCode => SchemaLockWriteFailureReasons.DbLocked,
            SqliteReadonlyErrorCode => SchemaLockWriteFailureReasons.DbReadonly,
            _ => SchemaLockWriteFailureReasons.DbIo
        };
}

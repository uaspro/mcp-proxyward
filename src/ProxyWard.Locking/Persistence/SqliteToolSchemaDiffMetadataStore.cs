using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ProxyWard.Locking.Persistence;

public sealed class SqliteToolSchemaDiffMetadataStore : IToolSchemaDiffMetadataStore, IDisposable
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

    public SqliteToolSchemaDiffMetadataStore(
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

    public async ValueTask<ToolSchemaDiffMetadataRow> RecordAsync(
        ToolSchemaDiffMetadataInput input,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteToolSchemaDiffMetadataStore));
        }

        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_openMode == SqliteOpenMode.ReadOnly)
                {
                    throw new SchemaLockWriteFailedException(
                        SchemaLockWriteFailureReasons.DbReadonly,
                        $"Tool schema diff metadata write failed for drift review '{input.DriftReviewId}': database was opened read-only.",
                        new InvalidOperationException("The tool schema diff metadata database was opened read-only."));
                }

                var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
                var createdAtIso = input.CreatedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

                await using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = """
                        INSERT INTO tool_schema_diff_metadata (
                            drift_review_id,
                            before_json,
                            after_json,
                            before_hash,
                            after_hash,
                            created_at_utc
                        ) VALUES (
                            $drift_review_id,
                            $before_json,
                            $after_json,
                            $before_hash,
                            $after_hash,
                            $created_at_utc
                        )
                        ON CONFLICT(drift_review_id) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("$drift_review_id", input.DriftReviewId);
                    insert.Parameters.AddWithValue("$before_json", (object?)input.BeforeJson ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$after_json", (object?)input.AfterJson ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$before_hash", input.BeforeHash);
                    insert.Parameters.AddWithValue("$after_hash", input.AfterHash);
                    insert.Parameters.AddWithValue("$created_at_utc", createdAtIso);

                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var row = await LoadByDriftReviewIdAsync(connection, input.DriftReviewId, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    throw new InvalidOperationException(
                        $"Tool schema diff metadata for drift review '{input.DriftReviewId}' could not be loaded after insert.");
                }

                return row;
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
                $"Tool schema diff metadata write failed for drift review '{input.DriftReviewId}': {ex.Message}",
                ex);
        }
    }

    public async ValueTask<ToolSchemaDiffMetadataRow?> GetByDriftReviewIdAsync(
        long driftReviewId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteToolSchemaDiffMetadataStore));
        }

        if (driftReviewId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(driftReviewId), "driftReviewId must be greater than zero.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await LoadByDriftReviewIdAsync(connection, driftReviewId, cancellationToken).ConfigureAwait(false);
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

    private static void ValidateInput(ToolSchemaDiffMetadataInput input)
    {
        if (input.DriftReviewId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "DriftReviewId must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(input.BeforeHash))
        {
            throw new ArgumentException("BeforeHash is required.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.AfterHash))
        {
            throw new ArgumentException("AfterHash is required.", nameof(input));
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
            CREATE TABLE IF NOT EXISTS tool_schema_diff_metadata (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                drift_review_id  INTEGER NOT NULL,
                before_json      TEXT    NULL,
                after_json       TEXT    NULL,
                before_hash      TEXT    NOT NULL,
                after_hash       TEXT    NOT NULL,
                created_at_utc   TEXT    NOT NULL,
                FOREIGN KEY(drift_review_id) REFERENCES schema_drift_reviews(id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_tool_schema_diff_metadata_review
                ON tool_schema_diff_metadata(drift_review_id);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ToolSchemaDiffMetadataRow?> LoadByDriftReviewIdAsync(
        SqliteConnection connection,
        long driftReviewId,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, drift_review_id, before_json, after_json, before_hash, after_hash, created_at_utc
            FROM tool_schema_diff_metadata
            WHERE drift_review_id = $drift_review_id
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$drift_review_id", driftReviewId);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ToolSchemaDiffMetadataRow(
            Id: reader.GetInt64(0),
            DriftReviewId: reader.GetInt64(1),
            BeforeJson: reader.IsDBNull(2) ? null : reader.GetString(2),
            AfterJson: reader.IsDBNull(3) ? null : reader.GetString(3),
            BeforeHash: reader.GetString(4),
            AfterHash: reader.GetString(5),
            CreatedAtUtc: DateTimeOffset.Parse(
                reader.GetString(6),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

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

using System.Globalization;
using Npgsql;
using ProxyWard.Core.Persistence;

namespace ProxyWard.Locking.Persistence;

public sealed class PostgresToolSchemaDiffMetadataStore : IToolSchemaDiffMetadataStore, IPersistenceSchemaInitializer, IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);
    private readonly bool _ownsDataSource;
    private bool _schemaInitialized;
    private bool _disposed;

    public PostgresToolSchemaDiffMetadataStore(string connectionString)
        : this(CreateDataSource(connectionString), ownsDataSource: true)
    {
    }

    public PostgresToolSchemaDiffMetadataStore(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresToolSchemaDiffMetadataStore(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public async ValueTask<ToolSchemaDiffMetadataRow> RecordAsync(
        ToolSchemaDiffMetadataInput input,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                            @drift_review_id,
                            @before_json,
                            @after_json,
                            @before_hash,
                            @after_hash,
                            @created_at_utc
                        )
                        ON CONFLICT(drift_review_id) DO NOTHING;
                        """;
                    insert.Parameters.AddWithValue("drift_review_id", input.DriftReviewId);
                    insert.Parameters.AddWithValue("before_json", (object?)input.BeforeJson ?? DBNull.Value);
                    insert.Parameters.AddWithValue("after_json", (object?)input.AfterJson ?? DBNull.Value);
                    insert.Parameters.AddWithValue("before_hash", input.BeforeHash);
                    insert.Parameters.AddWithValue("after_hash", input.AfterHash);
                    insert.Parameters.AddWithValue("created_at_utc", createdAtIso);

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
        catch (NpgsqlException ex)
        {
            throw new SchemaLockWriteFailedException(
                SchemaLockWriteFailureReasons.DbIo,
                $"Tool schema diff metadata write failed for drift review '{input.DriftReviewId}': {ex.Message}",
                ex);
        }
    }

    public async ValueTask<ToolSchemaDiffMetadataRow?> GetByDriftReviewIdAsync(
        long driftReviewId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (driftReviewId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(driftReviewId), "driftReviewId must be greater than zero.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await LoadByDriftReviewIdAsync(connection, driftReviewId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
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

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_schemaInitialized)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_drift_reviews (
                id                    BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
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

            CREATE TABLE IF NOT EXISTS tool_schema_diff_metadata (
                id               BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                drift_review_id  BIGINT NOT NULL,
                before_json      TEXT   NULL,
                after_json       TEXT   NULL,
                before_hash      TEXT   NOT NULL,
                after_hash       TEXT   NOT NULL,
                created_at_utc   TEXT   NOT NULL,
                FOREIGN KEY(drift_review_id) REFERENCES schema_drift_reviews(id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_tool_schema_diff_metadata_review
                ON tool_schema_diff_metadata(drift_review_id);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _schemaInitialized = true;
    }

    private static async Task<ToolSchemaDiffMetadataRow?> LoadByDriftReviewIdAsync(
        NpgsqlConnection connection,
        long driftReviewId,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, drift_review_id, before_json, after_json, before_hash, after_hash, created_at_utc
            FROM tool_schema_diff_metadata
            WHERE drift_review_id = @drift_review_id
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("drift_review_id", driftReviewId);

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

        _writeGate.Dispose();
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

        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgresToolSchemaDiffMetadataStore));
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

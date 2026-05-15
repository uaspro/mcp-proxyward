using System.Collections.Concurrent;
using System.Globalization;
using Npgsql;
using ProxyWard.Core.Persistence;

namespace ProxyWard.Locking.Persistence;

public sealed class PostgresTrackedToolSchemaStore : ITrackedToolSchemaStore, IPersistenceSchemaInitializer, IAsyncDisposable, IDisposable
{
    private const string UniqueViolationSqlState = "23505";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<string, ToolSchemaVersionRow> _latestByServer = new(StringComparer.Ordinal);
    private readonly bool _ownsDataSource;
    private bool _schemaInitialized;
    private bool _disposed;

    public PostgresTrackedToolSchemaStore(string connectionString)
        : this(CreateDataSource(connectionString), ownsDataSource: true)
    {
    }

    public PostgresTrackedToolSchemaStore(NpgsqlDataSource dataSource)
        : this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresTrackedToolSchemaStore(NpgsqlDataSource dataSource, bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public async ValueTask<ToolSchemaVersionRow?> GetLatestAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("serverId is required.", nameof(serverId));
        }

        if (_latestByServer.TryGetValue(serverId, out var cached))
        {
            return cached;
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_latestByServer.TryGetValue(serverId, out cached))
                {
                    return cached;
                }

                var loaded = await LoadLatestAsync(serverId, cancellationToken).ConfigureAwait(false);
                if (loaded is not null)
                {
                    _latestByServer[serverId] = loaded;
                }

                return loaded;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (NpgsqlException ex)
        {
            _latestByServer.TryRemove(serverId, out _);
            throw new SchemaLockWriteFailedException(
                SchemaLockWriteFailureReasons.DbIo,
                $"Schema-lock read failed for server '{serverId}': {ex.Message}",
                ex);
        }
    }

    public async ValueTask<RecordedVersion> RecordAsync(
        ToolSchemaSnapshotInput snapshot,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.ServerId))
        {
            throw new ArgumentException("ServerId is required.", nameof(snapshot));
        }

        if (string.IsNullOrWhiteSpace(snapshot.UpstreamUrl))
        {
            throw new ArgumentException("UpstreamUrl is required.", nameof(snapshot));
        }

        if (string.IsNullOrWhiteSpace(snapshot.McpProtocol))
        {
            throw new ArgumentException("McpProtocol is required.", nameof(snapshot));
        }

        var (canonicalJson, snapshotHash) = CanonicalToolSchemaSerializer.Serialize(
            snapshot.McpProtocol,
            snapshot.Tools);

        if (TryCreateCachedNoOpRecordedVersion(snapshot, snapshotHash, out var cachedRecorded))
        {
            return cachedRecorded;
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryCreateCachedNoOpRecordedVersion(snapshot, snapshotHash, out cachedRecorded))
                {
                    return cachedRecorded;
                }

                var latest = await LoadLatestAsync(snapshot.ServerId, cancellationToken).ConfigureAwait(false);
                if (latest is not null && string.Equals(latest.SnapshotHash, snapshotHash, StringComparison.Ordinal))
                {
                    _latestByServer[snapshot.ServerId] = latest;
                    return CreateNoOpRecordedVersion(latest, snapshot);
                }

                var nextVersion = (latest?.Version ?? 0) + 1;
                var createdAt = capturedAtUtc.ToUniversalTime();
                var createdAtIso = createdAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                var toolCount = snapshot.Tools.Count(entry => !string.IsNullOrEmpty(entry.ToolName));

                try
                {
                    await InsertAsync(
                        snapshot,
                        nextVersion,
                        snapshotHash,
                        canonicalJson,
                        toolCount,
                        createdAtIso,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException ex) when (ex.SqlState == UniqueViolationSqlState)
                {
                    _latestByServer.TryRemove(snapshot.ServerId, out _);
                    var winner = await LoadLatestAsync(snapshot.ServerId, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("UNIQUE constraint fired but no row could be loaded for server.");

                    if (string.Equals(winner.SnapshotHash, snapshotHash, StringComparison.Ordinal))
                    {
                        _latestByServer[snapshot.ServerId] = winner;
                        return new RecordedVersion(winner.Version, winner.SnapshotHash, WasNewVersion: false, winner.CreatedAtUtc);
                    }

                    nextVersion = winner.Version + 1;
                    await InsertAsync(
                        snapshot,
                        nextVersion,
                        snapshotHash,
                        canonicalJson,
                        toolCount,
                        createdAtIso,
                        cancellationToken).ConfigureAwait(false);
                }

                _latestByServer.TryRemove(snapshot.ServerId, out _);
                return new RecordedVersion(nextVersion, snapshotHash, WasNewVersion: true, createdAt);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (NpgsqlException ex)
        {
            _latestByServer.TryRemove(snapshot.ServerId, out _);
            throw new SchemaLockWriteFailedException(
                SchemaLockWriteFailureReasons.DbIo,
                $"Schema-lock write failed for server '{snapshot.ServerId}': {ex.Message}",
                ex);
        }
    }

    private bool TryCreateCachedNoOpRecordedVersion(
        ToolSchemaSnapshotInput snapshot,
        string snapshotHash,
        out RecordedVersion recorded)
    {
        if (_latestByServer.TryGetValue(snapshot.ServerId, out var latest)
            && string.Equals(latest.SnapshotHash, snapshotHash, StringComparison.Ordinal))
        {
            recorded = CreateNoOpRecordedVersion(latest, snapshot);
            return true;
        }

        recorded = default!;
        return false;
    }

    private static RecordedVersion CreateNoOpRecordedVersion(
        ToolSchemaVersionRow latest,
        ToolSchemaSnapshotInput snapshot)
    {
        var upstreamChanged = !string.Equals(latest.UpstreamUrl, snapshot.UpstreamUrl, StringComparison.Ordinal);
        return new RecordedVersion(
            latest.Version,
            latest.SnapshotHash,
            WasNewVersion: false,
            latest.CreatedAtUtc,
            upstreamChanged,
            upstreamChanged ? latest.UpstreamUrl : null,
            upstreamChanged ? snapshot.UpstreamUrl : null);
    }

    private async Task InsertAsync(
        ToolSchemaSnapshotInput snapshot,
        int version,
        string snapshotHash,
        string canonicalJson,
        int toolCount,
        string createdAtIso,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO tool_schema_versions (
                server_id,
                upstream_url,
                version,
                snapshot_hash,
                mcp_protocol,
                fingerprints,
                tool_count,
                policy_version,
                source_correlation_id,
                created_at_utc
            ) VALUES (
                @server_id,
                @upstream_url,
                @version,
                @snapshot_hash,
                @mcp_protocol,
                @fingerprints,
                @tool_count,
                @policy_version,
                @source_correlation_id,
                @created_at_utc
            );
            """;

        insert.Parameters.AddWithValue("server_id", snapshot.ServerId);
        insert.Parameters.AddWithValue("upstream_url", snapshot.UpstreamUrl);
        insert.Parameters.AddWithValue("version", version);
        insert.Parameters.AddWithValue("snapshot_hash", snapshotHash);
        insert.Parameters.AddWithValue("mcp_protocol", snapshot.McpProtocol);
        insert.Parameters.AddWithValue("fingerprints", canonicalJson);
        insert.Parameters.AddWithValue("tool_count", toolCount);
        insert.Parameters.AddWithValue("policy_version", (object?)snapshot.PolicyVersion ?? DBNull.Value);
        insert.Parameters.AddWithValue("source_correlation_id", (object?)snapshot.SourceCorrelationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("created_at_utc", createdAtIso);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolSchemaVersionRow?> LoadLatestAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, server_id, upstream_url, version, snapshot_hash, mcp_protocol,
                   fingerprints, tool_count, policy_version, source_correlation_id, created_at_utc
            FROM tool_schema_versions
            WHERE server_id = @server_id
            ORDER BY version DESC
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("server_id", serverId);

        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var canonicalJson = reader.GetString(6);
        var fingerprints = CanonicalToolSchemaSerializer.Deserialize(canonicalJson);

        return new ToolSchemaVersionRow(
            Id: reader.GetInt64(0),
            ServerId: reader.GetString(1),
            UpstreamUrl: reader.GetString(2),
            Version: reader.GetInt32(3),
            SnapshotHash: reader.GetString(4),
            McpProtocol: reader.GetString(5),
            Fingerprints: fingerprints,
            ToolCount: reader.GetInt32(7),
            PolicyVersion: reader.IsDBNull(8) ? null : reader.GetString(8),
            SourceCorrelationId: reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAtUtc: DateTimeOffset.Parse(
                reader.GetString(10),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
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
            CREATE TABLE IF NOT EXISTS tool_schema_versions (
                id                    BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                server_id             TEXT    NOT NULL,
                upstream_url          TEXT    NOT NULL,
                version               INTEGER NOT NULL,
                snapshot_hash         TEXT    NOT NULL,
                mcp_protocol          TEXT    NOT NULL,
                fingerprints          TEXT    NOT NULL,
                tool_count            INTEGER NOT NULL,
                policy_version        TEXT    NULL,
                source_correlation_id TEXT    NULL,
                created_at_utc        TEXT    NOT NULL,
                UNIQUE (server_id, version)
            );
            CREATE INDEX IF NOT EXISTS idx_tsv_server_created ON tool_schema_versions(server_id, created_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_tsv_server_version ON tool_schema_versions(server_id, version DESC);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _schemaInitialized = true;
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
            throw new ObjectDisposedException(nameof(PostgresTrackedToolSchemaStore));
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

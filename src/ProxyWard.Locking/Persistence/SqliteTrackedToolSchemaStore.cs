using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ProxyWard.Locking.Persistence;

public sealed class SqliteTrackedToolSchemaStore : ITrackedToolSchemaStore, IDisposable
{
    private const int SqliteBusyErrorCode = 5;
    private const int SqliteLockedErrorCode = 6;
    private const int SqliteReadonlyErrorCode = 8;
    private const int SqliteIoErrorCode = 10;
    private const int SqliteConstraintErrorCode = 19;

    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;
    private readonly SqliteOpenMode _openMode;
    private readonly SemaphoreSlim _writeGate = new(initialCount: 1, maxCount: 1);
    private readonly ConcurrentDictionary<string, ToolSchemaVersionRow> _latestByServer = new(StringComparer.Ordinal);
    private SqliteConnection? _connection;
    private bool _schemaInitialized;
    private bool _disposed;

    public SqliteTrackedToolSchemaStore(
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

    public async ValueTask<ToolSchemaVersionRow?> GetLatestAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteTrackedToolSchemaStore));
        }

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
        catch (SqliteException ex) when (IsOperationalWriteFailure(ex))
        {
            _latestByServer.TryRemove(serverId, out _);
            throw new SchemaLockWriteFailedException(
                MapFailureReason(ex),
                $"Schema-lock read failed for server '{serverId}': {ex.Message}",
                ex);
        }
    }

    public async ValueTask<RecordedVersion> RecordAsync(
        ToolSchemaSnapshotInput snapshot,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteTrackedToolSchemaStore));
        }

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

                if (_openMode == SqliteOpenMode.ReadOnly)
                {
                    throw new SchemaLockWriteFailedException(
                        SchemaLockWriteFailureReasons.DbReadonly,
                        $"Schema-lock write failed for server '{snapshot.ServerId}': database was opened read-only.",
                        new InvalidOperationException("The schema-lock database was opened read-only."));
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
                catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintErrorCode)
                {
                    // Concurrent first-row race: another writer inserted (server_id, version).
                    // Re-fetch latest; if it matches our hash, return it; otherwise insert at the new MAX+1.
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

                // Invalidate cache so the next GetLatestAsync re-reads the row we just wrote.
                _latestByServer.TryRemove(snapshot.ServerId, out _);

                return new RecordedVersion(nextVersion, snapshotHash, WasNewVersion: true, createdAt);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (SqliteException ex) when (IsOperationalWriteFailure(ex))
        {
            _latestByServer.TryRemove(snapshot.ServerId, out _);
            throw new SchemaLockWriteFailedException(
                MapFailureReason(ex),
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

    private async Task InsertAsync(
        ToolSchemaSnapshotInput snapshot,
        int version,
        string snapshotHash,
        string canonicalJson,
        int toolCount,
        string createdAtIso,
        CancellationToken cancellationToken)
    {
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

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
                $server_id,
                $upstream_url,
                $version,
                $snapshot_hash,
                $mcp_protocol,
                $fingerprints,
                $tool_count,
                $policy_version,
                $source_correlation_id,
                $created_at_utc
            );
            """;

        insert.Parameters.AddWithValue("$server_id", snapshot.ServerId);
        insert.Parameters.AddWithValue("$upstream_url", snapshot.UpstreamUrl);
        insert.Parameters.AddWithValue("$version", version);
        insert.Parameters.AddWithValue("$snapshot_hash", snapshotHash);
        insert.Parameters.AddWithValue("$mcp_protocol", snapshot.McpProtocol);
        insert.Parameters.AddWithValue("$fingerprints", canonicalJson);
        insert.Parameters.AddWithValue("$tool_count", toolCount);
        insert.Parameters.AddWithValue("$policy_version", (object?)snapshot.PolicyVersion ?? DBNull.Value);
        insert.Parameters.AddWithValue("$source_correlation_id", (object?)snapshot.SourceCorrelationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created_at_utc", createdAtIso);

        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolSchemaVersionRow?> LoadLatestAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, server_id, upstream_url, version, snapshot_hash, mcp_protocol,
                   fingerprints, tool_count, policy_version, source_correlation_id, created_at_utc
            FROM tool_schema_versions
            WHERE server_id = $server_id
            ORDER BY version DESC
            LIMIT 1;
            """;
        select.Parameters.AddWithValue("$server_id", serverId);

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

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_schema_versions (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
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
    }
}

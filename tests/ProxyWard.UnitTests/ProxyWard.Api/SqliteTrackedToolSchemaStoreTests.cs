using Microsoft.Data.Sqlite;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.UnitTests;

public class SqliteTrackedToolSchemaStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-schema-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in new[]
        {
            _databasePath,
            $"{_databasePath}-shm",
            $"{_databasePath}-wal"
        })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best-effort cleanup; SQLite may still hold the file briefly.
                }
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordAsyncOnEmptyServerWritesVersionOne()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);

        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
        {
            CreateEntry("repos.search", "h-d-1", "h-i-1")
        });

        var recorded = await store.RecordAsync(snapshot, capturedAt, CancellationToken.None);

        Assert.Equal(1, recorded.Version);
        Assert.True(recorded.WasNewVersion);
        Assert.False(string.IsNullOrEmpty(recorded.SnapshotHash));
        Assert.Equal(capturedAt, recorded.CreatedAtUtc);

        var latest = await store.GetLatestAsync("github", CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(1, latest!.Version);
        Assert.Equal(recorded.SnapshotHash, latest.SnapshotHash);
        Assert.Equal("https://github-mcp/", latest.UpstreamUrl);
        Assert.Equal(1, latest.ToolCount);
        Assert.Equal("repos.search", latest.Fingerprints.Single().ToolName);
    }

    [Fact]
    public async Task RecordAsyncIdempotentWhenSnapshotHashUnchanged()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
        {
            CreateEntry("repos.search", "h-d-1", "h-i-1")
        });

        var first = await store.RecordAsync(snapshot, capturedAt, CancellationToken.None);
        var second = await store.RecordAsync(snapshot, capturedAt.AddMinutes(5), CancellationToken.None);

        Assert.True(first.WasNewVersion);
        Assert.False(second.WasNewVersion);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.SnapshotHash, second.SnapshotHash);

        Assert.Equal(1, await CountRowsForServer(_databasePath, "github"));
    }

    [Fact]
    public async Task RecordAsyncBumpsVersionWhenFingerprintsChange()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

        var first = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("repos.search", "h-d-1", "h-i-1")
            }),
            capturedAt,
            CancellationToken.None);

        var second = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("repos.search", "h-d-2", "h-i-1")
            }),
            capturedAt.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.True(second.WasNewVersion);
        Assert.NotEqual(first.SnapshotHash, second.SnapshotHash);

        Assert.Equal(2, await CountRowsForServer(_databasePath, "github"));
    }

    [Fact]
    public async Task SnapshotHashIsStableAcrossInputReorderings()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

        var a = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("repos.search", "h-d-1", "h-i-1"),
                CreateEntry("issues.list", "h-d-2", "h-i-2")
            }),
            capturedAt,
            CancellationToken.None);

        // Re-record with the entries in reversed order — same set, different ordering.
        var b = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("issues.list", "h-d-2", "h-i-2"),
                CreateEntry("repos.search", "h-d-1", "h-i-1")
            }),
            capturedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(a.SnapshotHash, b.SnapshotHash);
        Assert.False(b.WasNewVersion);
    }

    [Fact]
    public async Task SnapshotHashChangesWhenOnlyMcpProtocolChanges()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

        var a = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("repos.search", "h-d-1", "h-i-1")
            }),
            capturedAt,
            CancellationToken.None);

        var b = await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2026-01-01", new[]
            {
                CreateEntry("repos.search", "h-d-1", "h-i-1")
            }),
            capturedAt.AddSeconds(1),
            CancellationToken.None);

        Assert.NotEqual(a.SnapshotHash, b.SnapshotHash);
        Assert.True(b.WasNewVersion);
        Assert.Equal(2, b.Version);
    }

    [Fact]
    public async Task GetLatestAsyncReturnsNullForUnknownServer()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);

        var latest = await store.GetLatestAsync("never-seen", CancellationToken.None);

        Assert.Null(latest);
    }

    [Fact]
    public async Task GetLatestAsyncIsCacheFrontedAfterFirstLoad()
    {
        using (var writer = new SqliteTrackedToolSchemaStore(_databasePath))
        {
            await writer.RecordAsync(
                CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
                {
                    CreateEntry("repos.search", "h-d-1", "h-i-1")
                }),
                new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero),
                CancellationToken.None);
        }

        using var reader = new SqliteTrackedToolSchemaStore(_databasePath);

        var first = await reader.GetLatestAsync("github", CancellationToken.None);
        Assert.NotNull(first);

        // Mutate the DB out from under the reader using a separate connection.
        // Cache should still return the previously loaded row.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString()))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE tool_schema_versions SET upstream_url = 'https://elsewhere/' WHERE server_id = 'github';";
            update.ExecuteNonQuery();
        }

        var second = await reader.GetLatestAsync("github", CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("https://github-mcp/", second!.UpstreamUrl);
    }

    [Fact]
    public async Task RecordAsyncUsesCachedLatestForIdempotentSnapshot()
    {
        var snapshot = CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
        {
            CreateEntry("repos.search", "h-d-1", "h-i-1")
        });

        using (var writer = new SqliteTrackedToolSchemaStore(_databasePath))
        {
            await writer.RecordAsync(
                snapshot,
                new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero),
                CancellationToken.None);
        }

        using var reader = new SqliteTrackedToolSchemaStore(_databasePath);
        var cached = await reader.GetLatestAsync("github", CancellationToken.None);
        Assert.NotNull(cached);

        // Mutate the row after the cache is warm. An idempotent RecordAsync should stay
        // cache-fronted instead of doing a serialized SQLite read on every response.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString()))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE tool_schema_versions SET upstream_url = 'https://elsewhere/' WHERE server_id = 'github';";
            update.ExecuteNonQuery();
        }

        var recorded = await reader.RecordAsync(
            snapshot,
            new DateTimeOffset(2026, 5, 5, 12, 5, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.False(recorded.WasNewVersion);
        Assert.False(recorded.UpstreamChanged);
        Assert.Equal("https://github-mcp/", cached!.UpstreamUrl);
        Assert.Equal(cached.Version, recorded.Version);
        Assert.Equal(cached.SnapshotHash, recorded.SnapshotHash);
        Assert.Equal(1, await CountRowsForServer(_databasePath, "github"));
    }

    [Fact]
    public async Task RecordAsyncResolvesUniqueConstraintRaceFromTwoStores()
    {
        using var a = new SqliteTrackedToolSchemaStore(_databasePath);
        using var b = new SqliteTrackedToolSchemaStore(_databasePath);

        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
        {
            CreateEntry("repos.search", "h-d-1", "h-i-1")
        });

        var taskA = Task.Run(() => a.RecordAsync(snapshot, capturedAt, CancellationToken.None).AsTask());
        var taskB = Task.Run(() => b.RecordAsync(snapshot, capturedAt, CancellationToken.None).AsTask());

        var results = await Task.WhenAll(taskA, taskB);

        // Exactly one new row should exist; both callers report version 1; one wasNew, one not.
        Assert.Equal(1, await CountRowsForServer(_databasePath, "github"));
        Assert.All(results, r => Assert.Equal(1, r.Version));
        Assert.Equal(1, results.Count(r => r.WasNewVersion));
        Assert.Equal(1, results.Count(r => !r.WasNewVersion));
        Assert.All(results, r => Assert.Equal(results[0].SnapshotHash, r.SnapshotHash));
    }

    [Fact]
    public async Task SchemaIncludesUniqueConstraintAndIndexes()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        await store.RecordAsync(
            CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
            {
                CreateEntry("repos.search", "h-d-1", "h-i-1")
            }),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();

        var indexes = QueryIndexNames(connection, "tool_schema_versions");
        Assert.Contains("idx_tsv_server_created", indexes);
        Assert.Contains("idx_tsv_server_version", indexes);

        // UNIQUE(server_id, version) enforced — second insert at version=1 fails.
        using var conflict = connection.CreateCommand();
        conflict.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='tool_schema_versions';";
        var ddl = (string)conflict.ExecuteScalar()!;
        Assert.Contains("UNIQUE (server_id, version)", ddl);
    }

    [Fact]
    public async Task RecordAsyncPersistsCrossReferenceColumns()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);

        var snapshot = CreateSnapshot(
            serverId: "github",
            upstreamUrl: "https://github-mcp/",
            mcpProtocol: "2025-11-25",
            tools: new[] { CreateEntry("repos.search", "h-d-1", "h-i-1") },
            policyVersion: "sha256:policy-abc",
            sourceCorrelationId: "corr-xyz");

        await store.RecordAsync(snapshot, new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT policy_version, source_correlation_id FROM tool_schema_versions WHERE server_id = 'github';";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("sha256:policy-abc", reader.GetString(0));
        Assert.Equal("corr-xyz", reader.GetString(1));
    }

    [Fact]
    public async Task RecordAsyncReportsUpstreamChangeWithoutWritingNewRow()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var snapshot = CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
        {
            CreateEntry("repos.search", "h-d-1", "h-i-1")
        });

        await store.RecordAsync(snapshot, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await store.RecordAsync(
            snapshot with { UpstreamUrl = "https://github-mcp-new/" },
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.False(second.WasNewVersion);
        Assert.True(second.UpstreamChanged);
        Assert.Equal("https://github-mcp/", second.PreviousUpstreamUrl);
        Assert.Equal("https://github-mcp-new/", second.CurrentUpstreamUrl);
        Assert.Equal(1, await CountRowsForServer(_databasePath, "github"));
    }

    [Fact]
    public async Task RecordAsyncReadOnlyDatabaseThrowsClassifiedWriteFailure()
    {
        using (var writable = new SqliteTrackedToolSchemaStore(_databasePath))
        {
            await writable.RecordAsync(
                CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
                {
                    CreateEntry("repos.search", "h-d-1", "h-i-1")
                }),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        using var readOnly = new SqliteTrackedToolSchemaStore(
            _databasePath,
            busyTimeoutMilliseconds: 50,
            openMode: SqliteOpenMode.ReadOnly);

        var ex = await Assert.ThrowsAsync<SchemaLockWriteFailedException>(() =>
            readOnly.RecordAsync(
                CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
                {
                    CreateEntry("repos.search", "h-d-2", "h-i-1")
                }),
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None).AsTask());

        Assert.Equal(SchemaLockWriteFailureReasons.DbReadonly, ex.Reason);
    }

    [Fact]
    public async Task RecordAsyncBusyDatabaseThrowsClassifiedWriteFailure()
    {
        using (var writable = new SqliteTrackedToolSchemaStore(_databasePath))
        {
            await writable.RecordAsync(
                CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
                {
                    CreateEntry("repos.search", "h-d-1", "h-i-1")
                }),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var update = blocker.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE tool_schema_versions SET upstream_url = upstream_url WHERE server_id = 'github';";
            await update.ExecuteNonQueryAsync();
        }

        using var store = new SqliteTrackedToolSchemaStore(_databasePath, busyTimeoutMilliseconds: 50);
        var ex = await Assert.ThrowsAsync<SchemaLockWriteFailedException>(() =>
            store.RecordAsync(
                CreateSnapshot("github", "https://github-mcp/", "2025-11-25", new[]
                {
                    CreateEntry("repos.search", "h-d-2", "h-i-1")
                }),
                DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None).AsTask());

        Assert.Equal(SchemaLockWriteFailureReasons.DbLocked, ex.Reason);
    }

    private static ToolSchemaSnapshotInput CreateSnapshot(
        string serverId,
        string upstreamUrl,
        string mcpProtocol,
        IEnumerable<ToolSchemaSnapshotEntry> tools,
        string? policyVersion = null,
        string? sourceCorrelationId = null) =>
        new(serverId, upstreamUrl, mcpProtocol, tools.ToArray(), policyVersion, sourceCorrelationId);

    private static ToolSchemaSnapshotEntry CreateEntry(
        string name,
        string descriptionHash,
        string inputSchemaHash) =>
        new(name, new ToolFingerprint(
            NameHash: $"sha256:n-{name}",
            TitleHash: null,
            DescriptionHash: $"sha256:{descriptionHash}",
            InputSchemaHash: $"sha256:{inputSchemaHash}",
            OutputSchemaHash: null));

    private static async Task<long> CountRowsForServer(string path, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tool_schema_versions WHERE server_id = $server_id;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static List<string> QueryIndexNames(SqliteConnection connection, string tableName)
    {
        var names = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=$tbl;";
        command.Parameters.AddWithValue("$tbl", tableName);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }
}

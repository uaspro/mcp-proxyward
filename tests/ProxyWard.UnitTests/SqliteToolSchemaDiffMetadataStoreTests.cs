using Microsoft.Data.Sqlite;
using ProxyWard.Locking.Persistence;

namespace ProxyWard.UnitTests;

public class SqliteToolSchemaDiffMetadataStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-diff-metadata-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in new[] { _databasePath, $"{_databasePath}-shm", $"{_databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best-effort cleanup */ }
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordAsyncCreatesSchemaAndStoresFullMetadata()
    {
        var review = await CreateReviewAsync();
        using var store = new SqliteToolSchemaDiffMetadataStore(_databasePath);
        var createdAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

        var row = await store.RecordAsync(
            new ToolSchemaDiffMetadataInput(
                DriftReviewId: review.Row.Id,
                BeforeJson: """{"description":"old"}""",
                AfterJson: """{"description":"new"}""",
                BeforeHash: "sha256:before",
                AfterHash: "sha256:after",
                CreatedAtUtc: createdAt),
            CancellationToken.None);

        Assert.True(row.Id > 0);
        Assert.Equal(review.Row.Id, row.DriftReviewId);
        Assert.Equal("""{"description":"old"}""", row.BeforeJson);
        Assert.Equal("""{"description":"new"}""", row.AfterJson);
        Assert.Equal("sha256:before", row.BeforeHash);
        Assert.Equal("sha256:after", row.AfterHash);
        Assert.Equal(createdAt, row.CreatedAtUtc);

        var loaded = await store.GetByDriftReviewIdAsync(review.Row.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(row.Id, loaded.Id);
    }

    [Fact]
    public async Task RecordAsyncStoresHashOnlyFallback()
    {
        var review = await CreateReviewAsync(fieldName: "schema");
        using var store = new SqliteToolSchemaDiffMetadataStore(_databasePath);

        var row = await store.RecordAsync(
            new ToolSchemaDiffMetadataInput(
                DriftReviewId: review.Row.Id,
                BeforeJson: null,
                AfterJson: null,
                BeforeHash: "sha256:before",
                AfterHash: "sha256:after",
                CreatedAtUtc: DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Null(row.BeforeJson);
        Assert.Null(row.AfterJson);
        Assert.Equal("sha256:before", row.BeforeHash);
        Assert.Equal("sha256:after", row.AfterHash);
    }

    [Fact]
    public async Task RecordAsyncIsIdempotentForSameReview()
    {
        var review = await CreateReviewAsync();
        using var store = new SqliteToolSchemaDiffMetadataStore(_databasePath);

        var first = await store.RecordAsync(
            new ToolSchemaDiffMetadataInput(
                review.Row.Id,
                """{"description":"old"}""",
                """{"description":"new"}""",
                "sha256:before",
                "sha256:after",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var second = await store.RecordAsync(
            new ToolSchemaDiffMetadataInput(
                review.Row.Id,
                """{"description":"changed"}""",
                """{"description":"changed"}""",
                "sha256:other-before",
                "sha256:other-after",
                DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.BeforeJson, second.BeforeJson);
        Assert.Equal(first.AfterJson, second.AfterJson);
        Assert.Equal(first.BeforeHash, second.BeforeHash);
        Assert.Equal(first.AfterHash, second.AfterHash);
    }

    [Fact]
    public async Task RecordAsyncRejectsInvalidInput()
    {
        using var store = new SqliteToolSchemaDiffMetadataStore(_databasePath);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.RecordAsync(
                new ToolSchemaDiffMetadataInput(0, null, null, "sha256:before", "sha256:after", DateTimeOffset.UtcNow),
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordAsync(
                new ToolSchemaDiffMetadataInput(1, null, null, "", "sha256:after", DateTimeOffset.UtcNow),
                CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordAsync(
                new ToolSchemaDiffMetadataInput(1, null, null, "sha256:before", "", DateTimeOffset.UtcNow),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RecordAsyncOnReadOnlyDbThrowsSchemaLockWriteFailedException()
    {
        var review = await CreateReviewAsync();
        using (var bootstrap = new SqliteToolSchemaDiffMetadataStore(_databasePath))
        {
            await bootstrap.RecordAsync(
                new ToolSchemaDiffMetadataInput(
                    review.Row.Id,
                    null,
                    null,
                    "sha256:before",
                    "sha256:after",
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        using var readOnly = new SqliteToolSchemaDiffMetadataStore(
            _databasePath,
            openMode: SqliteOpenMode.ReadOnly);

        var ex = await Assert.ThrowsAsync<SchemaLockWriteFailedException>(() =>
            readOnly.RecordAsync(
                new ToolSchemaDiffMetadataInput(
                    review.Row.Id,
                    null,
                    null,
                    "sha256:before",
                    "sha256:after",
                    DateTimeOffset.UtcNow),
                CancellationToken.None).AsTask());
        Assert.Equal(SchemaLockWriteFailureReasons.DbReadonly, ex.Reason);
    }

    private async Task<DriftReviewRecordResult> CreateReviewAsync(string fieldName = "description")
    {
        using var reviewStore = new SqliteSchemaDriftReviewStore(_databasePath);
        return await reviewStore.RecordObservationAsync(
            new DriftReviewObservation(
                ServerId: "github",
                ToolName: "repos.search",
                FieldName: fieldName,
                FromVersion: 1,
                ToVersion: 2,
                Reasons: ["tool_description_changed"],
                PolicyVersion: "sha256:policy",
                DetectedAtUtc: new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
    }
}

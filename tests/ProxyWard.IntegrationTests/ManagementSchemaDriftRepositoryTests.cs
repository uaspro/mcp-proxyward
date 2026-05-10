using ProxyWard.Locking.Persistence;
using ProxyWard.Management.Api.Audit;
using ProxyWard.Management.Api.Drift;

namespace ProxyWard.IntegrationTests;

public class ManagementSchemaDriftRepositoryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-drift-repo-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        DeleteDbFiles(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task QueryAsyncReturnsEmptyPageWhenDriftTableIsNotInitialized()
    {
        await CreateEmptyDatabaseAsync();

        var repository = new ManagementSchemaDriftRepository(_databasePath);
        var page = await repository.QueryAsync(
            new ManagementSchemaDriftQuery(
                FromUtc: new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
                ToUtc: new DateTimeOffset(2026, 5, 10, 11, 0, 0, TimeSpan.Zero),
                Status: "pending",
                Offset: 5,
                PageSize: 10),
            CancellationToken.None);

        Assert.Equal(5, page.Offset);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Equal(new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero), page.Window.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 10, 11, 0, 0, TimeSpan.Zero), page.Window.ToUtc);
    }

    [Fact]
    public async Task GetByIdAsyncReturnsNullWhenDriftTableIsNotInitialized()
    {
        await CreateEmptyDatabaseAsync();

        var repository = new ManagementSchemaDriftRepository(_databasePath);
        var detail = await repository.GetByIdAsync(1, null, null, CancellationToken.None);

        Assert.Null(detail);
    }

    [Fact]
    public async Task QueryAsyncReturnsNewestFirstWithFiltersAndImpactCount()
    {
        var first = await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        var second = await SeedReviewAsync(
            "alpha", "repos.search", "schema", "approved",
            new DateTimeOffset(2026, 5, 10, 10, 5, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "beta", "repos.search", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 10, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "alpha", "repos.search", "schema", "blocked",
            new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));

        var repository = new ManagementSchemaDriftRepository(_databasePath);
        var page = await repository.QueryAsync(
            new ManagementSchemaDriftQuery(
                FromUtc: new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
                ToUtc: new DateTimeOffset(2026, 5, 10, 10, 6, 0, TimeSpan.Zero),
                ServerId: "alpha",
                ToolName: "repos.search",
                PageSize: 10),
            CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(10, page.PageSize);
        Assert.Equal([second.Row.Id, first.Row.Id], page.Items.Select(item => item.Id).ToArray());
        Assert.All(page.Items, item => Assert.Equal(2, item.ImpactCount));
    }

    [Fact]
    public async Task QueryAsyncAppliesStatusFilterAndPageSizeCap()
    {
        await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "alpha", "repos.update", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 1, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "alpha", "repos.delete", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 2, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "alpha", "repos.search", "schema", "approved",
            new DateTimeOffset(2026, 5, 10, 10, 3, 0, TimeSpan.Zero));

        var repository = new ManagementSchemaDriftRepository(
            _databasePath,
            new ManagementAuditReadOptions(MaxPageSize: 2));
        var page = await repository.QueryAsync(
            new ManagementSchemaDriftQuery(Status: "pending", PageSize: 99),
            CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal("pending", item.Status));
    }

    [Fact]
    public async Task GetByIdAsyncReturnsDetailWithReadableDiffMetadata()
    {
        var review = await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        await SeedDiffMetadataAsync(
            review.Row.Id,
            """{"description":"old"}""",
            """{"description":"new"}""");

        var repository = new ManagementSchemaDriftRepository(_databasePath);
        var detail = await repository.GetByIdAsync(review.Row.Id, null, null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(review.Row.Id, detail.Id);
        Assert.True(detail.HasDiffMetadata);
        Assert.Equal("metadata", detail.DiffMode);
        Assert.Equal("metadata", detail.Diff.Mode);
        Assert.Equal("""{"description":"old"}""", detail.Diff.BeforeJson);
        Assert.Equal("""{"description":"new"}""", detail.Diff.AfterJson);
        Assert.StartsWith("sha256:", detail.Diff.BeforeHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", detail.Diff.AfterHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetByIdAsyncReturnsHashFallbackWhenDiffMetadataIsMissing()
    {
        var review = await SeedReviewAsync(
            "alpha", "repos.search", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        var repository = new ManagementSchemaDriftRepository(_databasePath);
        var detail = await repository.GetByIdAsync(review.Row.Id, null, null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.False(detail.HasDiffMetadata);
        Assert.Equal("hash", detail.DiffMode);
        Assert.Equal("hash", detail.Diff.Mode);
        Assert.Null(detail.Diff.BeforeJson);
        Assert.Null(detail.Diff.AfterJson);
        Assert.Equal("unavailable", detail.Diff.BeforeHash);
        Assert.Equal("unavailable", detail.Diff.AfterHash);
    }

    private async Task<DriftReviewRecordResult> SeedReviewAsync(
        string serverId,
        string toolName,
        string fieldName,
        string status,
        DateTimeOffset detectedAtUtc)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var result = await store.RecordObservationAsync(
            new DriftReviewObservation(
                ServerId: serverId,
                ToolName: toolName,
                FieldName: fieldName,
                FromVersion: 1,
                ToVersion: 2,
                Reasons: [$"tool_{fieldName}_changed"],
                PolicyVersion: "sha256:policy",
                DetectedAtUtc: detectedAtUtc),
            CancellationToken.None);

        await UpdateStatusAsync(result.Row.Id, status);
        return result;
    }

    private async Task SeedDiffMetadataAsync(long driftReviewId, string? beforeJson, string? afterJson)
    {
        using var store = new SqliteToolSchemaDiffMetadataStore(_databasePath);
        await store.RecordAsync(
            new ToolSchemaDiffMetadataInput(
                driftReviewId,
                beforeJson,
                afterJson,
                "sha256:before",
                "sha256:after",
                new DateTimeOffset(2026, 5, 10, 10, 30, 0, TimeSpan.Zero)),
            CancellationToken.None);
    }

    private async Task UpdateStatusAsync(long id, string status)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE schema_drift_reviews SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateEmptyDatabaseAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
            }.ToString());
        await connection.OpenAsync();
    }

    private static void DeleteDbFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best-effort cleanup */ }
            }
        }
    }
}

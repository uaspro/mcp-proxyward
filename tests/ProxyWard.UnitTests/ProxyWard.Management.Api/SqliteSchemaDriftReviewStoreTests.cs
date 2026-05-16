using Microsoft.Data.Sqlite;
using ProxyWard.Locking.Persistence;

namespace ProxyWard.UnitTests;

public class SqliteSchemaDriftReviewStoreTests : IAsyncLifetime
{
    private readonly string _databasePath = TestSqliteFiles.NewPath("proxyward-drift");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TestSqliteFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordObservationAsyncOnEmptyDbCreatesSchemaAndPendingRow()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);

        var detectedAt = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var observation = CreateObservation(detectedAt: detectedAt);

        var result = await store.RecordObservationAsync(observation, CancellationToken.None);

        Assert.True(result.WasNewlyCreated);
        Assert.True(result.Row.Id > 0);
        Assert.Equal(DriftReviewStatus.Pending, result.Row.Status);
        Assert.Equal("github", result.Row.ServerId);
        Assert.Equal("repos.search", result.Row.ToolName);
        Assert.Equal("description", result.Row.FieldName);
        Assert.Equal(1, result.Row.FromVersion);
        Assert.Equal(2, result.Row.ToVersion);
        Assert.Equal(detectedAt, result.Row.DetectedAtUtc);
        Assert.Equal("policy-1", result.Row.PolicyVersion);
        Assert.Null(result.Row.ReviewedAtUtc);
        Assert.Null(result.Row.ReviewedBy);
        Assert.Null(result.Row.ReviewNote);
    }

    [Fact]
    public async Task RecordObservationAsyncDuplicateObservationReturnsExistingRow()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation();

        var first = await store.RecordObservationAsync(observation, CancellationToken.None);
        var second = await store.RecordObservationAsync(observation, CancellationToken.None);

        Assert.True(first.WasNewlyCreated);
        Assert.False(second.WasNewlyCreated);
        Assert.Equal(first.Row.Id, second.Row.Id);
    }

    [Fact]
    public async Task RecordObservationAsyncDifferentFieldNameCreatesSeparateRow()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);

        var description = await store.RecordObservationAsync(CreateObservation(fieldName: "description"), CancellationToken.None);
        var inputSchema = await store.RecordObservationAsync(CreateObservation(fieldName: "inputSchema"), CancellationToken.None);

        Assert.True(description.WasNewlyCreated);
        Assert.True(inputSchema.WasNewlyCreated);
        Assert.NotEqual(description.Row.Id, inputSchema.Row.Id);
    }

    [Fact]
    public async Task RecordObservationAsyncPreservesApprovedStatusUnderDuplicateObservation()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation();

        var first = await store.RecordObservationAsync(observation, CancellationToken.None);
        ManuallyUpdateStatus(_databasePath, first.Row.Id, "approved");

        var second = await store.RecordObservationAsync(observation, CancellationToken.None);

        Assert.False(second.WasNewlyCreated);
        Assert.Equal(first.Row.Id, second.Row.Id);
        Assert.Equal(DriftReviewStatus.Approved, second.Row.Status);
    }

    [Fact]
    public async Task RecordObservationAsyncRoundTripsCommaSeparatedReasons()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation(reasons: ["description_changed", "input_schema_changed"]);

        var first = await store.RecordObservationAsync(observation, CancellationToken.None);

        Assert.Equal(new[] { "description_changed", "input_schema_changed" }, first.Row.Reasons.ToArray());
    }

    [Fact]
    public async Task GetByServerAsyncReturnsRowsNewestFirst()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);

        var older = await store.RecordObservationAsync(
            CreateObservation(fieldName: "description", detectedAt: new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
        var newer = await store.RecordObservationAsync(
            CreateObservation(fieldName: "inputSchema", detectedAt: new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var rows = await store.GetByServerAsync("github", CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal(newer.Row.Id, rows[0].Id);
        Assert.Equal(older.Row.Id, rows[1].Id);
    }

    [Fact]
    public async Task GetByServerAsyncReturnsEmptyForUnknownServer()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        await store.RecordObservationAsync(CreateObservation(serverId: "github"), CancellationToken.None);

        var rows = await store.GetByServerAsync("unknown", CancellationToken.None);

        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("", "tool", "field")]
    [InlineData(" ", "tool", "field")]
    [InlineData("server", "", "field")]
    [InlineData("server", "tool", "")]
    public async Task RecordObservationAsyncRejectsEmptyKeyFields(string serverId, string toolName, string fieldName)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation(serverId: serverId, toolName: toolName, fieldName: fieldName);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordObservationAsync(observation, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RecordObservationAsyncRejectsEmptyReasons()
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation(reasons: []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordObservationAsync(observation, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("description_changed,input_schema_changed")]
    public async Task RecordObservationAsyncRejectsInvalidReasons(string reason)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation(reasons: [reason]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.RecordObservationAsync(observation, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public async Task RecordObservationAsyncRejectsInvalidVersionPair(int fromVersion, int toVersion)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var observation = CreateObservation(fromVersion: fromVersion, toVersion: toVersion);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.RecordObservationAsync(observation, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task RecordObservationAsyncOnReadOnlyDbThrowsSchemaLockWriteFailedException()
    {
        // Bootstrap the DB with a writer first so the file/schema exist.
        using (var bootstrapStore = new SqliteSchemaDriftReviewStore(_databasePath))
        {
            await bootstrapStore.RecordObservationAsync(CreateObservation(), CancellationToken.None);
        }

        using var readOnlyStore = new SqliteSchemaDriftReviewStore(
            _databasePath,
            openMode: SqliteOpenMode.ReadOnly);
        var observation = CreateObservation(fieldName: "inputSchema");

        var ex = await Assert.ThrowsAsync<SchemaLockWriteFailedException>(
            () => readOnlyStore.RecordObservationAsync(observation, CancellationToken.None).AsTask());
        Assert.Equal(SchemaLockWriteFailureReasons.DbReadonly, ex.Reason);
    }

    private static DriftReviewObservation CreateObservation(
        string serverId = "github",
        string toolName = "repos.search",
        string fieldName = "description",
        int fromVersion = 1,
        int toVersion = 2,
        IReadOnlyCollection<string>? reasons = null,
        string? policyVersion = "policy-1",
        DateTimeOffset? detectedAt = null) =>
        new(
            ServerId: serverId,
            ToolName: toolName,
            FieldName: fieldName,
            FromVersion: fromVersion,
            ToVersion: toVersion,
            Reasons: reasons ?? ["description_changed"],
            PolicyVersion: policyVersion,
            DetectedAtUtc: detectedAt ?? new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));

    private static void ManuallyUpdateStatus(string dbPath, long rowId, string newStatus)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE schema_drift_reviews SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", newStatus);
        command.Parameters.AddWithValue("$id", rowId);
        command.ExecuteNonQuery();
    }
}

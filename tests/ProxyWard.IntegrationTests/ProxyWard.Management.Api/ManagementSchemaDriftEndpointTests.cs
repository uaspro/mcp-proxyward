using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyWard.Locking.Persistence;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementSchemaDriftEndpointTests : IAsyncLifetime
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string AdminTokenEnv = "PROXYWARD_MANAGEMENT_ADMIN_TOKEN";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-drift-api-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TestFiles.DeleteSqlite(_databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DriftListEndpointReturnsEmptyPageWhenDriftTableIsNotInitialized()
    {
        await CreateEmptyDatabaseAsync();

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(
                "/api/schema/drifts?status=pending&fromUtc=2026-05-10T10%3A00%3A00Z&toUtc=2026-05-10T11%3A00%3A00Z&pageSize=10");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);
            var root = payload.RootElement;

            Assert.Equal(0, root.GetProperty("totalCount").GetInt64());
            Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
            Assert.Empty(root.GetProperty("items").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task DriftListEndpointAppliesFiltersAndReturnsImpactCount()
    {
        var first = await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        var second = await SeedReviewAsync(
            "alpha", "repos.search", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 5, 0, TimeSpan.Zero));
        await SeedReviewAsync(
            "beta", "repos.search", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 6, 0, TimeSpan.Zero));
        await SeedDiffMetadataAsync(first.Row.Id, """{"description":"old"}""", """{"description":"new"}""");
        await SeedDiffMetadataAsync(second.Row.Id, null, null);

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(
                "/api/schema/drifts?status=pending&serverId=alpha&toolName=repos.search"
                + "&fromUtc=2026-05-10T10%3A00%3A00Z&toUtc=2026-05-10T10%3A05%3A30Z&pageSize=10");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);
            var root = payload.RootElement;

            Assert.Equal(2, root.GetProperty("totalCount").GetInt64());
            Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
            Assert.Equal("2026-05-10T10:00:00+00:00", root.GetProperty("window").GetProperty("fromUtc").GetString());

            var items = root.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal([second.Row.Id, first.Row.Id], items.Select(item => item.GetProperty("id").GetInt64()).ToArray());
            Assert.All(items, item => Assert.Equal(2, item.GetProperty("impactCount").GetInt64()));
            Assert.Equal("hash", items[0].GetProperty("diffMode").GetString());
            Assert.Equal("metadata", items[1].GetProperty("diffMode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task DriftDetailEndpointReturnsDiffMetadata()
    {
        var review = await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        await SeedDiffMetadataAsync(review.Row.Id, """{"description":"old"}""", """{"description":"new"}""");

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync($"/api/schema/drifts/{review.Row.Id}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);
            var root = payload.RootElement;

            Assert.Equal(review.Row.Id, root.GetProperty("id").GetInt64());
            Assert.Equal("metadata", root.GetProperty("diffMode").GetString());
            Assert.True(root.GetProperty("hasDiffMetadata").GetBoolean());

            var diff = root.GetProperty("diff");
            Assert.Equal("metadata", diff.GetProperty("mode").GetString());
            Assert.Equal("""{"description":"old"}""", diff.GetProperty("beforeJson").GetString());
            Assert.Equal("""{"description":"new"}""", diff.GetProperty("afterJson").GetString());
            Assert.StartsWith("sha256:", diff.GetProperty("beforeHash").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("sha256:", diff.GetProperty("afterHash").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task DriftDetailEndpointReturnsNotFoundForMissingItem()
    {
        await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/schema/drifts/9999");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);
            Assert.Equal("schema_drift_not_found", payload.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task DriftListEndpointReturnsBadRequestForInvalidStatus()
    {
        await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/schema/drifts?status=waiting");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task DriftActionEndpointRequiresAdminToken()
    {
        var review = await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                $"/api/schema/drifts/{review.Row.Id}/approve",
                new StringContent("""{"reviewedBy":"alice"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("pending", await ReadReviewStatusAsync(review.Row.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        }
    }

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    [InlineData("block", "blocked")]
    public async Task DriftActionEndpointUpdatesStatusAndWritesAuditEvent(string action, string expectedStatus)
    {
        var review = await SeedReviewAsync(
            "alpha", "repos.search", "schema", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-token");

            using var response = await client.PostAsync(
                $"/api/schema/drifts/{review.Row.Id}/{action}",
                new StringContent("""{"reviewedBy":"alice","reviewNote":"reviewed"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);
            var root = payload.RootElement;

            Assert.Equal(review.Row.Id, root.GetProperty("id").GetInt64());
            Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
            Assert.Equal("alice", root.GetProperty("reviewedBy").GetString());
            Assert.Equal("reviewed", root.GetProperty("reviewNote").GetString());
            Assert.Equal(expectedStatus, await ReadReviewStatusAsync(review.Row.Id));

            var audit = Assert.Single(await ReadActionAuditRowsAsync());
            Assert.Equal("schema_drift_review_action", audit.EventType);
            Assert.Equal("management", audit.Mode);
            Assert.Equal("allow", audit.Decision);
            Assert.Equal("alpha", audit.ServerId);
            Assert.Equal($"schema/drifts/{action}", audit.Method);
            Assert.Equal("repos.search", audit.ToolName);
            Assert.Equal($"schema_drift_review_{expectedStatus}", audit.Reasons);
            Assert.Contains($"""reviewId":{review.Row.Id}""", audit.PayloadJson, StringComparison.Ordinal);
            Assert.Contains($"\"action\":\"{action}\"", audit.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("alice", audit.PayloadJson, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        }
    }

    [Fact]
    public async Task DriftActionEndpointReturnsNotFoundForMissingItem()
    {
        await SeedReviewAsync(
            "alpha", "repos.search", "description", "pending",
            new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-token");

            using var response = await client.PostAsync(
                "/api/schema/drifts/9999/block",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        }
    }

    [Fact]
    public async Task DriftActionEndpointReturnsNotFoundWhenDriftTableIsNotInitialized()
    {
        await CreateEmptyDatabaseAsync();

        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-admin-token");

            using var response = await client.PostAsync(
                "/api/schema/drifts/9999/block",
                new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        }
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

    private async Task<string> ReadReviewStatusAsync(long id)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM schema_drift_reviews WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<ActionAuditRow>> ReadActionAuditRowsAsync()
    {
        var rows = new List<ActionAuditRow>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
            }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, mode, decision, server_id, method, tool_name, reasons, payload_json
            FROM audit_events
            WHERE event_type = 'schema_drift_review_action'
            ORDER BY id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ActionAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return rows;
    }

    private sealed record ActionAuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PayloadJson);
}

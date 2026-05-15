using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementAuditEventEndpointTests
{
    [Fact]
    public async Task AuditEventsEndpointReturnsDecisionKindsNewestFirst()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/events?pageSize=10");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal(4, root.GetProperty("totalCount").GetInt64());
            Assert.Equal(10, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(
                new[] { "warn", "would_block", "block", "allow" },
                root.GetProperty("items")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("decision").GetString())
                    .ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task AuditEventsEndpointAppliesFiltersAndPagination()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/events?serverId=beta&method=tools/call&pageSize=1");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal(2, root.GetProperty("totalCount").GetInt64());
            Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
            var item = Assert.Single(root.GetProperty("items").EnumerateArray());
            Assert.Equal("beta", item.GetProperty("serverId").GetString());
            Assert.Equal("tools/call", item.GetProperty("method").GetString());
            Assert.Equal("would_block", item.GetProperty("decision").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task AuditEventDetailEndpointReturnsMetadataAndRedactedSummary()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/events/2");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal(2, root.GetProperty("id").GetInt64());
            Assert.Equal("block", root.GetProperty("decision").GetString());
            Assert.Equal("path_traversal", root.GetProperty("reasons")[0].GetString());
            Assert.Equal(20, root.GetProperty("durationMs").GetInt64());
            Assert.Equal("[redacted]", root.GetProperty("argumentSummary").GetProperty("token").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task AuditEventDetailEndpointReturnsNotFoundForMissingEvent()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/events/9999");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task AuditEventsEndpointToleratesMalformedAuditPayloads()
    {
        var dbPath = TempDbPath();
        await SeedMalformedRowAsync(dbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var listResponse = await client.GetAsync("/api/audit/events?pageSize=10");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

            await using (var stream = await listResponse.Content.ReadAsStreamAsync())
            using (var payload = await JsonDocument.ParseAsync(stream))
            {
                var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("unknown", item.GetProperty("eventType").GetString());
                Assert.Equal("unknown", item.GetProperty("decision").GetString());
                Assert.Equal(JsonValueKind.Null, item.GetProperty("argumentSummary").ValueKind);
                Assert.Equal(DateTimeOffset.UnixEpoch, item.GetProperty("timestampUtc").GetDateTimeOffset());
            }

            using var detailResponse = await client.GetAsync("/api/audit/events/1");
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    private static async Task SeedDecisionRowsAsync(string dbPath)
    {
        using var sink = new SqliteAuditSink(dbPath);
        await sink.WriteAsync(CreateEvent(1, AuditDecision.Allow, "alpha", "tools/list", null, ["allowed"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(2, AuditDecision.Block, "beta", "tools/call", "fs.read", ["path_traversal"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(3, AuditDecision.WouldBlock, "beta", "tools/call", "net.fetch", ["private_network"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(4, AuditDecision.Warn, "gamma", "tools/list", null, ["schema_drift"]), CancellationToken.None);
    }

    private static async Task SeedMalformedRowAsync(string dbPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync();
        await SqliteAuditSchema.ConfigureWriteConnectionAsync(connection, CancellationToken.None);
        await SqliteAuditSchema.EnsureSchemaAsync(connection, CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (
                timestamp_utc,
                event_type,
                mode,
                decision,
                server_id,
                method,
                tool_name,
                reasons,
                policy_version,
                correlation_id,
                request_bytes,
                duration_ms,
                payload_json
            ) VALUES (
                'not-a-timestamp',
                '',
                '',
                '',
                '',
                NULL,
                NULL,
                '',
                '',
                '',
                0,
                0,
                '{not-json'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static AuditEvent CreateEvent(
        int index,
        AuditDecision decision,
        string serverId,
        string? method,
        string? toolName,
        IReadOnlyCollection<string> reasons) =>
        new(
            Timestamp: Timestamp.AddMinutes(index),
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: decision,
            ServerId: serverId,
            Method: method,
            ToolName: toolName,
            Reasons: reasons,
            PolicyVersion: $"policy-{index}",
            CorrelationId: $"corr-{index}",
            RequestBytes: index,
            DurationMs: index * 10,
            ArgumentSummary: JsonNode.Parse("""{"token":"[redacted]"}"""),
            BatchSize: 0);

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-management-audit-api-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Timestamp = new(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);
}

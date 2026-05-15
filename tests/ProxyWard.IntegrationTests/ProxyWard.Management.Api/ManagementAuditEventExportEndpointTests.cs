using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementAuditEventExportEndpointTests
{
    private const string PersistenceDbEnv = "PROXYWARD_DB_PATH";
    private const string MaxExportRowsEnv = "PROXYWARD_MANAGEMENT_AUDIT_MAX_EXPORT_ROWS";

    [Fact]
    public async Task ExportEndpointReturnsNdjsonContentTypeAndOneJsonObjectPerLineNewestFirst()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/export.ndjson");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-cache", Assert.Single(response.Headers.Pragma).Name);
            Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions));
            Assert.Equal("nosniff", Assert.Single(contentTypeOptions));
            Assert.Equal(
                "attachment",
                response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.False(string.IsNullOrWhiteSpace(response.Content.Headers.ContentDisposition?.FileName));

            var body = await response.Content.ReadAsStringAsync();
            Assert.False(body.StartsWith("[", StringComparison.Ordinal));
            Assert.False(body.TrimEnd().EndsWith("]", StringComparison.Ordinal));

            var lines = SplitNdjsonLines(body);
            Assert.Equal(4, lines.Length);
            Assert.Equal(
                new[] { "warn", "would_block", "block", "allow" },
                lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("decision").GetString()).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
        }
    }

    [Fact]
    public async Task ExportEndpointAppliesSameFiltersAsQueryEndpoint()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var queryResponse = await client.GetAsync("/api/audit/events?serverId=beta&method=tools/call&pageSize=200");
            Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
            await using var queryStream = await queryResponse.Content.ReadAsStreamAsync();
            using var queryPayload = await JsonDocument.ParseAsync(queryStream);
            var queryIds = queryPayload.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetInt64())
                .ToArray();

            using var exportResponse = await client.GetAsync("/api/audit/export.ndjson?serverId=beta&method=tools/call");
            Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
            var exportBody = await exportResponse.Content.ReadAsStringAsync();
            var exportIds = SplitNdjsonLines(exportBody)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt64())
                .ToArray();

            Assert.NotEmpty(exportIds);
            Assert.Equal(queryIds, exportIds);
            Assert.All(exportIds, id => Assert.Contains(id, new long[] { 2L, 3L }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
        }
    }

    [Fact]
    public async Task ExportEndpointCapsRowsByConfiguredMaxExportRowCount()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);
        Environment.SetEnvironmentVariable(MaxExportRowsEnv, "2");

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/export.ndjson");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var lines = SplitNdjsonLines(body);

            Assert.Equal(2, lines.Length);
            Assert.Equal(
                new[] { "warn", "would_block" },
                lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("decision").GetString()).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
            Environment.SetEnvironmentVariable(MaxExportRowsEnv, null);
        }
    }

    [Fact]
    public async Task ExportEndpointReturnsRedactedArgumentSummaryOnly()
    {
        var dbPath = TempDbPath();
        await SeedDecisionRowsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/export.ndjson");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var lines = SplitNdjsonLines(body);

            Assert.NotEmpty(lines);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var argumentSummary = document.RootElement.GetProperty("argumentSummary");
                Assert.Equal("[redacted]", argumentSummary.GetProperty("token").GetString());
                Assert.False(document.RootElement.TryGetProperty("payloadJson", out _));
                Assert.False(document.RootElement.TryGetProperty("payload_json", out _));
            }

            Assert.DoesNotContain("secret-token", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
        }
    }

    private static string[] SplitNdjsonLines(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static async Task SeedDecisionRowsAsync(string dbPath)
    {
        using var sink = new SqliteAuditSink(dbPath);
        await sink.WriteAsync(CreateEvent(1, AuditDecision.Allow, "alpha", "tools/list", null, ["allowed"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(2, AuditDecision.Block, "beta", "tools/call", "fs.read", ["path_traversal"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(3, AuditDecision.WouldBlock, "beta", "tools/call", "net.fetch", ["private_network"]), CancellationToken.None);
        await sink.WriteAsync(CreateEvent(4, AuditDecision.Warn, "gamma", "tools/list", null, ["schema_drift"]), CancellationToken.None);
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
        Path.Combine(Path.GetTempPath(), $"proxyward-management-audit-export-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Timestamp = new(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);
}

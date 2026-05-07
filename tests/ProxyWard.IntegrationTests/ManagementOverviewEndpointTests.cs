using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementOverviewEndpointTests
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string MaxSampleSizeEnv = "PROXYWARD_MANAGEMENT_OVERVIEW_MAX_SAMPLE_SIZE";

    [Fact]
    public async Task OverviewEndpointReturnsCountsRatesP95SeriesAndMetadata()
    {
        var dbPath = TempDbPath();
        var windowFrom = new DateTimeOffset(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);
        var windowTo = windowFrom.AddSeconds(120);

        await SeedFixedRowsAsync(dbPath, windowFrom);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            var url = $"/api/overview?fromUtc={IsoZ(windowFrom)}&toUtc={IsoZ(windowTo)}&bucketSeconds=10";
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            // Window
            Assert.Equal(120.0, root.GetProperty("window").GetProperty("durationSeconds").GetDouble());

            // Bucket
            Assert.Equal(10, root.GetProperty("bucket").GetProperty("sizeSeconds").GetInt32());
            Assert.Equal(12, root.GetProperty("bucket").GetProperty("count").GetInt32());

            // Metadata
            var metadata = root.GetProperty("metadata");
            Assert.Equal("audit-db", metadata.GetProperty("source").GetString());
            Assert.False(metadata.GetProperty("partial").GetBoolean());
            Assert.False(string.IsNullOrEmpty(metadata.GetProperty("asOfUtc").GetString()));

            // Rates
            Assert.Equal(4.0 / 120.0, root.GetProperty("requestRate").GetDouble(), 5);
            Assert.Equal(0.25, root.GetProperty("blockRate").GetDouble(), 5);
            Assert.Equal(0.25, root.GetProperty("wouldBlockRate").GetDouble(), 5);
            Assert.Equal(0.5, root.GetProperty("errorRate").GetDouble(), 5);

            // Latency p95: durations [10,20,30,40], nearest-rank index = ceil(0.95*4)-1 = 3 → 40
            Assert.Equal(40, root.GetProperty("latencyP95Ms").GetInt64());

            // Top reasons: path_traversal, private_network, schema_drift each 1 (allowed excluded)
            var topReasonKeys = root.GetProperty("topReasons")
                .EnumerateArray()
                .Select(e => e.GetProperty("key").GetString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "path_traversal", "private_network", "schema_drift" }, topReasonKeys);

            // Top tools: fs.read, net.fetch (NULL tool_name rows excluded)
            var topToolKeys = root.GetProperty("topTools")
                .EnumerateArray()
                .Select(e => e.GetProperty("key").GetString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "fs.read", "net.fetch" }, topToolKeys);

            // Series: 12 buckets with events at offsets 15, 25, 35, 55 → buckets 1, 2, 3, 5; rest empty.
            var series = root.GetProperty("series").EnumerateArray()
                .Select(b => b.GetProperty("total").GetInt32())
                .ToArray();
            Assert.Equal(new[] { 0, 1, 1, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, series);

            // Spot-check decisions in series (bucket index 1 has allow=1, bucket 2 has block=1, etc.)
            var bucketArray = root.GetProperty("series").EnumerateArray().ToArray();
            Assert.Equal(1, bucketArray[1].GetProperty("allow").GetInt32());
            Assert.Equal(1, bucketArray[2].GetProperty("block").GetInt32());
            Assert.Equal(1, bucketArray[3].GetProperty("wouldBlock").GetInt32());
            Assert.Equal(1, bucketArray[5].GetProperty("warn").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task OverviewEndpointSetsPartialTrueWhenNoEventsInWindow()
    {
        var dbPath = TempDbPath();
        var windowStart = new DateTimeOffset(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);

        // Seed rows OLDER than the window start.
        await SeedSingleRowAsync(dbPath, windowStart.AddHours(-2), AuditDecision.Allow);

        var queryFrom = windowStart;
        var queryTo = windowStart.AddSeconds(120);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            var url = $"/api/overview?fromUtc={IsoZ(queryFrom)}&toUtc={IsoZ(queryTo)}&bucketSeconds=10";
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            var metadata = root.GetProperty("metadata");
            Assert.True(metadata.GetProperty("partial").GetBoolean());
            Assert.Contains("no audit events in window",
                metadata.GetProperty("notes").GetString(),
                StringComparison.Ordinal);

            Assert.Equal(0.0, root.GetProperty("requestRate").GetDouble());
            Assert.Equal(0.0, root.GetProperty("blockRate").GetDouble());
            Assert.Equal(0.0, root.GetProperty("errorRate").GetDouble());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("latencyP95Ms").ValueKind);
            Assert.Empty(root.GetProperty("topReasons").EnumerateArray());
            Assert.Empty(root.GetProperty("topTools").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task OverviewEndpointSignalsIngestLagWhenNewestInWindowIsOlderThanThreshold()
    {
        var dbPath = TempDbPath();

        // Anchor the window to "now" so the lag check (which compares against now − 2×bucket
        // and requires the window to end near now) is in scope. Use a 1-hour window ending
        // just past "now" with bucketSeconds=60.
        var now = DateTimeOffset.UtcNow;
        var windowFrom = now.AddMinutes(-30);
        var windowTo = now.AddSeconds(30);

        // Seed an older-than-window row to make hasOlderRows true.
        await SeedSingleRowAsync(dbPath, windowFrom.AddDays(-1), AuditDecision.Allow);

        // Seed an in-window row whose timestamp is well past now − 2×bucket (= now − 120s).
        using (var sink = new SqliteAuditSink(dbPath))
        {
            await sink.WriteAsync(MakeEvent(
                timestamp: now.AddMinutes(-15),
                decision: AuditDecision.Allow,
                serverId: "alpha",
                method: "tools/list",
                toolName: null,
                reason: "allowed",
                durationMs: 5,
                correlationId: "corr-in-window"), CancellationToken.None);
        }

        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            var url = $"/api/overview?fromUtc={IsoZ(windowFrom)}&toUtc={IsoZ(windowTo)}&bucketSeconds=60";
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            var metadata = root.GetProperty("metadata");
            Assert.True(metadata.GetProperty("partial").GetBoolean());
            Assert.Contains("ingest may be lagging",
                metadata.GetProperty("notes").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task OverviewEndpointSetsLatencyNullAndPartialWhenSampleExceedsCap()
    {
        var dbPath = TempDbPath();
        var windowFrom = new DateTimeOffset(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);
        var windowTo = windowFrom.AddSeconds(120);

        await SeedFixedRowsAsync(dbPath, windowFrom);

        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);
        Environment.SetEnvironmentVariable(MaxSampleSizeEnv, "2");

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            var url = $"/api/overview?fromUtc={IsoZ(windowFrom)}&toUtc={IsoZ(windowTo)}&bucketSeconds=10";
            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal(JsonValueKind.Null, root.GetProperty("latencyP95Ms").ValueKind);

            var metadata = root.GetProperty("metadata");
            Assert.True(metadata.GetProperty("partial").GetBoolean());
            Assert.Contains("MaxOverviewSampleSize",
                metadata.GetProperty("notes").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(MaxSampleSizeEnv, null);
        }
    }

    [Theory]
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T09:00:00Z&bucketSeconds=60")] // toUtc < fromUtc
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-13T10:00:01Z&bucketSeconds=60")] // > 7 days
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T10:00:30Z&bucketSeconds=10")] // < 1 minute
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T11:00:00Z&bucketSeconds=5")]   // bucket < 10s
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T11:00:00Z&bucketSeconds=2000")]// bucket > window/2
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T11:00:00Z&topReasons=0")]      // topN below 1
    [InlineData("/api/overview?fromUtc=2026-05-06T10:00:00Z&toUtc=2026-05-06T11:00:00Z&topTools=51")]       // topN above 50
    public async Task OverviewEndpointReturnsBadRequestForInvalidArguments(string url)
    {
        var dbPath = TempDbPath();
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(url);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;
            var error = root.GetProperty("error").GetString();
            Assert.Contains(error, new[] { "window_invalid", "bucket_invalid", "topn_invalid" });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    [Fact]
    public async Task OverviewEndpointAppliesDefaultsWhenWindowAndBucketUnspecified()
    {
        var dbPath = TempDbPath();
        // Ensure the audit DB schema exists with a row outside the default 1h window.
        await SeedSingleRowAsync(dbPath, DateTimeOffset.UtcNow.AddDays(-30), AuditDecision.Allow);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/overview");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal(60, root.GetProperty("bucket").GetProperty("sizeSeconds").GetInt32());
            Assert.Equal(3600.0, root.GetProperty("window").GetProperty("durationSeconds").GetDouble(), 0);
            Assert.Equal("audit-db", root.GetProperty("metadata").GetProperty("source").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    private static async Task SeedFixedRowsAsync(string dbPath, DateTimeOffset windowFrom)
    {
        // 4 events at offsets 5s, 15s, 25s, 45s into the window — durations 10/20/30/40ms.
        // Bucket=10s places them in buckets 0, 1, 2, 4 (so empty buckets 3, 5, 6 in a 7-bucket window).
        // Wait — recompute for the test: this method seeds for the happy-path test below.
        // The happy-path test sets bucketSeconds=10 and windowFrom..windowFrom+70s.
        // We want totals 0,1,1,1,0,1,0 → events in bucket 1 (allow), 2 (block), 3 (wouldBlock), 5 (warn).
        // Bucket index k starts at windowFrom + k*10s.
        // Place events at offsets 15s (bucket 1), 25s (bucket 2), 35s (bucket 3), 55s (bucket 5).

        using var sink = new SqliteAuditSink(dbPath);
        await sink.WriteAsync(MakeEvent(
            timestamp: windowFrom.AddSeconds(15),
            decision: AuditDecision.Allow,
            serverId: "alpha",
            method: "tools/list",
            toolName: null,
            reason: "allowed",
            durationMs: 10,
            correlationId: "corr-1"), CancellationToken.None);
        await sink.WriteAsync(MakeEvent(
            timestamp: windowFrom.AddSeconds(25),
            decision: AuditDecision.Block,
            serverId: "beta",
            method: "tools/call",
            toolName: "fs.read",
            reason: "path_traversal",
            durationMs: 20,
            correlationId: "corr-2"), CancellationToken.None);
        await sink.WriteAsync(MakeEvent(
            timestamp: windowFrom.AddSeconds(35),
            decision: AuditDecision.WouldBlock,
            serverId: "beta",
            method: "tools/call",
            toolName: "net.fetch",
            reason: "private_network",
            durationMs: 30,
            correlationId: "corr-3"), CancellationToken.None);
        await sink.WriteAsync(MakeEvent(
            timestamp: windowFrom.AddSeconds(55),
            decision: AuditDecision.Warn,
            serverId: "gamma",
            method: "tools/list",
            toolName: null,
            reason: "schema_drift",
            durationMs: 40,
            correlationId: "corr-4"), CancellationToken.None);
    }

    private static async Task SeedSingleRowAsync(string dbPath, DateTimeOffset timestamp, AuditDecision decision)
    {
        using var sink = new SqliteAuditSink(dbPath);
        await sink.WriteAsync(MakeEvent(
            timestamp: timestamp,
            decision: decision,
            serverId: "alpha",
            method: "tools/list",
            toolName: null,
            reason: "allowed",
            durationMs: 10,
            correlationId: "corr-old"), CancellationToken.None);
    }

    private static AuditEvent MakeEvent(
        DateTimeOffset timestamp,
        AuditDecision decision,
        string serverId,
        string? method,
        string? toolName,
        string reason,
        long durationMs,
        string correlationId) =>
        new(
            Timestamp: timestamp,
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: decision,
            ServerId: serverId,
            Method: method,
            ToolName: toolName,
            Reasons: [reason],
            PolicyVersion: "policy-1",
            CorrelationId: correlationId,
            RequestBytes: 0,
            DurationMs: durationMs,
            ArgumentSummary: JsonNode.Parse("""{"token":"[redacted]"}"""),
            BatchSize: 0);

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-management-overview-{Guid.NewGuid():N}.db");

    private static string IsoZ(DateTimeOffset dt) =>
        Uri.EscapeDataString(dt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
}

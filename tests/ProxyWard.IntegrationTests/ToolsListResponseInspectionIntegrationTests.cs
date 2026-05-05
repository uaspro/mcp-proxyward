using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProxyWard.Api.Observability;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.IntegrationTests;

public class ToolsListResponseInspectionIntegrationTests
{
    [Fact]
    public async Task InspectableToolsListJsonResponseIsReturnedUnchangedAndAudited()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Find repositories","inputSchema":{"type":"object"}},{"name":"issues.list","description":"List issues","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal("tools/list", row.Method);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal(2, summary.GetProperty("toolCount").GetInt32());
        var toolNames = summary.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!).ToArray();
        Assert.Equal(["issues.list", "repos.search"], toolNames);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task StreamingToolsListResponseWarnsAndPassesThroughWhenUnsupportedStreamingWarn()
    {
        const string responseBody = "data: {\"jsonrpc\":\"2.0\"}\n\n";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("warn", row.Decision);
        Assert.Contains("inspection_unsupported", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task StreamingToolsListResponseBlocksWhenUnsupportedStreamingBlockInEnforceMode()
    {
        const string responseBody = "data: upstream-stream\n\n";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("inspection_unsupported", body, StringComparison.Ordinal);
            Assert.DoesNotContain(responseBody, body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("inspection_unsupported", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task OversizedToolsListResponseBlocksWithoutReturningUpstreamBody()
    {
        var responseBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[{\"name\":\""
            + new string('x', 2048)
            + "\"}]}}";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 128, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("inspection_unsupported", body, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 128), body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("inspection_unsupported", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeDescriptionDriftWarnsAndReturnsOriginalResponse()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"New description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool("repos.search", "Search", "Old description", JsonNode.Parse("""{"type":"object"}"""), null));
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("warn", row.Decision);
        Assert.Contains("tool_description_changed", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var driftedToolNames = payload.RootElement
            .GetProperty("argumentSummary")
            .GetProperty("driftedToolNames")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray();
        Assert.Equal(["repos.search"], driftedToolNames);

        Assert.Equal(2, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeSchemaDriftBlocksWithoutReturningUpstreamToolSurface()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool(
                "repos.search",
                "Search",
                "Description",
                JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
                null));
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("tool_schema_changed", body, StringComparison.Ordinal);
            Assert.DoesNotContain("limit", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("tool_schema_changed", row.Reasons, StringComparison.Ordinal);

        Assert.Equal(2, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task FirstObservationPersistsVersionOneWithNoDrift()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("enforce")]
    public async Task SchemaLockWriteFailureReturnsUpstreamPayloadAndEmitsMetric(string mode)
    {
        using var metrics = new MetricCollector();
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(mode, "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ITrackedToolSchemaStore>();
                        services.AddSingleton<ITrackedToolSchemaStore>(
                            new ThrowingSchemaStore(SchemaLockWriteFailureReasons.DbLocked));
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaWriteFailedMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaServerIdTag, out var serverId)
            && serverId == "github"
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaFailureReasonTag, out var reason)
            && reason == SchemaLockWriteFailureReasons.DbLocked);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task RenamedServerStartsNewVersionChainAndLeavesPriorChainUntouched()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();

        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("alpha", "/alpha/mcp", upstream.BaseAddress, dbPath),
            "/alpha/mcp");
        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("beta", "/beta/mcp", upstream.BaseAddress, dbPath),
            "/beta/mcp");

        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "alpha"));
        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "beta"));
        Assert.Equal(1, await LatestSchemaVersionAsync(dbPath, "alpha"));
        Assert.Equal(1, await LatestSchemaVersionAsync(dbPath, "beta"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task RemovedServerLeavesPriorSchemaRowsIntactOnNextStartup()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();

        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("alpha", "/alpha/mcp", upstream.BaseAddress, dbPath),
            "/alpha/mcp");

        var before = await CountSchemaVersionsAsync(dbPath, "alpha");
        var policyPath = WriteTempPolicy(CreateSingleServerPolicy("beta", "/beta/mcp", upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }

        Assert.Equal(1, before);
        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "alpha"));
        Assert.Equal(0, await CountSchemaVersionsAsync(dbPath, "beta"));

        DeleteIfExists(dbPath);
    }

    private const string ToolsListRequest = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static async Task WriteResponseAsync(
        HttpContext context,
        string body,
        string contentType,
        bool setLength = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        if (setLength)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await context.Response.Body.WriteAsync(bytes);
    }

    private static async Task<UpstreamApp> StartUpstreamAsync(Func<HttpContext, Task> handler)
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var counter = new RequestCounter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", async context =>
        {
            counter.Increment();
            await handler(context);
        });

        await app.StartAsync();
        return new UpstreamApp(baseAddress, app, counter);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string NewTempSqlitePath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db");

    private static async Task SeedSchemaVersionAsync(
        string dbPath,
        string serverId,
        string upstreamUrl,
        DiscoveredTool tool)
    {
        using var store = new SqliteTrackedToolSchemaStore(dbPath);
        var fingerprinter = new ToolFingerprinter();
        var snapshot = new ToolSchemaSnapshotInput(
            serverId,
            upstreamUrl,
            "2025-11-25",
            [new ToolSchemaSnapshotEntry(tool.Name!, fingerprinter.Fingerprint(tool))]);

        await store.RecordAsync(snapshot, new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);
    }

    private static async Task<long> CountSchemaVersionsAsync(string dbPath, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tool_schema_versions WHERE server_id = $server_id;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> LatestSchemaVersionAsync(string dbPath, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM tool_schema_versions WHERE server_id = $server_id ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task RunToolsListOnceAsync(string policyYaml, string route)
    {
        var policyPath = WriteTempPolicy(policyYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                route,
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; SQLite shared cache may delay file release on Windows.
        }
    }

    private static string CreatePolicy(
        string mode,
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress,
        string sqlitePath) =>
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: {{maxBodyBytes}}
          unsupportedStreaming: {{unsupportedStreaming}}
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{sqlitePath.Replace("\\", "/")}}
        observability:
          serviceName: mcp-proxyward
          console:
            enabled: true
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          github:
            route: /github/mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
        """;

    private static string CreateSingleServerPolicy(
        string serverId,
        string route,
        string upstreamBaseAddress,
        string sqlitePath) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 4096
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{sqlitePath.Replace("\\", "/")}}
        observability:
          serviceName: mcp-proxyward
          console:
            enabled: true
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          {{serverId}}:
            route: {{route}}
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
        """;

    private static List<AuditRow> ReadAuditEvents(string path) =>
        AuditDatabase.ReadEventually(() =>
        {
            var rows = new List<AuditRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_type, mode, decision, server_id, method, tool_name,
                       reasons, payload_json
                FROM audit_events
                ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AuditRow(
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
        });

    private sealed record AuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PayloadJson);

    private sealed class ThrowingSchemaStore(string reason) : ITrackedToolSchemaStore
    {
        public ValueTask<ToolSchemaVersionRow?> GetLatestAsync(
            string serverId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolSchemaVersionRow?>(null);

        public ValueTask<RecordedVersion> RecordAsync(
            ToolSchemaSnapshotInput snapshot,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            throw new SchemaLockWriteFailedException(
                reason,
                "Simulated schema-lock write failure.",
                new IOException("simulated"));
    }

    private sealed record MeasurementSnapshot(
        string Name,
        long Value,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed class MetricCollector : IDisposable
    {
        private readonly ConcurrentQueue<MeasurementSnapshot> _snapshots = new();
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ProxyWardTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                var copiedTags = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copiedTags[tag.Key] = tag.Value?.ToString();
                }

                _snapshots.Enqueue(new MeasurementSnapshot(instrument.Name, measurement, copiedTags));
            });
            _listener.Start();
        }

        public IReadOnlyCollection<MeasurementSnapshot> Snapshots => _snapshots.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RequestCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class UpstreamApp(string baseAddress, WebApplication app, RequestCounter counter) : IAsyncDisposable
    {
        public string BaseAddress { get; } = baseAddress;
        public int RequestCount => counter.Count;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.IntegrationTests;

public class ObservabilityIntegrationTests
{
    [Fact]
    public async Task ToolCallEmitsSafeOpenTelemetryActivitiesAndMetrics()
    {
        await using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"jsonrpc":"2.0","id":1,"result":{}}"""));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":700,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"rm -rf /tmp/build","path":"/etc/passwd","token":"api-token"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-700");

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }

        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.RequestInspectionActivity);
        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.AuditWriteActivity);
        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.YarpProxyActivity
            && activity.Tags.TryGetValue(ProxyWardTelemetry.McpMethodTag, out var method)
            && method == "tools/call");

        var toolPolicy = Assert.Single(
            activities.Snapshots,
            activity => activity.Name == ProxyWardTelemetry.PolicyEvaluationActivity
                && activity.Tags.TryGetValue(ProxyWardTelemetry.McpToolNameTag, out var toolName)
                && toolName == "shell.exec");
        Assert.Equal("allow", toolPolicy.Tags[ProxyWardTelemetry.PolicyDecisionTag]);
        Assert.Equal("github", toolPolicy.Tags[ProxyWardTelemetry.ServerIdTag]);
        Assert.Equal("corr-700", toolPolicy.Tags[ProxyWardTelemetry.CorrelationIdTag]);

        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.RequestsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.McpMethodTag, out var method)
            && method == "tools/call");
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.PolicyDecisionsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.PolicyDecisionTag, out var decision)
            && decision == "allow");

        var allTelemetryValues = string.Join(
            ' ',
            activities.Snapshots.SelectMany(activity => activity.Tags.Values)
                .Concat(metrics.Snapshots.SelectMany(measurement => measurement.Tags.Values)));
        Assert.DoesNotContain("rm -rf", allTelemetryValues, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/passwd", allTelemetryValues, StringComparison.Ordinal);
        Assert.DoesNotContain("api-token", allTelemetryValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedInspectionAndAuditFailureEmitMetrics()
    {
        await using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"ok":true}"""));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.AddSingleton<IAuditSink>(_ => new ThrowingAuditSink());
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent("plain text", Encoding.UTF8, "text/plain"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }

        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.AuditWriteActivity);
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.InspectionSkipsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.InspectionDirectionTag, out var direction)
            && direction == "request"
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.InspectionUnsupportedKindTag, out var kind)
            && kind == "unsupported_content_type");
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.AuditSinkFailuresMetric);
    }

    [Fact]
    public async Task ToolPolicyTelemetryUsesSameRedactedSummaryAndReasonsAsAudit()
    {
        await using var activities = new ActivityCollector();
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"jsonrpc":"2.0","id":1,"result":{}}"""));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            "enforce",
            "warn",
            4096,
            upstream.BaseAddress,
            dbPath,
            blockTool: "shell.exec"));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        const string secretToken = "api-token-very-secret";
        const string rawPath = "/etc/passwd";
        const string rawCommand = "rm -rf /tmp/build";
        const string rawHost = "10.0.0.5";
        const string rawUrl = "https://user:pass@internal.example.com/private/repos?token=abcDEF123";
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 710,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "shell.exec",
                ["arguments"] = new JsonObject
                {
                    ["command"] = rawCommand,
                    ["path"] = rawPath,
                    ["token"] = secretToken,
                    ["host"] = rawHost,
                    ["url"] = rawUrl
                }
            }
        }.ToJsonString();

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-710");

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var auditRow = Assert.Single(
                await ReadAuditEvents(dbPath),
                row => row.EventType == "tool_call_policy");
            var auditSummary = auditRow.ArgumentSummary.ToJsonString();
            var policyActivity = Assert.Single(
                activities.Snapshots,
                activity => activity.Name == ProxyWardTelemetry.PolicyEvaluationActivity
                    && activity.Tags.TryGetValue(ProxyWardTelemetry.PolicyDecisionTag, out var decision)
                    && decision == "block");

            Assert.Equal(auditRow.Reasons, policyActivity.Tags[ProxyWardTelemetry.PolicyReasonsTag]);
            Assert.Equal(auditSummary, policyActivity.Tags[ProxyWardTelemetry.McpArgumentSummaryTag]);

            var combinedTelemetry = string.Join(' ', policyActivity.Tags.Values);
            Assert.DoesNotContain(secretToken, combinedTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain(rawPath, combinedTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain(rawCommand, combinedTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain(rawHost, combinedTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("internal.example.com", combinedTelemetry, StringComparison.Ordinal);
            Assert.DoesNotContain("abcDEF123", combinedTelemetry, StringComparison.Ordinal);
            Assert.Contains("[redacted]", auditSummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-path]", auditSummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-command]", auditSummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-host]", auditSummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-query]", auditSummary, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ConfiguredSecretPatternsAreRedactedFromAuditAndTelemetrySummaries()
    {
        await using var activities = new ActivityCollector();
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"jsonrpc":"2.0","id":1,"result":{}}"""));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            "enforce",
            "warn",
            4096,
            upstream.BaseAddress,
            dbPath,
            secretsBlock: """
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
                - /github_pat_[A-Za-z0-9_]+/
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        const string literalSecret = "ghp_literal_secret";
        const string regexSecret = "github_pat_regex_secret_123";
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 711,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "repos.search",
                ["arguments"] = new JsonObject
                {
                    ["note"] = $"prefix {literalSecret}",
                    ["session"] = regexSecret,
                    ["limit"] = 5
                }
            }
        }.ToJsonString();

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-711");

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            var auditRow = Assert.Single(
                await ReadAuditEvents(dbPath),
                row => row.EventType == "tool_call_policy");
            var auditSummary = auditRow.ArgumentSummary.ToJsonString();
            var policyActivity = Assert.Single(
                activities.Snapshots,
                activity => activity.Name == ProxyWardTelemetry.PolicyEvaluationActivity
                    && activity.Tags.TryGetValue(ProxyWardTelemetry.McpToolNameTag, out var toolName)
                    && toolName == "repos.search");
            var telemetrySummary = policyActivity.Tags[ProxyWardTelemetry.McpArgumentSummaryTag];

            Assert.DoesNotContain(literalSecret, auditSummary, StringComparison.Ordinal);
            Assert.DoesNotContain(regexSecret, auditSummary, StringComparison.Ordinal);
            Assert.DoesNotContain(literalSecret, telemetrySummary, StringComparison.Ordinal);
            Assert.DoesNotContain(regexSecret, telemetrySummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-secret:literal]", auditSummary, StringComparison.Ordinal);
            Assert.Contains("[redacted-secret:regex]", auditSummary, StringComparison.Ordinal);
            Assert.Equal(auditSummary, telemetrySummary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task BlockedAndWouldBlockToolCallsEmitDedicatedDecisionMetrics()
    {
        using var metrics = new MetricCollector();

        await using (var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"jsonrpc":"2.0","id":1,"result":{}}""")))
        {
            var dbPath = NewTempSqlitePath();
            var policyPath = WriteTempPolicy(CreatePolicy(
                "enforce",
                "warn",
                4096,
                upstream.BaseAddress,
                dbPath,
                blockTool: "shell.exec"));
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

            try
            {
                await using var factory = new WebApplicationFactory<Program>();
                using var client = factory.CreateClient();

                using var response = await client.PostAsync(
                    "/github/mcp",
                    new StringContent("""{"jsonrpc":"2.0","id":701,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"echo safe"}}}""", Encoding.UTF8, "application/json"));

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(0, upstream.RequestCount);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
                DeleteIfExists(dbPath);
            }
        }

        await using (var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, """{"jsonrpc":"2.0","id":1,"result":{}}""")))
        {
            var dbPath = NewTempSqlitePath();
            var policyPath = WriteTempPolicy(CreatePolicy(
                "audit",
                "warn",
                4096,
                upstream.BaseAddress,
                dbPath,
                blockTool: "shell.exec"));
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

            try
            {
                await using var factory = new WebApplicationFactory<Program>();
                using var client = factory.CreateClient();

                using var response = await client.PostAsync(
                    "/github/mcp",
                    new StringContent("""{"jsonrpc":"2.0","id":702,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"echo safe"}}}""", Encoding.UTF8, "application/json"));

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(1, upstream.RequestCount);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
                DeleteIfExists(dbPath);
            }
        }

        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.BlockedCallsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.PolicyDecisionTag, out var decision)
            && decision == "block");
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.WouldBlockCallsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.PolicyDecisionTag, out var decision)
            && decision == "would_block");
    }

    [Fact]
    public async Task ToolsListDriftEmitsSchemaLockActivityAndSchemaDriftMetric()
    {
        await using var activities = new ActivityCollector();
        using var metrics = new MetricCollector();
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody));
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
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }

        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.SchemaLockCheckActivity
            && activity.Tags.TryGetValue(ProxyWardTelemetry.SchemaVersionTag, out var version)
            && version == "2");
        Assert.Contains(activities.Snapshots, activity => activity.Name == ProxyWardTelemetry.SchemaLockCheckActivity
            && activity.Tags.TryGetValue(ProxyWardTelemetry.PolicyReasonsTag, out var reasons)
            && reasons == "tool_schema_changed");
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaDriftEventsMetric);
        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaDriftEventsMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaVersionTag, out var version)
            && version == "2");
        Assert.DoesNotContain(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaDriftEventsMetric
            && measurement.Tags.ContainsKey(ProxyWardTelemetry.PolicyReasonsTag));
        Assert.DoesNotContain(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaDriftEventsMetric
            && measurement.Tags.ContainsKey(ProxyWardTelemetry.PolicyVersionTag));
    }

    [Fact]
    public async Task ToolsListNoOpWithChangedUpstreamEmitsOperationalMetric()
    {
        using var metrics = new MetricCollector();
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            "http://old-upstream/mcp",
            new DiscoveredTool(
                "repos.search",
                "Search",
                "Description",
                JsonNode.Parse("""{"type":"object"}"""),
                null));
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(dbPath);
        }

        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaUpstreamChangedMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaServerIdTag, out var serverId)
            && serverId == "github"
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaPreviousUrlTag, out var previousUrl)
            && previousUrl == "http://old-upstream/mcp"
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaCurrentUrlTag, out var currentUrl)
            && currentUrl == $"{upstream.BaseAddress}/mcp");
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        string body,
        string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
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
        app.Map("/{**path}", async (HttpContext context) =>
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

    private static readonly System.Threading.AsyncLocal<string?> NextPolicyDatabasePath = new();

    private static string WriteTempPolicy(string yaml)
    {
        var path = NextPolicyDatabasePath.Value
            ?? Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.db");
        NextPolicyDatabasePath.Value = null;
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string NewTempSqlitePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db");
        NextPolicyDatabasePath.Value = path;
        return path;
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

    private static Task<List<AuditRow>> ReadAuditEvents(string path) =>
        AuditDatabase.ReadEventuallyAsync(() =>
        {
            var rows = new List<AuditRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_type, reasons, payload_json FROM audit_events ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var payload = JsonNode.Parse(reader.GetString(2))!.AsObject();
                var argumentSummary = payload["argumentSummary"]?.DeepClone() ?? new JsonObject();
                rows.Add(new AuditRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    argumentSummary));
            }

            return rows;
        });

    private static string CreatePolicy(
        string mode,
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress,
        string sqlitePath,
        string? blockTool = null,
        string? secretsBlock = null)
    {
        var blockBlock = string.IsNullOrWhiteSpace(blockTool)
            ? "block: []"
            : "block:\n              - " + blockTool;

        var yaml =
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: {{maxBodyBytes}}
          unsupportedStreaming: {{unsupportedStreaming}}
          batchToolCalls: failClosed
        audit:
          enabled: true
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
              default: allow
              allow: []
              {{blockBlock}}
            arguments:
              paths:
                allowedRoots: []
                blockTraversal: false
              hosts:
                allow: []
                blockPrivateNetworks: false
              commands:
                blockShell: false
                dangerous: []
        """;

        return string.IsNullOrWhiteSpace(secretsBlock)
            ? yaml
            : InsertBeforeFirstTools(yaml, secretsBlock);
    }

    private static string InsertBeforeFirstTools(string yaml, string secretsBlock)
    {
        var markerIndex = yaml.IndexOf("tools:", StringComparison.Ordinal);
        var lineStart = yaml.LastIndexOf('\n', markerIndex) + 1;
        var indent = yaml[lineStart..markerIndex];
        return yaml.Insert(lineStart, Indent(secretsBlock, indent.Length) + "\n");
    }

    private static string Indent(string block, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(
            "\n",
            block.ReplaceLineEndings("\n")
                .Split('\n')
                .Where(line => line.Length > 0)
                .Select(line => prefix + line));
    }

    private sealed record ActivitySnapshot(
        string Name,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed class ActivityCollector : IAsyncDisposable
    {
        private readonly ConcurrentQueue<ActivitySnapshot> _snapshots = new();
        private readonly ActivityListener _listener;

        public ActivityCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ProxyWardTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _snapshots.Enqueue(new ActivitySnapshot(
                    activity.OperationName,
                    activity.TagObjects.ToDictionary(
                        tag => tag.Key,
                        tag => tag.Value?.ToString(),
                        StringComparer.Ordinal)))
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyCollection<ActivitySnapshot> Snapshots => _snapshots.ToArray();

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
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

    private sealed record AuditRow(
        string EventType,
        string Reasons,
        JsonNode ArgumentSummary);

    private sealed class ThrowingAuditSink : IAuditSink
    {
        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("audit sink unavailable");
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

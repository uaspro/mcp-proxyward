using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ProxyWard.IntegrationTests;

public class ToolResponseSecretInspectionIntegrationTests
{
    [Fact]
    public async Task EnforceModeBlocksToolResponseContainingConfiguredLiteralSecret()
    {
        const string secret = "ghp_literal_secret";
        var responseBody = CreateToolResponse(7, secret);
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath, blockReturn: true, patterns: ["ghp_"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(CreateToolCallRequest(7), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);

            using var payload = JsonDocument.Parse(body);
            Assert.Equal("2.0", payload.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(7, payload.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(-32001, payload.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Contains(
                payload.RootElement.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "secret_return_blocked");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_response_secret_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Equal("tools/call", row.Method);
        Assert.Equal("repos.search", row.ToolName);
        Assert.Contains("secret_return_blocked", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeBlocksToolResponseContainingConfiguredRegexSecret()
    {
        const string secret = "github_pat_regex_secret_123";
        var responseBody = CreateToolResponse(8, secret);
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            "enforce",
            "warn",
            4096,
            upstream.BaseAddress,
            dbPath,
            blockReturn: true,
            patterns: ["/github_pat_[A-Za-z0-9_]+/"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(CreateToolCallRequest(8), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            Assert.Contains("secret_return_blocked", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_response_secret_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("regex", row.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeWouldBlockToolResponseSecretButReturnsUpstreamBody()
    {
        const string secret = "ghp_audit_secret";
        var responseBody = CreateToolResponse(9, secret);
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath, blockReturn: true, patterns: ["ghp_"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(CreateToolCallRequest(9), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_response_secret_inspection");
        Assert.Equal("would_block", row.Decision);
        Assert.Contains("secret_return_blocked", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task BlockReturnFalseAllowsConfiguredPatternMatchWithoutResponseSecretAudit()
    {
        const string secret = "ghp_allowed_secret";
        var responseBody = CreateToolResponse(10, secret);
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath, blockReturn: false, patterns: ["ghp_"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(CreateToolCallRequest(10), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        Assert.DoesNotContain(await ReadAuditEvents(dbPath), row => row.EventType == "tool_response_secret_inspection");

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task StreamingToolCallResponseWithinMaxBodyBytesIsInspectedForSecrets()
    {
        const string secret = "ghp_stream_secret";
        var streamBody = "event: message\n"
            + $"data: {CreateToolResponse(11, secret)}\n\n";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, streamBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 4096, upstream.BaseAddress, dbPath, blockReturn: true, patterns: ["ghp_"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(CreateToolCallRequest(11), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("secret_return_blocked", body, StringComparison.Ordinal);
            Assert.NotEqual(streamBody, body);
            Assert.DoesNotContain(streamBody, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_response_secret_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("secret_return_blocked", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("inspection_unsupported", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    private static string CreateToolCallRequest(int id) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "repos.search",
                ["arguments"] = new JsonObject
                {
                    ["q"] = "proxyward"
                }
            }
        }.ToJsonString();

    private static string CreateToolResponse(int id, string text) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                }
            }
        }.ToJsonString();

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
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string NewTempSqlitePath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db");

    private static string CreatePolicy(
        string mode,
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress,
        string sqlitePath,
        bool blockReturn,
        IReadOnlyCollection<string> patterns)
    {
        var patternsBlock = patterns.Count == 0
            ? "patterns: []"
            : "patterns:\n              - " + string.Join("\n              - ", patterns);

        return $$"""
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
            secrets:
              redactInLogs: true
              blockReturn: {{blockReturn.ToString().ToLowerInvariant()}}
              {{patternsBlock}}
            tools:
              default: allow
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

    private sealed record AuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PayloadJson);

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

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ProxyWard.IntegrationTests;

public class ToolPolicyIntegrationTests
{
    [Fact]
    public async Task EnforceModeBlockedToolReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "deny",
            allow: ["repos.search"],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"ls"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);

            Assert.Equal("2.0", payload.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("id").GetInt32());

            var error = payload.RootElement.GetProperty("error");
            Assert.Equal(-32001, error.GetProperty("code").GetInt32());
            Assert.Equal("MCP ProxyWard blocked this tool call", error.GetProperty("message").GetString());

            var reasons = error.GetProperty("data").GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "tool_blocked");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        Assert.Single(rows);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("enforce", row.Mode);
        Assert.Equal("block", row.Decision);
        Assert.Equal("github", row.ServerId);
        Assert.Equal("tools/call", row.Method);
        Assert.Equal("shell.exec", row.ToolName);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeBlockedToolReachesUpstreamAndRecordsWouldBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "allow",
            allow: [],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"ls"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("audit", row.Mode);
        Assert.Equal("would_block", row.Decision);
        Assert.Equal("shell.exec", row.ToolName);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeDenyDefaultUnlistedToolBlocksWithToolNotAllowed()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "deny",
            allow: ["repos.search"],
            block: []));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"issues.list","arguments":{}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);

            Assert.Equal("2.0", payload.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal(3, payload.RootElement.GetProperty("id").GetInt32());

            var reasons = payload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "tool_not_allowed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("enforce", row.Mode);
        Assert.Equal("block", row.Decision);
        Assert.Equal("issues.list", row.ToolName);
        Assert.Contains("tool_not_allowed", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeHiddenToolReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "allow",
            allow: [],
            block: [],
            hide: ["repos.search"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":32,"method":"tools/call","params":{"name":"repos.search","arguments":{"q":"cached"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(32, payload.RootElement.GetProperty("id").GetInt32());
            Assert.Contains(
                payload.RootElement.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "tool_blocked");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Equal("repos.search", row.ToolName);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeHideDefaultUnlistedToolReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "hide",
            allow: ["repos.search"],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":33,"method":"tools/call","params":{"name":"issues.list","arguments":{"state":"open"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(33, payload.RootElement.GetProperty("id").GetInt32());
            Assert.Contains(
                payload.RootElement.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "tool_not_allowed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(await ReadAuditEvents(dbPath), r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Equal("issues.list", row.ToolName);
        Assert.Contains("tool_not_allowed", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeBlockedToolPreservesStringJsonRpcId()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "allow",
            allow: [],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":"request-abc","method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"ls"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);

            Assert.Equal("request-abc", payload.RootElement.GetProperty("id").GetString());
            Assert.Contains(
                payload.RootElement.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "tool_blocked");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Equal("shell.exec", row.ToolName);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeBlockedToolMissingJsonRpcIdReturnsHttpErrorAndAudits()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "allow",
            allow: [],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"ls"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("tool_blocked", responseBody, StringComparison.Ordinal);
            Assert.Contains("Invalid JSON-RPC request", responseBody, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("enforce", row.Mode);
        Assert.Equal("block", row.Decision);
        Assert.Equal("tools/call", row.Method);
        Assert.Equal("shell.exec", row.ToolName);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeAllowedBatchToolCallsProxyUnchangedAndAuditEachToolCall()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "deny",
            allow: ["issues.list", "repos.search"],
            block: []));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """
            [{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"repos.search","arguments":{"q":"proxyward"}}},{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"issues.list","arguments":{"state":"open"}}}]
            """;

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var toolRows = (await ReadAuditEvents(dbPath))
            .Where(r => r.EventType == "tool_call_policy")
            .OrderBy(ReadBatchIndex)
            .ToArray();

        Assert.Collection(
            toolRows,
            row =>
            {
                Assert.Equal("allow", row.Decision);
                Assert.Equal("repos.search", row.ToolName);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(0, ReadBatchIndex(row));
            },
            row =>
            {
                Assert.Equal("allow", row.Decision);
                Assert.Equal("issues.list", row.ToolName);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(1, ReadBatchIndex(row));
            });

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeMixedBatchFailClosedBlocksUpstreamAndAuditsEachToolCall()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "deny",
            allow: ["repos.search"],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """
            [{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"repos.search","arguments":{"q":"proxyward"}}},{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"rm -rf /tmp/demo"}}}]
            """;

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(JsonValueKind.Array, payload.RootElement.ValueKind);
            Assert.Equal(2, payload.RootElement.GetArrayLength());

            var allowedResponse = FindBatchResponseByIntId(payload.RootElement, 20);
            Assert.Equal(-32001, allowedResponse.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(0, allowedResponse.GetProperty("error").GetProperty("data").GetProperty("batchIndex").GetInt32());
            Assert.Contains(
                allowedResponse.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "batch_blocked");

            var blockedResponse = FindBatchResponseByIntId(payload.RootElement, 21);
            Assert.Equal(1, blockedResponse.GetProperty("error").GetProperty("data").GetProperty("batchIndex").GetInt32());
            Assert.Contains(
                blockedResponse.GetProperty("error").GetProperty("data").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetString() == "tool_blocked");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var toolRows = (await ReadAuditEvents(dbPath))
            .Where(r => r.EventType == "tool_call_policy")
            .OrderBy(ReadBatchIndex)
            .ToArray();

        Assert.Collection(
            toolRows,
            row =>
            {
                Assert.Equal("allow", row.Decision);
                Assert.Equal("repos.search", row.ToolName);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(0, ReadBatchIndex(row));
            },
            row =>
            {
                Assert.Equal("block", row.Decision);
                Assert.Equal("shell.exec", row.ToolName);
                Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(1, ReadBatchIndex(row));
            });

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeMixedBatchProxiesUnchangedAndAuditsWouldBlockPerMessage()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "allow",
            allow: [],
            block: ["shell.exec"]));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """
            [{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"shell.exec","arguments":{"cmd":"ls"}}},{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"repos.search","arguments":{"q":"proxyward"}}}]
            """;

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var toolRows = (await ReadAuditEvents(dbPath))
            .Where(r => r.EventType == "tool_call_policy")
            .OrderBy(ReadBatchIndex)
            .ToArray();

        Assert.Collection(
            toolRows,
            row =>
            {
                Assert.Equal("would_block", row.Decision);
                Assert.Equal("shell.exec", row.ToolName);
                Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(0, ReadBatchIndex(row));
            },
            row =>
            {
                Assert.Equal("allow", row.Decision);
                Assert.Equal("repos.search", row.ToolName);
                Assert.Equal(2, ReadPayloadInt(row, "batchSize"));
                Assert.Equal(1, ReadBatchIndex(row));
            });

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeAllowedToolReachesUpstreamWithoutToolPolicyBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            toolDefault: "deny",
            allow: ["repos.search"],
            block: []));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"repos.search","arguments":{"q":"foo"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("allow", row.Decision);
        Assert.Equal("repos.search", row.ToolName);
        Assert.Equal(string.Empty, row.Reasons);

        DeleteIfExists(dbPath);
    }

    private static async Task<UpstreamApp> StartUpstreamAsync()
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var counter = new RequestCounter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", async (HttpRequest request) =>
        {
            counter.Increment();
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var read = await reader.ReadToEndAsync();
            return Results.Json(new
            {
                method = request.Method,
                body = read
            });
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

    private static string CreatePolicy(
        string mode,
        string upstreamBaseAddress,
        string sqlitePath,
        string toolDefault,
        IReadOnlyCollection<string> allow,
        IReadOnlyCollection<string> block,
        IReadOnlyCollection<string>? hide = null)
    {
        var allowBlock = allow.Count == 0 ? "allow: []" : "allow:\n              - " + string.Join("\n              - ", allow);
        var blockBlock = block.Count == 0 ? "block: []" : "block:\n              - " + string.Join("\n              - ", block);
        var hiddenTools = hide ?? [];
        var hideBlock = hiddenTools.Count == 0 ? "hide: []" : "hide:\n              - " + string.Join("\n              - ", hiddenTools);

        return $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: 4096
          unsupportedStreaming: warn
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
              default: {{toolDefault}}
              {{allowBlock}}
              {{blockBlock}}
              {{hideBlock}}
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

    private static JsonElement FindBatchResponseByIntId(JsonElement batchResponse, int id) =>
        batchResponse.EnumerateArray().Single(response => response.GetProperty("id").GetInt32() == id);

    private static int ReadBatchIndex(AuditRow row) =>
        ReadPayloadInt(row, "batchIndex");

    private static int ReadPayloadInt(AuditRow row, string propertyName)
    {
        using var payload = JsonDocument.Parse(row.PayloadJson);
        return payload.RootElement.GetProperty(propertyName).GetInt32();
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

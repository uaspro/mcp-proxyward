using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Policy.Engine;

namespace ProxyWard.IntegrationTests;

public class CommandArgumentRuleIntegrationTests
{
    [Fact]
    public async Task EnforceModeDangerousExecutableReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            blockShell: false,
            dangerous: ["rm"],
            allowedRoots: [],
            blockPrivateNetworks: false,
            blockTool: null));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":500,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"rm -rf /tmp/build"}}}""";

        try
        {
            await using var factory = CreateFactory(new StubHostResolver());
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            Assert.Equal(500, payload.RootElement.GetProperty("id").GetInt32());

            var reasons = payload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(r => r.GetString()!)
                .ToArray();

            Assert.Equal(["dangerous_command"], reasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("enforce", row.Mode);
        Assert.Equal("block", row.Decision);
        Assert.Equal("shell.exec", row.ToolName);
        Assert.Contains("dangerous_command", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf /tmp/build", row.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("[redacted-command]", row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeDangerousExecutableProxiesUnchangedAndRecordsWouldBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            upstream.BaseAddress,
            dbPath,
            blockShell: false,
            dangerous: ["rm"],
            allowedRoots: [],
            blockPrivateNetworks: false,
            blockTool: null));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":501,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"rm -rf /tmp/build"}}}""";

        try
        {
            await using var factory = CreateFactory(new StubHostResolver());
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

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("audit", row.Mode);
        Assert.Equal("would_block", row.Decision);
        Assert.Contains("dangerous_command", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf /tmp/build", row.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("[redacted-command]", row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ShellMetacharacterPatternReturnsJsonRpcErrorWhenShellBlockingEnabled()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            blockShell: true,
            dangerous: [],
            allowedRoots: [],
            blockPrivateNetworks: false,
            blockTool: null));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":502,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"echo ready && echo done"}}}""";

        try
        {
            await using var factory = CreateFactory(new StubHostResolver());
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            var reasons = payload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "dangerous_command");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("dangerous_command", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task PathHostAndCommandViolationsMergeIntoSingleDecisionInStableOrder()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            blockShell: false,
            dangerous: ["rm"],
            allowedRoots: ["/workspace"],
            blockPrivateNetworks: true,
            blockTool: null));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var body = """{"jsonrpc":"2.0","id":503,"method":"tools/call","params":{"name":"fs.fetch","arguments":{"path":"/etc/passwd","url":"https://internal.svc/api","command":"rm -rf tmp"}}}""";

        try
        {
            await using var factory = CreateFactory(resolver);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            var reasons = payload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(r => r.GetString()!)
                .ToArray();

            Assert.Equal(["path_outside_allowed_roots", "private_network_target", "dangerous_command"], reasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("path_outside_allowed_roots", row.Reasons, StringComparison.Ordinal);
        Assert.Contains("private_network_target", row.Reasons, StringComparison.Ordinal);
        Assert.Contains("dangerous_command", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ToolBlockTakesPrecedenceOverCommandRules()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            blockShell: true,
            dangerous: ["rm"],
            allowedRoots: [],
            blockPrivateNetworks: false,
            blockTool: "shell.exec"));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":504,"method":"tools/call","params":{"name":"shell.exec","arguments":{"command":"rm -rf /tmp/build"}}}""";

        try
        {
            await using var factory = CreateFactory(new StubHostResolver());
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(responseBody);
            var reasons = payload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(r => r.GetString())
                .ToArray();

            Assert.Contains("tool_blocked", reasons);
            Assert.DoesNotContain("dangerous_command", reasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("dangerous_command", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    private static WebApplicationFactory<Program> CreateFactory(IHostResolver resolver) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(resolver);
            });
        });

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
            ?? TestFiles.NewSqlitePath();
        NextPolicyDatabasePath.Value = null;
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string NewTempSqlitePath()
    {
        var path = TestFiles.NewSqlitePath("proxyward-audit");
        NextPolicyDatabasePath.Value = path;
        return path;
    }

    private static void DeleteIfExists(string path) =>
        TestFiles.DeleteSqlite(path);

    private static string CreatePolicy(
        string mode,
        string upstreamBaseAddress,
        string sqlitePath,
        bool blockShell,
        IReadOnlyCollection<string> dangerous,
        IReadOnlyCollection<string> allowedRoots,
        bool blockPrivateNetworks,
        string? blockTool)
    {
        var rootsBlock = allowedRoots.Count == 0
            ? "allowedRoots: []"
            : "allowedRoots:\n                  - " + string.Join("\n                  - ", allowedRoots);
        var dangerousBlock = dangerous.Count == 0
            ? "dangerous: []"
            : "dangerous:\n                  - " + string.Join("\n                  - ", dangerous);
        var toolBlock = string.IsNullOrWhiteSpace(blockTool)
            ? "block: []"
            : $"block:\n              - {blockTool}";

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
              default: allow
              allow: []
              {{toolBlock}}
            arguments:
              paths:
                {{rootsBlock}}
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: {{(blockPrivateNetworks ? "true" : "false")}}
              commands:
                blockShell: {{(blockShell ? "true" : "false")}}
                {{dangerousBlock}}
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

    private sealed class StubHostResolver : IHostResolver
    {
        private readonly Dictionary<string, IPAddress[]> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public StubHostResolver WithHost(string host, params IPAddress[] addresses)
        {
            _hosts[host] = addresses;
            return this;
        }

        public ValueTask<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (_hosts.TryGetValue(host, out var addresses))
            {
                return new ValueTask<HostResolution>(new HostResolution(addresses, ResolutionFailed: false));
            }

            return new ValueTask<HostResolution>(new HostResolution([], ResolutionFailed: true));
        }
    }
}

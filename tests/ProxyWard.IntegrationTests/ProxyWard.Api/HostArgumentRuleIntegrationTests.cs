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

public class HostArgumentRuleIntegrationTests
{
    [Fact]
    public async Task EnforceModePrivateNetworkUrlReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: [],
            blockPrivateNetworks: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var body = """{"jsonrpc":"2.0","id":400,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://internal.svc/api"}}}""";

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
                .GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "private_network_target");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("enforce", row.Mode);
        Assert.Equal("block", row.Decision);
        Assert.Contains("private_network_target", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeHostNotInAllowlistReturnsJsonRpcErrorAndDoesNotReachUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: ["api.github.com"],
            blockPrivateNetworks: false));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver();
        var body = """{"jsonrpc":"2.0","id":401,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://evil.example.com/x"}}}""";

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
                .GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "host_not_allowed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("host_not_allowed", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModePrivateNetworkUrlProxiesUnchangedAndRecordsWouldBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            upstream.BaseAddress,
            dbPath,
            allow: [],
            blockPrivateNetworks: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var body = """{"jsonrpc":"2.0","id":402,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://internal.svc/api"}}}""";

        try
        {
            await using var factory = CreateFactory(resolver);
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
        Assert.Contains("private_network_target", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeAllowedPublicHostProxiesAndAuditsAllow()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: ["api.github.com"],
            blockPrivateNetworks: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("api.github.com", IPAddress.Parse("140.82.121.6"));
        var body = """{"jsonrpc":"2.0","id":403,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://api.github.com/repos/foo/bar"}}}""";

        try
        {
            await using var factory = CreateFactory(resolver);
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

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task PathAndHostViolationsMergeIntoSingleAuditRowWithBothReasons()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicyWithPaths(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: [],
            blockPrivateNetworks: true,
            allowedRoots: ["/workspace"],
            blockTraversal: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var body = """{"jsonrpc":"2.0","id":404,"method":"tools/call","params":{"name":"fs.fetch","arguments":{"path":"/etc/passwd","url":"https://internal.svc/api"}}}""";

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

            Assert.Equal(["path_outside_allowed_roots", "private_network_target"], reasons);
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

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ToolBlockTakesPrecedenceOverHostRulesAndSkipsResolver()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicyWithBlock(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            blockTool: "http.get",
            allow: [],
            blockPrivateNetworks: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var body = """{"jsonrpc":"2.0","id":405,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://internal.svc/api"}}}""";

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
                .Select(r => r.GetString())
                .ToArray();

            Assert.Contains("tool_blocked", reasons);
            Assert.DoesNotContain("private_network_target", reasons);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        Assert.Equal(0, resolver.CallCount);

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("tool_blocked", row.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("private_network_target", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AllowlistMatchIsCaseInsensitiveAtIntegrationLayer()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: ["API.GitHub.com"],
            blockPrivateNetworks: false));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver();
        var body = """{"jsonrpc":"2.0","id":407,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://api.github.com/repos/foo/bar"}}}""";

        try
        {
            await using var factory = CreateFactory(resolver);
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

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task DnsResolutionFailureWithBlockingEnabledFailsClosed()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            upstream.BaseAddress,
            dbPath,
            allow: [],
            blockPrivateNetworks: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var resolver = new StubHostResolver().WithFailingHost("unresolvable.example");
        var body = """{"jsonrpc":"2.0","id":406,"method":"tools/call","params":{"name":"http.get","arguments":{"url":"https://unresolvable.example/x"}}}""";

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
                .GetProperty("reasons");
            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "private_network_target");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var row = Assert.Single(rows, r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Contains("private_network_target", row.Reasons, StringComparison.Ordinal);

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
        IReadOnlyCollection<string> allow,
        bool blockPrivateNetworks) =>
        BuildPolicy(
            mode,
            upstreamBaseAddress,
            sqlitePath,
            toolDefault: "allow",
            toolBlock: [],
            allow,
            blockPrivateNetworks,
            allowedRoots: [],
            blockTraversal: false);

    private static string CreatePolicyWithPaths(
        string mode,
        string upstreamBaseAddress,
        string sqlitePath,
        IReadOnlyCollection<string> allow,
        bool blockPrivateNetworks,
        IReadOnlyCollection<string> allowedRoots,
        bool blockTraversal) =>
        BuildPolicy(
            mode,
            upstreamBaseAddress,
            sqlitePath,
            toolDefault: "allow",
            toolBlock: [],
            allow,
            blockPrivateNetworks,
            allowedRoots,
            blockTraversal);

    private static string CreatePolicyWithBlock(
        string mode,
        string upstreamBaseAddress,
        string sqlitePath,
        string blockTool,
        IReadOnlyCollection<string> allow,
        bool blockPrivateNetworks) =>
        BuildPolicy(
            mode,
            upstreamBaseAddress,
            sqlitePath,
            toolDefault: "allow",
            toolBlock: [blockTool],
            allow,
            blockPrivateNetworks,
            allowedRoots: [],
            blockTraversal: false);

    private static string BuildPolicy(
        string mode,
        string upstreamBaseAddress,
        string sqlitePath,
        string toolDefault,
        IReadOnlyCollection<string> toolBlock,
        IReadOnlyCollection<string> allow,
        bool blockPrivateNetworks,
        IReadOnlyCollection<string> allowedRoots,
        bool blockTraversal)
    {
        var allowBlock = allow.Count == 0 ? "allow: []" : "allow:\n              - " + string.Join("\n              - ", allow);
        var blockBlock = toolBlock.Count == 0 ? "block: []" : "block:\n              - " + string.Join("\n              - ", toolBlock);
        var rootsBlock = allowedRoots.Count == 0
            ? "allowedRoots: []"
            : "allowedRoots:\n                  - " + string.Join("\n                  - ", allowedRoots);

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
              allow: []
              {{blockBlock}}
            arguments:
              paths:
                {{rootsBlock}}
                blockTraversal: {{(blockTraversal ? "true" : "false")}}
              hosts:
                {{allowBlock}}
                blockPrivateNetworks: {{(blockPrivateNetworks ? "true" : "false")}}
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
                       reasons
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
                    reader.GetString(6)));
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
        string Reasons);

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
        private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public StubHostResolver WithHost(string host, params IPAddress[] addresses)
        {
            _hosts[host] = addresses;
            return this;
        }

        public StubHostResolver WithFailingHost(string host)
        {
            _failures.Add(host);
            return this;
        }

        public ValueTask<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_failures.Contains(host))
            {
                return new ValueTask<HostResolution>(new HostResolution([], ResolutionFailed: true));
            }
            if (_hosts.TryGetValue(host, out var addresses))
            {
                return new ValueTask<HostResolution>(new HostResolution(addresses, ResolutionFailed: false));
            }
            return new ValueTask<HostResolution>(new HostResolution([], ResolutionFailed: true));
        }
    }
}

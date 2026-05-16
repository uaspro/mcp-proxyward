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

public class AuditEventIntegrationTests
{
    [Fact]
    public async Task InspectableNonToolRequestRecordsAllowAuditEvent()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            unsupportedStreaming: "warn",
            maxBodyBytes: 1024,
            upstream.BaseAddress,
            sqlitePath: dbPath,
            serverAllowed: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        var body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""";

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
        var row = Assert.Single(rows, r => r.EventType == "request_inspection");
        Assert.Equal("audit", row.Mode);
        Assert.Equal("allow", row.Decision);
        Assert.Equal("github", row.ServerId);
        Assert.Equal("initialize", row.Method);
        Assert.Null(row.ToolName);
        Assert.False(string.IsNullOrEmpty(row.PolicyVersion));
        Assert.False(string.IsNullOrEmpty(row.CorrelationId));
        Assert.True(row.RequestBytes > 0);
        Assert.True(row.DurationMs >= 0);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ToolsCallWithSensitiveArgumentsRecordsRedactedPolicyAuditEvent()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "audit",
            unsupportedStreaming: "warn",
            maxBodyBytes: 4096,
            upstream.BaseAddress,
            sqlitePath: dbPath,
            serverAllowed: true));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        const string secretToken = "very-secret-token-abc123";
        var body = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"fs.read\",\"arguments\":{\"path\":\"/etc/passwd\",\"token\":\""
            + secretToken
            + "\"}}}";

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
        Assert.Equal("tools/call", row.Method);
        Assert.Equal("fs.read", row.ToolName);
        Assert.Equal("would_block", row.Decision);
        Assert.DoesNotContain(secretToken, row.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/passwd", row.PayloadJson, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        var arguments = summary.GetProperty("arguments");
        Assert.Equal("[redacted-path]", arguments.GetProperty("path").GetString());
        Assert.Equal("[redacted]", arguments.GetProperty("token").GetString());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ServerAllowlistBlockInEnforceModeRecordsBlockAuditEvent()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            mode: "enforce",
            unsupportedStreaming: "warn",
            maxBodyBytes: 1024,
            upstream.BaseAddress,
            sqlitePath: dbPath,
            serverAllowed: false));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcp");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = await ReadAuditEvents(dbPath);
        var blockRow = Assert.Single(rows, r => r.EventType == "server_allowlist_policy");
        Assert.Equal("enforce", blockRow.Mode);
        Assert.Equal("block", blockRow.Decision);
        Assert.Equal("github", blockRow.ServerId);
        Assert.Equal($"{HttpMethod.Get.Method} /github/mcp", blockRow.Method);
        Assert.Null(blockRow.ToolName);
        Assert.Contains("server_not_allowed", blockRow.Reasons, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(blockRow.PolicyVersion));
        Assert.False(string.IsNullOrEmpty(blockRow.CorrelationId));

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
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress,
        string sqlitePath,
        bool serverAllowed) =>
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: {{maxBodyBytes}}
          unsupportedStreaming: {{unsupportedStreaming}}
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
            allowed: {{(serverAllowed ? "true" : "false")}}
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
                       reasons, policy_version, correlation_id, request_bytes, duration_ms, payload_json
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
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetString(11)));
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
        string PolicyVersion,
        string CorrelationId,
        long RequestBytes,
        long DurationMs,
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

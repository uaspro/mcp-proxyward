using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.IntegrationTests;

public class ToolArgumentOverrideIntegrationTests
{
    [Fact]
    public async Task ToolOverrideCanRelaxPathRulesAndAuditsOverrideApplied()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            dbPath,
            serverAllowedRoots: ["/workspace"],
            serverBlockTraversal: true,
            overridesBlock:
            """
            overrides:
              fs.safe-read:
                paths:
                  allowedRoots: []
                  blockTraversal: false
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":600,"method":"tools/call","params":{"name":"fs.safe-read","arguments":{"path":"/etc/passwd"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tool_call_policy");
        Assert.Equal("allow", row.Decision);
        Assert.Equal("fs.safe-read", row.ToolName);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.True(payload.RootElement.GetProperty("argumentOverrideApplied").GetBoolean());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ToolWithoutOverrideStillUsesServerLevelPathRules()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            dbPath,
            serverAllowedRoots: ["/workspace"],
            serverBlockTraversal: true,
            overridesBlock:
            """
            overrides:
              fs.safe-read:
                paths:
                  allowedRoots: []
                  blockTraversal: false
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":601,"method":"tools/call","params":{"name":"fs.other-read","arguments":{"path":"/etc/passwd"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var responsePayload = JsonDocument.Parse(responseBody);
            var reasons = responsePayload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons");

            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "path_outside_allowed_roots");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Equal("fs.other-read", row.ToolName);
        Assert.Contains("path_outside_allowed_roots", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.False(payload.RootElement.GetProperty("argumentOverrideApplied").GetBoolean());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task StricterToolOverrideCanBlockWhenServerLevelRulesAllow()
    {
        await using var upstream = await StartUpstreamAsync();
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            dbPath,
            serverAllowedRoots: [],
            serverBlockTraversal: false,
            overridesBlock:
            """
            overrides:
              fs.strict-read:
                paths:
                  allowedRoots:
                    - /workspace
                  blockTraversal: true
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        var body = """{"jsonrpc":"2.0","id":602,"method":"tools/call","params":{"name":"fs.strict-read","arguments":{"path":"/etc/passwd"}}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            using var responsePayload = JsonDocument.Parse(responseBody);
            var reasons = responsePayload.RootElement
                .GetProperty("error")
                .GetProperty("data")
                .GetProperty("reasons");

            Assert.Contains(reasons.EnumerateArray(), reason => reason.GetString() == "path_outside_allowed_roots");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tool_call_policy");
        Assert.Equal("block", row.Decision);
        Assert.Equal("fs.strict-read", row.ToolName);
        Assert.Contains("path_outside_allowed_roots", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.True(payload.RootElement.GetProperty("argumentOverrideApplied").GetBoolean());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public void InvalidToolOverridePreventsHostStartup()
    {
        var policyPath = WriteRawTempPolicy(CreatePolicy(
            upstreamBaseAddress: "http://127.0.0.1:8080",
            sqlitePath: NewTempSqlitePath(),
            serverAllowedRoots: ["/workspace"],
            serverBlockTraversal: true,
            overridesBlock:
            """
            overrides:
              fs.safe-read:
                paths: {}
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var ex = Assert.Throws<PolicyValidationException>(() => factory.CreateClient());
            Assert.Contains(
                "servers.github.arguments.overrides.fs.safe-read must define at least one argument rule override",
                ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
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

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.yaml");
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string WriteRawTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE policy_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at_utc TEXT NOT NULL,
                    policy_hash TEXT NOT NULL,
                    yaml TEXT NOT NULL,
                    requested_by TEXT NULL,
                    note TEXT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO policy_snapshots (created_at_utc, policy_hash, yaml)
            VALUES ($created_at_utc, $policy_hash, $yaml);
            """;
        command.Parameters.AddWithValue("$created_at_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
        command.Parameters.AddWithValue("$policy_hash", "sha256:invalid");
        command.Parameters.AddWithValue("$yaml", yaml);
        command.ExecuteNonQuery();
        return path;
    }

    private static string NewTempSqlitePath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db");

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
        string upstreamBaseAddress,
        string sqlitePath,
        IReadOnlyCollection<string> serverAllowedRoots,
        bool serverBlockTraversal,
        string overridesBlock)
    {
        var rootsBlock = serverAllowedRoots.Count == 0
            ? "allowedRoots: []"
            : "allowedRoots:\n                  - " + string.Join("\n                  - ", serverAllowedRoots);

        return $$"""
        mode: enforce
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
          github:
            route: /github/mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: allow
              allow: []
              block: []
            arguments:
              paths:
                {{rootsBlock}}
                blockTraversal: {{(serverBlockTraversal ? "true" : "false")}}
              hosts:
                allow: []
                blockPrivateNetworks: false
              commands:
                blockShell: false
                dangerous: []
        """ + "\n" + Indent(overridesBlock, 6);
    }

    private static string Indent(string block, int spaces)
    {
        var prefix = new string(' ', spaces);
        var lines = block.ReplaceLineEndings("\n").Split('\n');
        return string.Join(
            "\n",
            lines
                .Where(line => line.Length > 0)
                .Select(line => prefix + line));
    }

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

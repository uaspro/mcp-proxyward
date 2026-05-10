using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Persistence;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementPolicyApplyEndpointTests : IAsyncLifetime
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string AdminTokenEnv = "PROXYWARD_MANAGEMENT_ADMIN_TOKEN";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-policy-apply-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, null);
        Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        DeleteDbFiles(_databasePath);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task PolicyApplyRequiresAdminAuthorization()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.YarpConfigs);
        Assert.Empty(stub.PolicySnapshots);
    }

    [Fact]
    public async Task PolicyApplyValidYamlCallsYarpThenPolicySnapshotAndWritesAudit()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        await SeedPolicyAsync(CurrentPolicyYaml());

        var stub = new StubProxyControlClient
        {
            Status = new ProxyControlStatus("audit", "sha256:old", 1, 1),
            PolicySnapshotResult = new ProxyControlStatus("enforce", "sha256:new", 2, 4),
            YarpResult = new ProxyControlYarpConfigStatus(4, 10, 2)
        };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}},"requestedBy":"alice","note":"rollout"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["yarp", "policy"], stub.CallOrder);
        Assert.Single(stub.PolicySnapshots);

        var yarp = Assert.Single(stub.YarpConfigs);
        Assert.Equal(10, yarp.Routes.Count);
        Assert.Equal(2, yarp.Clusters.Count);
        Assert.Contains(yarp.Routes, route => route.RouteId == "github-exact" && route.Match.Path == "/github/mcp");
        Assert.Contains(
            yarp.Routes,
            route => route.RouteId == "github-well-known-0"
                && route.Match.Path == "/.well-known/oauth-protected-resource/github/mcp");
        Assert.Contains(yarp.Clusters, cluster => cluster.ClusterId == "filesystem");

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;
        Assert.Equal("audit", root.GetProperty("previousMode").GetString());
        Assert.Equal("enforce", root.GetProperty("mode").GetString());
        Assert.Equal("sha256:old", root.GetProperty("previousPolicyHash").GetString());
        Assert.Equal("sha256:new", root.GetProperty("policyHash").GetString());
        Assert.Equal(10, root.GetProperty("yarp").GetProperty("routeCount").GetInt32());
        Assert.Equal(2, root.GetProperty("yarp").GetProperty("clusterCount").GetInt32());

        var audit = Assert.Single(await ReadPolicyApplyAuditRowsAsync());
        Assert.Equal("policy_apply", audit.EventType);
        Assert.Equal("management", audit.Mode);
        Assert.Equal("allow", audit.Decision);
        Assert.Equal("policy/apply", audit.Method);
        Assert.Equal("policy_apply_accepted", audit.Reasons);
        Assert.Equal("sha256:new", audit.PolicyVersion);
        Assert.Contains("\"previousPolicyHash\":\"sha256:old\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"policyHash\":\"sha256:new\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"requestedBy\":\"alice\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: enforce", audit.PayloadJson, StringComparison.Ordinal);

        var persistedYaml = (await new SqlitePolicyStore(_databasePath).ReadCurrentAsync())!.Yaml;
        Assert.Contains("mode: enforce", persistedYaml, StringComparison.Ordinal);
        Assert.Contains("filesystem:", persistedYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("current:", persistedYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyApplyAcceptsStructuredModelRequest()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        await SeedPolicyAsync(CurrentPolicyYaml());

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody(StructuredModelApplyRequestJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["yarp", "policy"], stub.CallOrder);
        Assert.Single(stub.PolicySnapshots);
        Assert.Single(stub.YarpConfigs);

        var persistedYaml = (await new SqlitePolicyStore(_databasePath).ReadCurrentAsync())!.Yaml;
        Assert.Contains("github:", persistedYaml, StringComparison.Ordinal);
        Assert.Contains("route: /github/mcp", persistedYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyApplyPersistsSourceForSubsequentRead()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");
        await SeedPolicyAsync(CurrentPolicyYaml());

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var applyResponse = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        using var readResponse = await client.GetAsync("/api/policy");

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        using var payload = await ReadJsonAsync(readResponse);
        var servers = payload.RootElement.GetProperty("model").GetProperty("servers");
        Assert.True(servers.TryGetProperty("filesystem", out _));
        Assert.False(servers.TryGetProperty("current", out _));
    }

    [Fact]
    public async Task PolicyApplyInvalidPolicyReturnsValidationErrorsAndDoesNotCallProxy()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(InvalidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(stub.YarpConfigs);
        Assert.Empty(stub.PolicySnapshots);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;
        Assert.Equal("policy_validation_failed", root.GetProperty("error").GetString());
        Assert.False(root.GetProperty("validation").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task PolicyApplyYarpFailureDoesNotCallPolicySnapshot()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient
        {
            YarpFailure = new ProxyControlClientException(
                "YARP rejected config.",
                "proxy_control_request_failed",
                statusCode: 400)
        };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["yarp"], stub.CallOrder);
        Assert.Single(stub.YarpConfigs);
        Assert.Empty(stub.PolicySnapshots);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;
        Assert.Equal("policy_apply_failed", root.GetProperty("error").GetString());
        Assert.Equal("yarp_config", root.GetProperty("phase").GetString());
        Assert.False(root.GetProperty("rollbackAttempted").GetBoolean());
    }

    [Fact]
    public async Task PolicyApplyPolicySnapshotFailureAttemptsYarpRollback()
    {
        await SeedPolicyAsync(CurrentPolicyYaml());
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient
        {
            PolicySnapshotFailure = new ProxyControlClientException(
                "Policy snapshot failed.",
                "proxy_control_request_failed",
                statusCode: 500)
        };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PutAsync(
            "/api/policy",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["yarp", "policy", "yarp"], stub.CallOrder);
        Assert.Equal(2, stub.YarpConfigs.Count);
        Assert.Single(stub.PolicySnapshots);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;
        Assert.Equal("policy_apply_failed", root.GetProperty("error").GetString());
        Assert.Equal("policy_snapshot", root.GetProperty("phase").GetString());
        Assert.True(root.GetProperty("rollbackAttempted").GetBoolean());
        Assert.True(root.GetProperty("rollbackApplied").GetBoolean());
    }

    private async Task<IReadOnlyList<PolicyApplyAuditRow>> ReadPolicyApplyAuditRowsAsync()
    {
        var rows = new List<PolicyApplyAuditRow>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, mode, decision, server_id, method, tool_name, reasons, policy_version, payload_json
            FROM audit_events
            WHERE event_type = 'policy_apply'
            ORDER BY id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PolicyApplyAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }

    private static WebApplicationFactory<ManagementProgram> CreateFactory(IProxyControlClient stub) =>
        new WebApplicationFactory<ManagementProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(stub);
                services.AddSingleton<IProxyControlClient>(stub);
            }));

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private async Task SeedPolicyAsync(string yaml) =>
        await new SqlitePolicyStore(_databasePath).SaveAsync(yaml);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static void DeleteDbFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private sealed class StubProxyControlClient : IProxyControlClient
    {
        public ProxyControlStatus Status { get; set; } =
            new("audit", "sha256:old", 1, 1);

        public ProxyControlStatus PolicySnapshotResult { get; set; } =
            new("enforce", "sha256:new", 2, 4);

        public ProxyControlYarpConfigStatus YarpResult { get; set; } =
            new(4, 4, 2);

        public ProxyControlClientException? YarpFailure { get; set; }

        public ProxyControlClientException? PolicySnapshotFailure { get; set; }

        public List<string> CallOrder { get; } = [];

        public List<ProxyControlYarpConfigRequest> YarpConfigs { get; } = [];

        public List<string> PolicySnapshots { get; } = [];

        public Task<ProxyControlProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlProbeResult(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: Status.ToDetails()));

        public Task<ProxyControlStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<ProxyControlStatus> ApplyModeAsync(string mode, CancellationToken cancellationToken)
        {
            CallOrder.Add("mode");
            Status = Status with { Mode = mode };
            return Task.FromResult(Status);
        }

        public Task<ProxyControlStatus> ApplyPolicySnapshotAsync(string yaml, CancellationToken cancellationToken)
        {
            CallOrder.Add("policy");
            PolicySnapshots.Add(yaml);
            if (PolicySnapshotFailure is not null)
            {
                throw PolicySnapshotFailure;
            }

            Status = PolicySnapshotResult;
            return Task.FromResult(PolicySnapshotResult);
        }

        public Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
            ProxyControlYarpConfigRequest request,
            CancellationToken cancellationToken)
        {
            CallOrder.Add("yarp");
            YarpConfigs.Add(request);
            if (YarpFailure is not null)
            {
                throw YarpFailure;
            }

            return Task.FromResult(YarpResult);
        }
    }

    private sealed record PolicyApplyAuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PolicyVersion,
        string PayloadJson);

    private static string CurrentPolicyYaml() =>
        ValidPolicyYaml()
            .Replace("mode: enforce", "mode: audit", StringComparison.Ordinal)
            .Replace("filesystem:", "current:", StringComparison.Ordinal)
            .Replace("route: /fs/mcp", "route: /current/mcp", StringComparison.Ordinal);

    private static string InvalidPolicyYaml() =>
        ValidPolicyYaml().Replace("route: /github/mcp", "route: github/mcp", StringComparison.Ordinal);

    private static string ValidPolicyYaml() =>
        """
        mode: enforce
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: ./data/proxyward.db
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
            upstream: https://github.example/mcp
            allowed: true
            tools:
              default: deny
              allow:
                - repos.search
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow:
                  - api.github.com
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - curl
          filesystem:
            route: /fs/mcp
            upstream: http://localhost:8090/mcp
            allowed: true
            tools:
              default: allow
              allow: []
              block: []
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

    private static string StructuredModelApplyRequestJson() =>
        """
        {
          "model": {
            "mode": "enforce",
            "inspection": {
              "maxBodyBytes": 1048576,
              "unsupportedStreaming": "warn",
              "batchToolCalls": "failClosed"
            },
            "audit": {
              "sink": "sqlite",
              "sqlitePath": "./data/proxyward.db"
            },
            "observability": {
              "serviceName": "mcp-proxyward",
              "console": { "enabled": true },
              "otlp": {
                "enabled": false,
                "endpoint": "http://otel-collector:4317"
              },
              "applicationInsights": {
                "enabled": false,
                "connectionStringEnv": "APPLICATIONINSIGHTS_CONNECTION_STRING"
              },
              "sampling": { "tracesRatio": 1.0 }
            },
            "servers": {
              "github": {
                "id": "github",
                "route": "/github/mcp",
                "upstream": "https://github.example/mcp",
                "allowed": true,
                "tools": {
                  "default": "deny",
                  "allow": ["repos.search"],
                  "block": []
                },
                "arguments": {
                  "paths": {
                    "allowedRoots": ["/workspace"],
                    "blockTraversal": true
                  },
                  "hosts": {
                    "allow": ["api.github.com"],
                    "blockPrivateNetworks": true
                  },
                  "commands": {
                    "blockShell": true,
                    "dangerous": ["curl"]
                  },
                  "overrides": {}
                }
              }
            }
          }
        }
        """;
}

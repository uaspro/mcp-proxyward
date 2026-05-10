using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Api.Status;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementStatusEndpointTests
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string ProxyControlTokenEnv = "PROXYWARD_PROXY_CONTROL_TOKEN";

    [Fact]
    public async Task StatusEndpointReturnsHealthyWhenAllComponentsHealthy()
    {
        var dbPath = TempDbPath();
        await EnsureAuditDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);
        Environment.SetEnvironmentVariable(ProxyControlTokenEnv, "test-token");

        var stub = new StubProxyControlClient
        {
            Result = new ProxyControlProbeResult(
                ComponentStatusValues.Healthy,
                null,
                new Dictionary<string, object?>
                {
                    ["mode"] = "audit",
                    ["policyVersion"] = "p-1",
                    ["serverCount"] = 1,
                    ["routeVersion"] = 1
                })
        };

        try
        {
            await using var factory = CreateFactory(stub);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal("healthy", root.GetProperty("status").GetString());
            Assert.Equal("MCP ProxyWard Management API", root.GetProperty("service").GetString());

            var components = root.GetProperty("components");
            Assert.Equal("healthy", components.GetProperty("managementApi").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("proxyControl").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("auditDb").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("telemetry").GetProperty("status").GetString());
            // schemaLock may be unknown if tool_schema_versions has not been bootstrapped — that does NOT degrade top status.
            var schemaLockStatus = components.GetProperty("schemaLock").GetProperty("status").GetString();
            Assert.Contains(schemaLockStatus, new[] { "healthy", "unknown" });

            // Audit DB details should expose the path (already exposed today).
            Assert.Equal(dbPath, components.GetProperty("auditDb").GetProperty("details").GetProperty("sqlitePath").GetString());

            // Telemetry source is audit-db.
            Assert.Equal("audit-db", components.GetProperty("telemetry").GetProperty("details").GetProperty("source").GetString());

            // Proxy control details forwarded from stub probe result.
            Assert.Equal("audit", components.GetProperty("proxyControl").GetProperty("details").GetProperty("mode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReturnsDegradedWhenProxyControlIsUnreachable()
    {
        var dbPath = TempDbPath();
        await EnsureAuditDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);
        Environment.SetEnvironmentVariable(ProxyControlTokenEnv, "test-token");

        var stub = new StubProxyControlClient
        {
            Result = new ProxyControlProbeResult(
                ComponentStatusValues.Degraded,
                "proxy probe failed (transport error)",
                null)
        };

        try
        {
            await using var factory = CreateFactory(stub);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal("degraded", root.GetProperty("status").GetString());

            var components = root.GetProperty("components");
            Assert.Equal("degraded", components.GetProperty("proxyControl").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("auditDb").GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReturnsUnhealthyWhenAuditDbIsMissing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proxyward-management-status-missing-{Guid.NewGuid():N}.db");
        // Do NOT create the file — auditDb probe should fail.
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);
        Environment.SetEnvironmentVariable(ProxyControlTokenEnv, "test-token");

        var stub = new StubProxyControlClient
        {
            Result = new ProxyControlProbeResult(ComponentStatusValues.Healthy, null, null)
        };

        try
        {
            await using var factory = CreateFactory(stub);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal("unhealthy", root.GetProperty("status").GetString());

            var components = root.GetProperty("components");
            Assert.Equal("unhealthy", components.GetProperty("auditDb").GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReportsProxyControlUnknownWhenTokenNotConfigured()
    {
        var dbPath = TempDbPath();
        await EnsureAuditDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(AuditDbEnv, dbPath);
        Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);

        // The stub should never be called, but provide one anyway for safety.
        var stub = new StubProxyControlClient
        {
            Result = new ProxyControlProbeResult(
                ComponentStatusValues.Unknown,
                "control token not configured",
                null)
        };

        try
        {
            await using var factory = CreateFactory(stub);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal("healthy", root.GetProperty("status").GetString());

            var proxyControl = root.GetProperty("components").GetProperty("proxyControl");
            Assert.Equal("unknown", proxyControl.GetProperty("status").GetString());
            Assert.Contains("control token not configured",
                proxyControl.GetProperty("notes").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditDbEnv, null);
        }
    }

    private static WebApplicationFactory<ManagementProgram> CreateFactory(IProxyControlClient stub) =>
        new WebApplicationFactory<ManagementProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IProxyControlClient>(stub);
            }));

    private static async Task EnsureAuditDbExistsAsync(string dbPath)
    {
        // Triggers schema bootstrap by writing a single throwaway row.
        using var sink = new SqliteAuditSink(dbPath);
        await sink.WriteAsync(new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow.AddDays(-30),
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "alpha",
            Method: "tools/list",
            ToolName: null,
            Reasons: ["allowed"],
            PolicyVersion: "policy-1",
            CorrelationId: "corr-status-test",
            RequestBytes: 0,
            DurationMs: 1,
            ArgumentSummary: JsonNode.Parse("""{"token":"[redacted]"}"""),
            BatchSize: 0), CancellationToken.None);
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-management-status-{Guid.NewGuid():N}.db");

    private sealed class StubProxyControlClient : IProxyControlClient
    {
        public ProxyControlProbeResult Result { get; set; } =
            new ProxyControlProbeResult(ComponentStatusValues.Unknown, null, null);

        public Task<ProxyControlProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<ProxyControlStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlStatus(
                Mode: "audit",
                PolicyVersion: "sha256:stub",
                ServerCount: 1,
                RouteVersion: 1));

        public Task<ProxyControlStatus> ApplyModeAsync(string mode, CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlStatus(
                Mode: mode,
                PolicyVersion: "sha256:stub-applied",
                ServerCount: 1,
                RouteVersion: 1));

        public Task<ProxyControlStatus> ApplyPolicySnapshotAsync(string yaml, CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlStatus(
                Mode: "audit",
                PolicyVersion: "sha256:stub-applied",
                ServerCount: 1,
                RouteVersion: 1));

        public Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
            ProxyControlYarpConfigRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlYarpConfigStatus(
                RouteVersion: 1,
                RouteCount: request.Routes.Count,
                ClusterCount: request.Clusters.Count));
    }
}

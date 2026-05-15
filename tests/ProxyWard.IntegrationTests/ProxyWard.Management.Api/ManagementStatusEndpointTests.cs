using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Status;
using ProxyWard.Management.Infrastructure.Status;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementStatusEndpointTests
{
    private const string PersistenceDbEnv = "PROXYWARD_DB_PATH";
    private const string ProxyControlTokenEnv = "PROXYWARD_PROXY_CONTROL_TOKEN";

    [Fact]
    public async Task StatusEndpointReturnsHealthyWhenAllComponentsHealthy()
    {
        var dbPath = TempDbPath();
        await EnsurePersistenceDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);
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
            Assert.Equal("healthy", components.GetProperty("persistenceDb").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("telemetry").GetProperty("status").GetString());
            // schemaLock may be unknown if tool_schema_versions has not been bootstrapped — that does NOT degrade top status.
            var schemaLockStatus = components.GetProperty("schemaLock").GetProperty("status").GetString();
            Assert.Contains(schemaLockStatus, new[] { "healthy", "unknown" });

            var persistenceDetails = components.GetProperty("persistenceDb").GetProperty("details");
            Assert.Equal("sqlite", persistenceDetails.GetProperty("provider").GetString());
            Assert.Equal($"sqlite:{Path.GetFullPath(dbPath)}", persistenceDetails.GetProperty("source").GetString());

            // Telemetry reads persisted audit rows from the persistence DB.
            Assert.Equal("persistence-db", components.GetProperty("telemetry").GetProperty("details").GetProperty("source").GetString());

            // Proxy control details forwarded from stub probe result.
            Assert.Equal("audit", components.GetProperty("proxyControl").GetProperty("details").GetProperty("mode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReturnsDegradedWhenProxyControlIsUnreachable()
    {
        var dbPath = TempDbPath();
        await EnsurePersistenceDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);
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
            Assert.Equal("healthy", components.GetProperty("persistenceDb").GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReturnsUnhealthyWhenPersistenceDbIsMissing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proxyward-management-status-missing-{Guid.NewGuid():N}.db");
        // Do NOT create the file — persistenceDb probe should fail.
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);
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

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            Assert.Equal("unhealthy", root.GetProperty("status").GetString());

            var components = root.GetProperty("components");
            Assert.Equal("unhealthy", components.GetProperty("persistenceDb").GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
            Environment.SetEnvironmentVariable(ProxyControlTokenEnv, null);
        }
    }

    [Fact]
    public async Task StatusEndpointReportsProxyControlUnknownWhenTokenNotConfigured()
    {
        var dbPath = TempDbPath();
        await EnsurePersistenceDbExistsAsync(dbPath);
        Environment.SetEnvironmentVariable(PersistenceDbEnv, dbPath);
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
            Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
        }
    }

    [Fact]
    public async Task ProxyControlClientPreservesConfiguredBasePath()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "mode": "audit",
                  "policyVersion": "policy-1",
                  "serverCount": 2,
                  "routeVersion": 3
                }
                """)
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://proxy.local/admin/")
        };
        var options = new ManagementApiOptions(
            SqliteDatabasePath: TempDbPath(),
            ProxyControlBaseUrl: httpClient.BaseAddress,
            ProxyControlToken: "test-token",
            AdminToken: null,
            LocalDevelopmentMode: false,
            CorsAllowedOrigins: []);
        var client = new HttpProxyControlClient(httpClient, options);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.Equal("audit", status.Mode);
        Assert.Equal(new Uri("http://proxy.local/admin/control/status"), handler.LastRequestUri);
        Assert.Equal("Bearer", handler.LastAuthorization?.Scheme);
        Assert.Equal("test-token", handler.LastAuthorization?.Parameter);
    }

    private static WebApplicationFactory<ManagementProgram> CreateFactory(IProxyControlClient stub) =>
        new WebApplicationFactory<ManagementProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IProxyControlClient>(stub);
            }));

    private static async Task EnsurePersistenceDbExistsAsync(string dbPath)
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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            return Task.FromResult(responseFactory(request));
        }
    }
}

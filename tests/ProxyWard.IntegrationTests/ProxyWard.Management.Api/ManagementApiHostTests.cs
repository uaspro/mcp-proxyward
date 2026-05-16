using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ManagementMarker = ProxyWard.Management.Api.ProxyWardManagementApiMarker;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementApiHostTests
{
    [Fact]
    public async Task StatusEndpointReturnsConfiguredManagementMetadata()
    {
        var persistenceDbPath = TestFiles.NewSqlitePath("proxyward-management");
        await EnsurePersistenceDbExistsAsync(persistenceDbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", persistenceDbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL", "http://127.0.0.1:8089");

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var root = payload.RootElement;

            // Top-level status: healthy when persistence DB is reachable, even though proxyControl is unknown
            // (no PROXYWARD_PROXY_CONTROL_TOKEN configured). unknown components do not degrade top.
            Assert.Equal("healthy", root.GetProperty("status").GetString());
            Assert.Equal("MCP ProxyWard Management API", root.GetProperty("service").GetString());

            var components = root.GetProperty("components");
            Assert.Equal("healthy", components.GetProperty("managementApi").GetProperty("status").GetString());
            Assert.Equal("unknown", components.GetProperty("proxyControl").GetProperty("status").GetString());
            Assert.Equal("healthy", components.GetProperty("persistenceDb").GetProperty("status").GetString());
            var persistenceDetails = components.GetProperty("persistenceDb").GetProperty("details");
            Assert.Equal("sqlite", persistenceDetails.GetProperty("provider").GetString());
            Assert.Equal($"sqlite:{Path.GetFullPath(persistenceDbPath)}", persistenceDetails.GetProperty("source").GetString());
            Assert.Equal(
                "persistence-db",
                components.GetProperty("telemetry").GetProperty("details").GetProperty("source").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            Environment.SetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL", null);
        }
    }

    private static async Task EnsurePersistenceDbExistsAsync(string dbPath)
    {
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
            CorrelationId: "corr-host-test",
            RequestBytes: 0,
            DurationMs: 1,
            ArgumentSummary: JsonNode.Parse("""{"token":"[redacted]"}"""),
            BatchSize: 0), CancellationToken.None);
    }

    [Fact]
    public void ProxyDataPlaneDoesNotReferenceManagementApi()
    {
        var references = typeof(global::Program)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("ProxyWard.Management.Api", references);
    }

    [Fact]
    public void ManagementApiDoesNotReferenceProxyDataPlane()
    {
        var references = typeof(ManagementMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("ProxyWard.Api", references);
    }
}

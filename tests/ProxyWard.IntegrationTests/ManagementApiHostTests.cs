using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ManagementMarker = ProxyWard.Management.Api.ProxyWardManagementApiMarker;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementApiHostTests
{
    [Fact]
    public async Task StatusEndpointReturnsConfiguredManagementMetadata()
    {
        var auditDbPath = Path.Combine(Path.GetTempPath(), $"proxyward-management-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("PROXYWARD_MANAGEMENT_AUDIT_DB_PATH", auditDbPath);
        Environment.SetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL", "http://127.0.0.1:8089");

        try
        {
            await using var factory = new WebApplicationFactory<ManagementProgram>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("healthy", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("MCP ProxyWard Management API", payload.RootElement.GetProperty("service").GetString());
            Assert.Equal(auditDbPath, payload.RootElement.GetProperty("audit").GetProperty("sqlitePath").GetString());
            Assert.Equal(
                "http://127.0.0.1:8089/",
                payload.RootElement.GetProperty("proxyControl").GetProperty("baseUrl").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_MANAGEMENT_AUDIT_DB_PATH", null);
            Environment.SetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL", null);
        }
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

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.IntegrationTests;

public class ApiHostTests
{
    [Fact]
    public async Task HealthEndpointIncludesLoadedPolicyMetadata()
    {
        var policyPath = WriteTempPolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("healthy", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("audit", payload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("serverCount").GetInt32());
            Assert.StartsWith("sha256:", payload.RootElement.GetProperty("policyVersion").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public void InvalidPolicyPreventsHostStartup()
    {
        var policyPath = WriteTempPolicy("""
            mode: audit
            """);
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var ex = Assert.Throws<PolicyValidationException>(() => factory.CreateClient());
            Assert.Contains("Invalid ProxyWard policy", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public void RemovedLockfileKeyPreventsHostStartup()
    {
        var policyPath = WriteTempPolicy(ValidYaml.Replace(
            "servers:",
            "lockfile: ./proxyward.lock.yaml\nservers:",
            StringComparison.Ordinal));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var ex = Assert.Throws<PolicyValidationException>(() => factory.CreateClient());
            Assert.Contains(ProxyWardPolicyLoader.RemovedLockfileMessage, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private const string ValidYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
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
          sample:
            route: /sample/mcp
            upstream: http://localhost:8080/mcp
            allowed: true
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
}

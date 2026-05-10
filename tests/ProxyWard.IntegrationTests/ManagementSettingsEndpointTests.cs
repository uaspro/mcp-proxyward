using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementSettingsEndpointTests : IAsyncLifetime
{
    private const string PolicyPathEnv = "PROXYWARD_MANAGEMENT_POLICY_PATH";

    private readonly string _policyPath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-settings-{Guid.NewGuid():N}.yaml");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(PolicyPathEnv, null);

        if (File.Exists(_policyPath))
        {
            File.Delete(_policyPath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SettingsEndpointReturnsReadOnlyPolicyBackedSettings()
    {
        await File.WriteAllTextAsync(_policyPath, PolicyYaml());
        Environment.SetEnvironmentVariable(PolicyPathEnv, _policyPath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var payload = await JsonDocument.ParseAsync(stream);
        var root = payload.RootElement;

        var observability = root.GetProperty("observability");
        Assert.Equal("mcp-proxyward-settings", observability.GetProperty("serviceName").GetString());
        Assert.True(observability.GetProperty("consoleEnabled").GetBoolean());
        Assert.True(observability.GetProperty("otlpEnabled").GetBoolean());
        Assert.Equal("http://otel-collector:4317", observability.GetProperty("otlpEndpoint").GetString());
        Assert.False(observability.GetProperty("applicationInsightsEnabled").GetBoolean());
        Assert.Equal(0.75, observability.GetProperty("tracesRatio").GetDouble());

        var audit = root.GetProperty("audit");
        Assert.Equal("sqlite", audit.GetProperty("sink").GetString());
        Assert.Equal("./data/settings.db", audit.GetProperty("sqlitePath").GetString());

        var inspection = root.GetProperty("inspection");
        Assert.Equal(1048576, inspection.GetProperty("maxBodyBytes").GetInt32());
        Assert.Equal("block", inspection.GetProperty("unsupportedStreaming").GetString());
        Assert.Equal("failClosed", inspection.GetProperty("batchToolCalls").GetString());

        var service = root.GetProperty("service");
        Assert.StartsWith("sha256:", service.GetProperty("policyHash").GetString(), StringComparison.Ordinal);
        Assert.Equal(_policyPath, service.GetProperty("sourcePath").GetString());
        Assert.Equal(1, service.GetProperty("serverCount").GetInt32());
        Assert.True(service.GetProperty("sourceSizeBytes").GetInt64() > 0);

        var runtime = root.GetProperty("runtime");
        Assert.False(runtime.GetProperty("editingSupported").GetBoolean());
        Assert.False(runtime.GetProperty("settingsWritable").GetBoolean());
    }

    private static string PolicyYaml() =>
        """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: block
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: ./data/settings.db
        observability:
          serviceName: mcp-proxyward-settings
          console:
            enabled: true
          otlp:
            enabled: true
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 0.75
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
                  - bash
              overrides: {}
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
        """;
}

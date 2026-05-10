using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementPolicyEndpointTests : IAsyncLifetime
{
    private const string PolicyPathEnv = "PROXYWARD_MANAGEMENT_POLICY_PATH";

    private readonly string _policyPath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-policy-{Guid.NewGuid():N}.yaml");

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
    public async Task PolicyEndpointReturnsYamlModelHashSourceAndReadOnlyFields()
    {
        await File.WriteAllTextAsync(_policyPath, PolicyYaml());
        Environment.SetEnvironmentVariable(PolicyPathEnv, _policyPath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var payload = await JsonDocument.ParseAsync(stream);
        var root = payload.RootElement;

        Assert.StartsWith("sha256:", root.GetProperty("policyHash").GetString(), StringComparison.Ordinal);

        var yaml = root.GetProperty("yaml").GetString();
        Assert.NotNull(yaml);
        Assert.Contains("mode", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATIONINSIGHTS_CONNECTION_STRING", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("operator:credential", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("top-level-secret", yaml, StringComparison.Ordinal);

        var source = root.GetProperty("source");
        Assert.Equal(_policyPath, source.GetProperty("path").GetString());
        Assert.Equal("yaml", source.GetProperty("format").GetString());
        Assert.True(source.GetProperty("exists").GetBoolean());
        Assert.True(source.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("lastModifiedUtc").GetString()));

        var readOnly = root.GetProperty("readOnly");
        Assert.Equal(root.GetProperty("policyHash").GetString(), readOnly.GetProperty("policyHash").GetString());
        Assert.Equal(_policyPath, readOnly.GetProperty("sourcePath").GetString());
        Assert.Equal(2, readOnly.GetProperty("serverCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(readOnly.GetProperty("loadedAtUtc").GetString()));

        var model = root.GetProperty("model");
        Assert.Equal("audit", model.GetProperty("mode").GetString());
        Assert.Equal(2097152, model.GetProperty("inspection").GetProperty("maxBodyBytes").GetInt32());
        Assert.Equal("warn", model.GetProperty("inspection").GetProperty("unsupportedStreaming").GetString());
        Assert.Equal("failClosed", model.GetProperty("inspection").GetProperty("batchToolCalls").GetString());
        Assert.Equal("sqlite", model.GetProperty("audit").GetProperty("sink").GetString());
        Assert.Equal("./data/policy-read.db", model.GetProperty("audit").GetProperty("sqlitePath").GetString());
        Assert.Equal("mcp-proxyward-test", model.GetProperty("observability").GetProperty("serviceName").GetString());
        Assert.True(model.GetProperty("observability").GetProperty("console").GetProperty("enabled").GetBoolean());
        Assert.True(model.GetProperty("observability").GetProperty("otlp").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "http://otel-collector:4317",
            model.GetProperty("observability").GetProperty("otlp").GetProperty("endpoint").GetString());
        Assert.False(model.GetProperty("observability").GetProperty("applicationInsights").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            model.GetProperty("observability").GetProperty("applicationInsights").GetProperty("connectionStringEnv").GetString());
        Assert.Equal(0.5, model.GetProperty("observability").GetProperty("sampling").GetProperty("tracesRatio").GetDouble());

        var servers = model.GetProperty("servers");
        var github = servers.GetProperty("github");
        Assert.Equal("github", github.GetProperty("id").GetString());
        Assert.Equal("/github/mcp", github.GetProperty("route").GetString());
        Assert.Equal("https://***@github.example/mcp", github.GetProperty("upstream").GetString());
        Assert.True(github.GetProperty("allowed").GetBoolean());
        Assert.Equal("deny", github.GetProperty("tools").GetProperty("default").GetString());
        Assert.Equal(["repos.read", "repos.search"], github.GetProperty("tools").GetProperty("allow")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray());
        Assert.Equal(["repos.delete"], github.GetProperty("tools").GetProperty("block")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray());
        Assert.Equal(["/tmp", "/workspace"], github.GetProperty("arguments").GetProperty("paths").GetProperty("allowedRoots")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray());
        Assert.True(github.GetProperty("arguments").GetProperty("paths").GetProperty("blockTraversal").GetBoolean());
        Assert.Equal(["api.github.com"], github.GetProperty("arguments").GetProperty("hosts").GetProperty("allow")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray());
        Assert.True(github.GetProperty("arguments").GetProperty("hosts").GetProperty("blockPrivateNetworks").GetBoolean());
        Assert.True(github.GetProperty("arguments").GetProperty("commands").GetProperty("blockShell").GetBoolean());
        Assert.Equal(["bash", "curl"], github.GetProperty("arguments").GetProperty("commands").GetProperty("dangerous")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray());

        var repoOverride = github.GetProperty("arguments").GetProperty("overrides").GetProperty("repos.search");
        Assert.Equal("repos.search", repoOverride.GetProperty("toolName").GetString());
        Assert.Equal(["/workspace/repositories"], repoOverride.GetProperty("paths").GetProperty("allowedRoots")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray());

        var filesystem = servers.GetProperty("filesystem");
        Assert.Equal("filesystem", filesystem.GetProperty("id").GetString());
        Assert.Equal("/fs/mcp", filesystem.GetProperty("route").GetString());
        Assert.Equal("http://localhost:8090/mcp", filesystem.GetProperty("upstream").GetString());
        Assert.False(filesystem.GetProperty("allowed").GetBoolean());
        Assert.Equal("allow", filesystem.GetProperty("tools").GetProperty("default").GetString());
    }

    [Fact]
    public async Task PolicyEndpointReturnsNotFoundForMissingPolicy()
    {
        Environment.SetEnvironmentVariable(PolicyPathEnv, _policyPath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/policy");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var payload = await JsonDocument.ParseAsync(stream);
        Assert.Equal("policy_not_found", payload.RootElement.GetProperty("error").GetString());
        Assert.Equal(_policyPath, payload.RootElement.GetProperty("path").GetString());
    }

    private static string PolicyYaml() =>
        """
        mode: audit
        adminToken: top-level-secret
        inspection:
          maxBodyBytes: 2097152
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: ./data/policy-read.db
        observability:
          serviceName: mcp-proxyward-test
          console:
            enabled: true
          otlp:
            enabled: true
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 0.5
        servers:
          github:
            route: /github/mcp
            upstream: https://operator:credential@github.example/mcp
            allowed: true
            tools:
              default: deny
              allow:
                - repos.search
                - repos.read
              block:
                - repos.delete
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                  - /tmp
                blockTraversal: true
              hosts:
                allow:
                  - api.github.com
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - bash
                  - curl
              overrides:
                repos.search:
                  paths:
                    allowedRoots:
                      - /workspace/repositories
                    blockTraversal: false
          filesystem:
            route: /fs/mcp
            upstream: http://localhost:8090/mcp
            allowed: false
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
}

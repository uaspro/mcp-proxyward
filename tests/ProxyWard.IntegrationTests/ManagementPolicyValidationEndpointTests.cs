using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementPolicyValidationEndpointTests : IAsyncLifetime
{
    private const string PolicyPathEnv = "PROXYWARD_MANAGEMENT_POLICY_PATH";

    private readonly string _activePolicyPath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-policy-validation-active-{Guid.NewGuid():N}.yaml");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(PolicyPathEnv, null);

        if (File.Exists(_activePolicyPath))
        {
            File.Delete(_activePolicyPath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateEndpointAcceptsYamlRequestAndReturnsNormalizedModelAndHash()
    {
        await File.WriteAllTextAsync(_activePolicyPath, ActivePolicyYaml());
        Environment.SetEnvironmentVariable(PolicyPathEnv, _activePolicyPath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.StartsWith("sha256:", root.GetProperty("policyHash").GetString(), StringComparison.Ordinal);

        var model = root.GetProperty("normalizedModel");
        Assert.Equal("audit", model.GetProperty("mode").GetString());
        Assert.Equal(1048576, model.GetProperty("inspection").GetProperty("maxBodyBytes").GetInt32());
        Assert.Equal("github", model.GetProperty("servers").GetProperty("github").GetProperty("id").GetString());
        Assert.Equal("https://github.example/mcp", model.GetProperty("servers").GetProperty("github").GetProperty("upstream").GetString());
        var defaultSecrets = model.GetProperty("servers").GetProperty("github").GetProperty("secrets");
        Assert.True(defaultSecrets.GetProperty("redactInLogs").GetBoolean());
        Assert.False(defaultSecrets.GetProperty("blockReturn").GetBoolean());
        Assert.Empty(defaultSecrets.GetProperty("patterns").EnumerateArray());
    }

    [Fact]
    public async Task ValidateEndpointAcceptsRawYamlBody()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            new StringContent(ValidPolicyYaml(), Encoding.UTF8, "application/x-yaml"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        Assert.True(payload.RootElement.GetProperty("valid").GetBoolean());
        Assert.StartsWith("sha256:", payload.RootElement.GetProperty("policyHash").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateEndpointAcceptsStructuredModelRequest()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody(StructuredModelRequestJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.StartsWith("sha256:", root.GetProperty("policyHash").GetString(), StringComparison.Ordinal);

        var model = root.GetProperty("normalizedModel");
        Assert.Equal("enforce", model.GetProperty("mode").GetString());
        Assert.Equal("allow", model.GetProperty("servers").GetProperty("github").GetProperty("tools").GetProperty("default").GetString());
        Assert.Equal(["repos.read"], model.GetProperty("servers").GetProperty("github").GetProperty("tools").GetProperty("block")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task ValidateEndpointReturnsFieldLevelErrorsForInvalidPolicy()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(InvalidPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.False(root.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("policyHash").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("normalizedModel").ValueKind);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());

        var errors = root.GetProperty("errors").EnumerateArray().ToArray();
        var fields = errors.Select(error => error.GetProperty("field").GetString()).ToArray();
        Assert.Contains("servers.github.route", fields);
        Assert.Contains("servers.github.upstream", fields);
        Assert.Contains("servers.github.tools.default", fields);
        Assert.Contains("servers.github.arguments.overrides.repos.search", fields);
        Assert.All(errors, error => Assert.Equal("policy_validation_error", error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task ValidateEndpointAcceptsStructuredSecretPolicy()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody(StructuredSecretModelRequestJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.True(root.GetProperty("valid").GetBoolean());
        var secrets = root.GetProperty("normalizedModel")
            .GetProperty("servers")
            .GetProperty("github")
            .GetProperty("secrets");
        Assert.True(secrets.GetProperty("redactInLogs").GetBoolean());
        Assert.True(secrets.GetProperty("blockReturn").GetBoolean());
        Assert.Equal(["/github_pat_[A-Za-z0-9_]+/", "ghp_"], secrets.GetProperty("patterns")
            .EnumerateArray()
            .Select(pattern => pattern.GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task ValidateEndpointReturnsFieldLevelErrorsForInvalidSecretRegex()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(InvalidSecretRegexPolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.False(root.GetProperty("valid").GetBoolean());
        var fields = root.GetProperty("errors")
            .EnumerateArray()
            .Select(error => error.GetProperty("field").GetString())
            .ToArray();
        Assert.Contains("servers.github.secrets.patterns[0]", fields);
    }

    [Fact]
    public async Task ValidateEndpointReturnsBadRequestForMalformedRequest()
    {
        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody("""{}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        Assert.Equal("policy_validation_request_invalid", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ValidateEndpointDoesNotModifyActivePolicyFile()
    {
        var activePolicy = ActivePolicyYaml();
        await File.WriteAllTextAsync(_activePolicyPath, activePolicy);
        Environment.SetEnvironmentVariable(PolicyPathEnv, _activePolicyPath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/api/policy/validate",
            JsonBody($$"""{"yaml":{{JsonSerializer.Serialize(ValidEnforcePolicyYaml())}}}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        Assert.True(payload.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(activePolicy, await File.ReadAllTextAsync(_activePolicyPath));
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string ActivePolicyYaml() =>
        """
        mode: audit
        inspection:
          maxBodyBytes: 4096
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: ./data/active.db
        observability:
          serviceName: active-proxyward
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
          active:
            route: /active/mcp
            upstream: http://localhost:8080/mcp
            allowed: true
            tools:
              default: deny
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

    private static string ValidPolicyYaml() =>
        """
        mode: audit
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
        """;

    private static string ValidEnforcePolicyYaml() =>
        ValidPolicyYaml().Replace("mode: audit", "mode: enforce", StringComparison.Ordinal);

    private static string InvalidPolicyYaml() =>
        """
        mode: audit
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
            route: github/mcp
            upstream: ftp://github.example/mcp
            allowed: true
            secrets:
              blockReturn: true
            tools:
              default: sometimes
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots: []
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous: []
              overrides:
                repos.search: {}
        """;

    private static string InvalidSecretRegexPolicyYaml()
    {
        var yaml = ValidPolicyYaml();
        var markerIndex = yaml.IndexOf("tools:", StringComparison.Ordinal);
        var lineStart = yaml.LastIndexOf('\n', markerIndex) + 1;
        var indent = yaml[lineStart..markerIndex];
        return yaml.Insert(
            lineStart,
            $"""
            {indent}secrets:
            {indent}  redactInLogs: true
            {indent}  blockReturn: true
            {indent}  patterns:
            {indent}    - /github_pat_(/
            """.ReplaceLineEndings("\n") + "\n");
    }

    private static string StructuredModelRequestJson() =>
        """
        {
          "model": {
            "mode": "enforce",
            "inspection": {
              "maxBodyBytes": 65536,
              "unsupportedStreaming": "block",
              "batchToolCalls": "failClosed"
            },
            "audit": {
              "sink": "sqlite",
              "sqlitePath": "./data/structured.db"
            },
            "observability": {
              "serviceName": "structured-proxyward",
              "console": { "enabled": false },
              "otlp": {
                "enabled": true,
                "endpoint": "http://otel-collector:4317"
              },
              "applicationInsights": {
                "enabled": false,
                "connectionStringEnv": "APPLICATIONINSIGHTS_CONNECTION_STRING"
              },
              "sampling": { "tracesRatio": 0.25 }
            },
            "servers": {
              "github": {
                "id": "github",
                "route": "/github/mcp",
                "upstream": "https://github.example/mcp",
                "allowed": true,
                "tools": {
                  "default": "allow",
                  "allow": [],
                  "block": ["repos.read"]
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

    private static string StructuredSecretModelRequestJson() =>
        """
        {
          "model": {
            "mode": "audit",
            "inspection": {
              "maxBodyBytes": 65536,
              "unsupportedStreaming": "warn",
              "batchToolCalls": "failClosed"
            },
            "audit": {
              "sink": "sqlite",
              "sqlitePath": "./data/structured.db"
            },
            "observability": {
              "serviceName": "structured-proxyward",
              "console": { "enabled": false },
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
                "secrets": {
                  "redactInLogs": true,
                  "blockReturn": true,
                  "patterns": ["ghp_", "/github_pat_[A-Za-z0-9_]+/"]
                },
                "tools": {
                  "default": "deny",
                  "allow": [],
                  "block": []
                },
                "arguments": {
                  "paths": {
                    "allowedRoots": [],
                    "blockTraversal": false
                  },
                  "hosts": {
                    "allow": [],
                    "blockPrivateNetworks": false
                  },
                  "commands": {
                    "blockShell": false,
                    "dangerous": []
                  },
                  "overrides": {}
                }
              }
            }
          }
        }
        """;
}

using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class PolicyConfigurationTests
{
    [Fact]
    public void LoadValidYamlReturnsStronglyTypedPolicy()
    {
        var policy = ProxyWardPolicyLoader.Load(ValidYaml);

        Assert.Equal(ProxyWardMode.Audit, policy.Mode);
        Assert.Equal(1_048_576, policy.Inspection.MaxBodyBytes);
        Assert.Equal(UnsupportedInspectionBehavior.Warn, policy.Inspection.UnsupportedStreaming);
        Assert.Equal(BatchToolCallBehavior.FailClosed, policy.Inspection.BatchToolCalls);
        Assert.Equal("sqlite", policy.Audit.Sink);
        Assert.Equal("./data/proxyward.db", policy.Audit.SqlitePath);
        Assert.Equal("mcp-proxyward", policy.Observability.ServiceName);
        Assert.Equal("APPLICATIONINSIGHTS_CONNECTION_STRING", policy.Observability.ApplicationInsights.ConnectionStringEnv);
        Assert.DoesNotContain(
            typeof(ProxyWardPolicy).GetProperties(),
            property => string.Equals(property.Name, "Lockfile", StringComparison.Ordinal));
        Assert.True(policy.Servers["sample"].Allowed);
        Assert.Equal("/sample/mcp", policy.Servers["sample"].Route);
        Assert.Equal(new Uri("http://localhost:8080/mcp"), policy.Servers["sample"].Upstream);
        Assert.Equal(ToolDefaultMode.Deny, policy.Servers["sample"].Tools.Default);
        Assert.True(policy.Servers["sample"].Secrets.RedactInLogs);
        Assert.False(policy.Servers["sample"].Secrets.BlockReturn);
        Assert.Empty(policy.Servers["sample"].Secrets.Patterns);
        Assert.StartsWith("sha256:", policy.VersionHash, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadExplicitFailClosedBatchToolCallBehaviorReturnsPolicy()
    {
        var yaml = ValidYaml.Replace(
            "unsupportedStreaming: warn",
            "unsupportedStreaming: warn\n  batchToolCalls: failClosed",
            StringComparison.Ordinal);

        var policy = ProxyWardPolicyLoader.Load(yaml);

        Assert.Equal(BatchToolCallBehavior.FailClosed, policy.Inspection.BatchToolCalls);
    }

    [Fact]
    public void LoadUnsupportedBatchToolCallBehaviorThrowsClearValidationException()
    {
        var yaml = ValidYaml.Replace(
            "unsupportedStreaming: warn",
            "unsupportedStreaming: warn\n  batchToolCalls: allowMixed",
            StringComparison.Ordinal);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("inspection.batchToolCalls must be 'failClosed'", ex.Message);
    }

    [Fact]
    public void LoadIncompleteYamlThrowsClearValidationException()
    {
        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load("""
            mode: audit
            """));

        Assert.Contains("inspection section is required", ex.Message);
        Assert.Contains("audit section is required", ex.Message);
        Assert.Contains("observability section is required", ex.Message);
        Assert.Contains("servers must contain at least one server", ex.Message);
    }

    [Fact]
    public void LoadYamlContainingRemovedLockfileKeyThrowsClearValidationException()
    {
        var yaml = ValidYaml.Replace(
            "servers:",
            "lockfile: ./proxyward.lock.yaml\nservers:",
            StringComparison.Ordinal);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Equal(ProxyWardPolicyLoader.RemovedLockfileMessage, Assert.Single(ex.Errors));
        Assert.Contains(ProxyWardPolicyLoader.RemovedLockfileMessage, ex.Message);
    }

    [Fact]
    public void PolicyVersionHashIsStableForRepeatedLoads()
    {
        var first = ProxyWardPolicyLoader.Load(ValidYaml);
        var second = ProxyWardPolicyLoader.Load(ValidYaml);

        Assert.Equal(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public void LoadToolArgumentOverridesReturnsTypedPolicy()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths:
                  allowedRoots: []
                  blockTraversal: false
                commands:
                  dangerous: []
            """);

        var policy = ProxyWardPolicyLoader.Load(yaml);

        var overrides = policy.Servers["sample"].Arguments.Overrides;
        var toolOverride = Assert.Contains("fs.safe-read", overrides);
        Assert.NotNull(toolOverride.Paths);
        Assert.Empty(toolOverride.Paths.AllowedRoots!);
        Assert.False(toolOverride.Paths.BlockTraversal!.Value);
        Assert.Null(toolOverride.Hosts);
        Assert.NotNull(toolOverride.Commands);
        Assert.Empty(toolOverride.Commands.Dangerous!);
        Assert.Null(toolOverride.Commands.BlockShell);
    }

    [Fact]
    public void LoadNoOpToolArgumentOverrideThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths: {}
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides.fs.safe-read must define at least one argument rule override",
            ex.Message);
    }

    [Fact]
    public void LoadEmptyToolArgumentOverrideNameThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              "":
                paths:
                  blockTraversal: false
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides contains an empty tool name",
            ex.Message);
    }

    [Fact]
    public void LoadNullToolArgumentOverrideThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides.fs.safe-read section is required",
            ex.Message);
    }

    [Fact]
    public void PolicyVersionHashChangesWhenToolArgumentOverrideChanges()
    {
        var firstYaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths:
                  blockTraversal: false
            """);
        var secondYaml = firstYaml.Replace("blockTraversal: false", "blockTraversal: true", StringComparison.Ordinal);

        var first = ProxyWardPolicyLoader.Load(firstYaml);
        var second = ProxyWardPolicyLoader.Load(secondYaml);

        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public void LoadSecretsPolicyReturnsTypedPolicy()
    {
        var policy = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
                - /github_pat_[A-Za-z0-9_]+/
            """));

        var secrets = policy.Servers["sample"].Secrets;
        Assert.True(secrets.RedactInLogs);
        Assert.True(secrets.BlockReturn);
        Assert.Equal(["/github_pat_[A-Za-z0-9_]+/", "ghp_"], secrets.Patterns);
    }

    [Fact]
    public void LoadInvalidSecretsRegexThrowsClearValidationException()
    {
        var yaml = ValidYamlWithSecrets(
            """
            secrets:
              patterns:
                - /github_pat_(/
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("servers.sample.secrets.patterns[0] is not a valid regex", ex.Message);
    }

    [Fact]
    public void LoadOverbroadSecretsRegexThrowsClearValidationException()
    {
        var yaml = ValidYamlWithSecrets(
            """
            secrets:
              patterns:
                - /.*/
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("servers.sample.secrets.patterns[0] is over-broad", ex.Message);
    }

    [Fact]
    public void PolicyVersionHashChangesWhenSecretsPolicyChanges()
    {
        var first = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: false
              patterns:
                - ghp_
            """));
        var second = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
            """));

        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    private static string ValidYamlWithOverrides(string overridesBlock) =>
        """
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
                  - curl
                  - wget
                  - nc
                  - powershell
                  - bash
        """ + "\n" + Indent(overridesBlock, 6);

    private static string ValidYamlWithSecrets(string secretsBlock) =>
        ValidYaml.Replace(
            "    tools:",
            Indent(secretsBlock, 4) + "\n    tools:",
            StringComparison.Ordinal);

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
                  - curl
                  - wget
                  - nc
                  - powershell
                  - bash
        """;
}

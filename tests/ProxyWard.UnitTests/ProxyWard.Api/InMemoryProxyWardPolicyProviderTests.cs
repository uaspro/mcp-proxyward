using ProxyWard.Api.Runtime;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class InMemoryProxyWardPolicyProviderTests
{
    [Fact]
    public void CapturedSnapshotRemainsStableAfterReplacement()
    {
        var initial = ProxyWardPolicyLoader.Load(ValidYaml);
        var replacement = ProxyWardPolicyLoader.Load(ValidYaml.Replace("mode: audit", "mode: enforce", StringComparison.Ordinal));
        var provider = new InMemoryProxyWardPolicyProvider(initial);

        var captured = provider.Current;

        provider.Replace(replacement);

        Assert.Same(initial, captured);
        Assert.Equal(ProxyWardMode.Audit, captured.Mode);
        Assert.Same(replacement, provider.Current);
        Assert.Equal(ProxyWardMode.Enforce, provider.Current.Mode);
        Assert.NotEqual(captured.VersionHash, provider.Current.VersionHash);
    }

    [Fact]
    public void ReplaceRejectsNullSnapshot()
    {
        var provider = new InMemoryProxyWardPolicyProvider(ProxyWardPolicyLoader.Load(ValidYaml));

        Assert.Throws<ArgumentNullException>(() => provider.Replace(null!));
    }

    private const string ValidYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
        audit:
          enabled: true
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

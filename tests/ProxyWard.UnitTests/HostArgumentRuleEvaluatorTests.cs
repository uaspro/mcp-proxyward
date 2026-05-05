using System.Net;
using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class HostArgumentRuleEvaluatorTests
{
    [Fact]
    public async Task UrlHostNotInAllowlistBlocksWithHostNotAllowedInEnforce()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://evil.example.com/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.HostNotAllowed, decision.Reasons);
    }

    [Fact]
    public async Task UrlHostNotInAllowlistInAuditModeReturnsWouldBlock()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://evil.example.com/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Audit, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.HostNotAllowed, decision.Reasons);
    }

    [Fact]
    public async Task UrlHostInAllowlistReturnsAllow()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://api.github.com/repos/foo/bar"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public async Task AllowlistComparisonIsCaseInsensitive()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://API.GITHUB.COM/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Fact]
    public async Task AllowlistRequiresExactHostNotSuffixMatch()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://evil.api.github.com/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.HostNotAllowed, decision.Reasons);
    }

    [Fact]
    public async Task EmptyAllowlistAndPrivateNetworkBlockingDisabledIsNoOp()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://evil.example.com/x","ip":"10.0.0.5"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.5.4")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task IpLiteralInPrivateOrLoopbackOrLinkLocalRangeBlocks(string ip)
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["ip"] = ip }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public async Task IpV6PrivateOrLoopbackOrLinkLocalLiteralBlocks(string ip)
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["ip"] = ip }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
    }

    [Fact]
    public async Task IpV4MappedIpV6PrivateLiteralBlocks()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["ip"] = "::ffff:10.0.0.5" }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
    }

    [Fact]
    public async Task UrlHostnameResolvingToPrivateIpBlocks()
    {
        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://internal.svc/api"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task UrlHostnameResolvingToPublicIpAllows()
    {
        var resolver = new StubHostResolver().WithHost("api.github.com", IPAddress.Parse("140.82.121.6"));
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://api.github.com/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public async Task BracketedIpV6UrlBlocksWithoutDnsCall()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://[::1]/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task MultiAddressHostWithAnyPrivateAddressBlocks()
    {
        var resolver = new StubHostResolver().WithHost(
            "mixed.example",
            IPAddress.Parse("140.82.121.6"),
            IPAddress.Parse("10.0.0.5"));
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://mixed.example/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
    }

    [Fact]
    public async Task DnsResolutionFailureWithBlockingEnabledFailsClosed()
    {
        var resolver = new StubHostResolver().WithFailingHost("unresolvable.example");
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://unresolvable.example/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
    }

    [Fact]
    public async Task AllowlistedHostnameStillCheckedForPrivateNetwork()
    {
        var resolver = new StubHostResolver().WithFailingHost("allowed.example");
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["allowed.example"], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://allowed.example/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
    }

    [Fact]
    public async Task DnsResolutionFailureWithBlockingDisabledAllows()
    {
        var resolver = new StubHostResolver().WithFailingHost("unresolvable.example");
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://unresolvable.example/x"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task AllowlistMissAndPrivateResolutionProduceBothReasonsInStableOrder()
    {
        var resolver = new StubHostResolver().WithHost("internal.svc", IPAddress.Parse("10.0.0.5"));
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://internal.svc/api"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal(
            [PolicyReasonCodes.HostNotAllowed, PolicyReasonCodes.PrivateNetworkTarget],
            decision.Reasons);
    }

    [Fact]
    public async Task NonStringScalarLeavesAreIgnored()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"size":42,"recursive":true,"limit":null}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Theory]
    [InlineData("repos.search")]
    [InlineData("issues.list")]
    [InlineData("hello world")]
    public async Task DottedToolNameStringsAreNotTreatedAsHosts(string value)
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["q"] = value }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceStringIsIgnored(string value)
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["url"] = value }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Fact]
    public async Task NullParamsReturnsAllow()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams: null, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Fact]
    public async Task ExplicitNullArgumentsFallsBackToScanningParams()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = new JsonObject
        {
            ["name"] = "fs.read",
            ["arguments"] = null,
            ["target"] = "https://evil.example.com/x"
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.HostNotAllowed, decision.Reasons);
    }

    [Fact]
    public async Task DuplicateViolatingStringsProduceDeduplicatedReasons()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"a":"https://evil1.example/x","b":"https://evil2.example/y"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.HostNotAllowed], decision.Reasons);
    }

    [Theory]
    [InlineData("010.0.0.5")]
    [InlineData("0xa.0.0.5")]
    [InlineData("127")]
    public async Task NonCanonicalIpV4StringsAreNotTreatedAsHostCandidates(string value)
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["ip"] = value }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Fact]
    public async Task MailtoUrlIsNotTreatedAsHostCandidate()
    {
        var resolver = new StubHostResolver().WithFailingHost("internal.svc");
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["address"] = "mailto:user@internal.svc" }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task TrailingDotInUrlHostIsStrippedForAllowlistMatch()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: ["api.github.com"], blockPrivateNetworks: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"url":"https://api.github.com./repos/foo/bar"}}""");

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
    }

    [Fact]
    public async Task BareBracketedIpV6WithPortIsClassified()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["endpoint"] = "[::1]:8080" }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PrivateNetworkTarget, decision.Reasons);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task PublicIpLiteralWithBlockingEnabledIsAllowed()
    {
        var resolver = new StubHostResolver();
        var evaluator = new HostArgumentRuleEvaluator(resolver);
        var hosts = CreateHosts(allow: [], blockPrivateNetworks: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["ip"] = "8.8.8.8" }
        };

        var decision = await evaluator.EvaluateAsync(ProxyWardMode.Enforce, hosts, toolCallParams, CancellationToken.None);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Equal(0, resolver.CallCount);
    }

    private static HostArgumentPolicy CreateHosts(
        IReadOnlyCollection<string> allow,
        bool blockPrivateNetworks) =>
        new(allow, blockPrivateNetworks);

    private sealed class StubHostResolver : IHostResolver
    {
        private readonly Dictionary<string, IPAddress[]> _hosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failures = new(StringComparer.OrdinalIgnoreCase);
        private int _callCount;

        public int CallCount => _callCount;

        public StubHostResolver WithHost(string host, params IPAddress[] addresses)
        {
            _hosts[host] = addresses;
            return this;
        }

        public StubHostResolver WithFailingHost(string host)
        {
            _failures.Add(host);
            return this;
        }

        public ValueTask<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_failures.Contains(host))
            {
                return new ValueTask<HostResolution>(new HostResolution([], ResolutionFailed: true));
            }
            if (_hosts.TryGetValue(host, out var addresses))
            {
                return new ValueTask<HostResolution>(new HostResolution(addresses, ResolutionFailed: false));
            }
            return new ValueTask<HostResolution>(new HostResolution([], ResolutionFailed: true));
        }
    }
}

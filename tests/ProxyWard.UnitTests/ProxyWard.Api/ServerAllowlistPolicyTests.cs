using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class ServerAllowlistPolicyTests
{
    [Fact]
    public void AllowedServerReturnsAllowDecision()
    {
        var evaluator = new ServerAllowlistPolicyEvaluator();

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, CreateServer(allowed: true));

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void DisallowedServerInEnforceModeReturnsBlockDecision()
    {
        var evaluator = new ServerAllowlistPolicyEvaluator();

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, CreateServer(allowed: false));

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ServerNotAllowed, decision.Reasons);
    }

    [Fact]
    public void DisallowedServerInAuditModeReturnsWouldBlockDecision()
    {
        var evaluator = new ServerAllowlistPolicyEvaluator();

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, CreateServer(allowed: false));

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.ServerNotAllowed, decision.Reasons);
    }

    private static ServerPolicy CreateServer(bool allowed) =>
        new(
            "github",
            "/github/mcp",
            new Uri("http://localhost:8080/mcp"),
            allowed,
            new SecretsPolicy(RedactInLogs: true, BlockReturn: false, Patterns: []),
            new ToolPolicy(ToolDefaultMode.Deny, [], [], []),
            new ArgumentPolicy(
                new PathArgumentPolicy([], BlockTraversal: true),
                new HostArgumentPolicy([], BlockPrivateNetworks: true),
                new CommandArgumentPolicy(BlockShell: true, []),
                new Dictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal)));
}

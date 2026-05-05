using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class PolicyDecisionExtensionsTests
{
    [Fact]
    public void EnforceModeProducesBlockDecisionWithReason()
    {
        var decision = ProxyWardMode.Enforce.AsBlockDecision(PolicyReasonCodes.ToolBlocked);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void AuditModeProducesWouldBlockDecisionWithReason()
    {
        var decision = ProxyWardMode.Audit.AsBlockDecision(PolicyReasonCodes.ServerNotAllowed);

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.ServerNotAllowed, decision.Reasons);
    }

    [Fact]
    public void OriginalReasonCodeIsPreservedAcrossModes()
    {
        var enforceDecision = ProxyWardMode.Enforce.AsBlockDecision(PolicyReasonCodes.ToolNotAllowed);
        var auditDecision = ProxyWardMode.Audit.AsBlockDecision(PolicyReasonCodes.ToolNotAllowed);

        Assert.Single(enforceDecision.Reasons, PolicyReasonCodes.ToolNotAllowed);
        Assert.Single(auditDecision.Reasons, PolicyReasonCodes.ToolNotAllowed);
    }
}

using ProxyWard.Audit;
using ProxyWard.Core.Policies;
using ProxyWard.Policy;

namespace ProxyWard.UnitTests;

public class ArchitectureBoundaryTests
{
    [Fact]
    public void PolicyProjectReferencesCoreWithoutAspNetCoreOrYarp()
    {
        var references = typeof(ProxyWardPolicyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Contains("ProxyWard.Core", references);
        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Microsoft.AspNetCore"));
        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Yarp"));
    }

    [Fact]
    public void AuditProjectDoesNotReferenceAspNetCoreOrYarp()
    {
        var references = typeof(ProxyWardAuditMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Microsoft.AspNetCore"));
        Assert.DoesNotContain(references, name => name is not null && name.StartsWith("Yarp"));
    }

    [Fact]
    public void PolicyDecisionCanRepresentBlockReason()
    {
        var decision = PolicyDecision.Block(PolicyReasonCodes.ToolBlocked);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }
}

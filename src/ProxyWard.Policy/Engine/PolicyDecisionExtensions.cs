using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public static class PolicyDecisionExtensions
{
    public static PolicyDecision AsBlockDecision(this ProxyWardMode mode, string reason) =>
        mode == ProxyWardMode.Enforce
            ? PolicyDecision.Block(reason)
            : PolicyDecision.WouldBlock(reason);

    public static PolicyDecision AsBlockDecision(this ProxyWardMode mode, IReadOnlyCollection<string> reasons)
    {
        var reasonArray = reasons as string[] ?? reasons.ToArray();
        return mode == ProxyWardMode.Enforce
            ? PolicyDecision.Block(reasonArray)
            : PolicyDecision.WouldBlock(reasonArray);
    }
}

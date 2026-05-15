using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class ServerAllowlistPolicyEvaluator
{
    public PolicyDecision Evaluate(ProxyWardMode mode, ServerPolicy server) =>
        server.Allowed
            ? PolicyDecision.Allow()
            : mode.AsBlockDecision(PolicyReasonCodes.ServerNotAllowed);
}

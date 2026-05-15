using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy;

public sealed class ProxyWardPolicyMarker
{
    public static PolicyDecision DefaultDecision => PolicyDecision.Allow();

    public static Type PolicyType => typeof(ProxyWardPolicy);
}

using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Runtime;

public interface IProxyWardPolicyProvider
{
    ProxyWardPolicy Current { get; }

    void Replace(ProxyWardPolicy policy);
}

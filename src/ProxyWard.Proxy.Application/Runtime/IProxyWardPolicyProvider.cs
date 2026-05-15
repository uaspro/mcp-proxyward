using ProxyWard.Policy.Configuration;

namespace ProxyWard.Proxy.Application.Runtime;

public interface IProxyWardPolicyProvider
{
    ProxyWardPolicy Current { get; }

    void Replace(ProxyWardPolicy policy);
}

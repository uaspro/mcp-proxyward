using ProxyWard.Policy.Configuration;

namespace ProxyWard.Proxy.Application.Runtime;

public sealed class InMemoryProxyWardPolicyProvider : IProxyWardPolicyProvider
{
    private ProxyWardPolicy _current;

    public InMemoryProxyWardPolicyProvider(ProxyWardPolicy initialPolicy)
    {
        ArgumentNullException.ThrowIfNull(initialPolicy);
        _current = initialPolicy;
    }

    public ProxyWardPolicy Current => Volatile.Read(ref _current);

    public void Replace(ProxyWardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Interlocked.Exchange(ref _current, policy);
    }
}

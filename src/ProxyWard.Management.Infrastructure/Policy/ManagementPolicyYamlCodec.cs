using ProxyWard.Management.Application.Policy;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class ManagementPolicyYamlCodec : IManagementPolicyYamlCodec
{
    public string RemovedLockfileMessage => ProxyWardPolicyLoader.RemovedLockfileMessage;

    public string CreateDefaultYaml() => ProxyWardDefaultPolicy.CreateYaml();

    public ProxyWardPolicy Load(string yaml) => ProxyWardPolicyLoader.Load(yaml);

    public ProxyWardPolicy WithMode(ProxyWardPolicy policy, ProxyWardMode mode) =>
        ProxyWardPolicyLoader.WithMode(policy, mode);

    public string ToYaml(ProxyWardPolicy policy) => ProxyWardPolicySerializer.ToYaml(policy);
}

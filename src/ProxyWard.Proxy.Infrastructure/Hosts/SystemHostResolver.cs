using System.Net;
using System.Net.Sockets;
using ProxyWard.Policy.Engine;

namespace ProxyWard.Proxy.Infrastructure.Hosts;

public sealed class SystemHostResolver : IHostResolver
{
    public async ValueTask<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return new HostResolution(addresses, ResolutionFailed: false);
        }
        catch (SocketException)
        {
            return new HostResolution([], ResolutionFailed: true);
        }
        catch (ArgumentException)
        {
            return new HostResolution([], ResolutionFailed: true);
        }
    }
}

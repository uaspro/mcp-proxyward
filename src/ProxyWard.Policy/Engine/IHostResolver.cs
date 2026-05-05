using System.Net;

namespace ProxyWard.Policy.Engine;

public interface IHostResolver
{
    ValueTask<HostResolution> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed record HostResolution(IReadOnlyList<IPAddress> Addresses, bool ResolutionFailed);

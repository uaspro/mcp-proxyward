using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Proxy.Infrastructure.Yarp;

public interface IProxyWardYarpConfigProvider : IProxyConfigProvider
{
    int Version { get; }

    int RouteCount { get; }

    int ClusterCount { get; }

    void Replace(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters);
}

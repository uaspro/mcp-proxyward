using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Proxy.Infrastructure.Yarp;

public sealed class DynamicProxyWardYarpConfigProvider : IProxyWardYarpConfigProvider
{
    private readonly object _gate = new();
    private readonly InMemoryConfigProvider _inner;
    private IReadOnlyList<RouteConfig> _routes;
    private IReadOnlyList<ClusterConfig> _clusters;
    private int _version = 1;

    public DynamicProxyWardYarpConfigProvider(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);

        _routes = routes.ToArray();
        _clusters = clusters.ToArray();
        _inner = new InMemoryConfigProvider(_routes, _clusters, FormatRevision(_version));
    }

    public int Version => Volatile.Read(ref _version);

    public int RouteCount => _routes.Count;

    public int ClusterCount => _clusters.Count;

    public IProxyConfig GetConfig() => _inner.GetConfig();

    public void Replace(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);

        var nextRoutes = routes.ToArray();
        var nextClusters = clusters.ToArray();

        lock (_gate)
        {
            var nextVersion = checked(_version + 1);
            _inner.Update(nextRoutes, nextClusters, FormatRevision(nextVersion));
            _routes = nextRoutes;
            _clusters = nextClusters;
            Volatile.Write(ref _version, nextVersion);
        }
    }

    private static string FormatRevision(int version) => version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

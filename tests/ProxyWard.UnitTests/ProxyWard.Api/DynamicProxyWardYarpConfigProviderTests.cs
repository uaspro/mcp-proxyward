using ProxyWard.Proxy.Infrastructure.Yarp;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.UnitTests;

public class DynamicProxyWardYarpConfigProviderTests
{
    [Fact]
    public void ReplaceIncrementsVersionAndLeavesCapturedSnapshotStable()
    {
        var provider = new DynamicProxyWardYarpConfigProvider(
            [CreateRoute("old-exact", "old", "/old/mcp")],
            [CreateCluster("old", "http://127.0.0.1:9001/")]);

        var captured = provider.GetConfig();

        provider.Replace(
            [CreateRoute("new-exact", "new", "/new/mcp")],
            [CreateCluster("new", "http://127.0.0.1:9002/")]);

        Assert.Equal(2, provider.Version);
        Assert.Equal(1, provider.RouteCount);
        Assert.Equal(1, provider.ClusterCount);
        Assert.Equal("old-exact", Assert.Single(captured.Routes).RouteId);
        Assert.True(captured.ChangeToken.HasChanged);
        Assert.Equal("new-exact", Assert.Single(provider.GetConfig().Routes).RouteId);
    }

    [Fact]
    public void ReplaceRejectsNullConfig()
    {
        var provider = new DynamicProxyWardYarpConfigProvider(
            [CreateRoute("old-exact", "old", "/old/mcp")],
            [CreateCluster("old", "http://127.0.0.1:9001/")]);

        Assert.Throws<ArgumentNullException>(() => provider.Replace(null!, []));
        Assert.Throws<ArgumentNullException>(() => provider.Replace([], null!));
    }

    private static RouteConfig CreateRoute(string routeId, string clusterId, string path) =>
        new()
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Match = new RouteMatch
            {
                Path = path
            }
        };

    private static ClusterConfig CreateCluster(string clusterId, string address) =>
        new()
        {
            ClusterId = clusterId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["primary"] = new()
                {
                    Address = address
                }
            }
        };
}

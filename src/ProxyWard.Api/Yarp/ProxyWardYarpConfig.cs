using ProxyWard.Policy.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Yarp;

public static class ProxyWardYarpConfig
{
    public static IReadOnlyList<RouteConfig> CreateRoutes(ProxyWardPolicy policy) =>
        policy.Servers.Values
            .SelectMany(CreateRoutes)
            .ToArray();

    public static IReadOnlyList<ClusterConfig> CreateClusters(ProxyWardPolicy policy) =>
        policy.Servers.Values
            .Select(CreateCluster)
            .ToArray();

    private static IEnumerable<RouteConfig> CreateRoutes(ServerPolicy server)
    {
        var routePrefix = NormalizeRoutePrefix(server.Route);
        var transforms = CreatePathTransforms(routePrefix, server.Upstream.AbsolutePath);

        yield return new RouteConfig
        {
            RouteId = $"{server.Id}-exact",
            ClusterId = server.Id,
            Order = 0,
            Match = new RouteMatch
            {
                Path = routePrefix
            },
            Transforms = transforms
        };

        yield return new RouteConfig
        {
            RouteId = $"{server.Id}-catch-all",
            ClusterId = server.Id,
            Order = 1,
            Match = new RouteMatch
            {
                Path = $"{routePrefix}/{{**catchAll}}"
            },
            Transforms = transforms
        };
    }

    private static ClusterConfig CreateCluster(ServerPolicy server) =>
        new()
        {
            ClusterId = server.Id,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["primary"] = new()
                {
                    Address = CreateDestinationAddress(server.Upstream)
                }
            }
        };

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreatePathTransforms(
        string routePrefix,
        string upstreamPath)
    {
        var transforms = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PathRemovePrefix"] = routePrefix
            }
        };

        if (!string.IsNullOrWhiteSpace(upstreamPath) && upstreamPath != "/")
        {
            transforms.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PathPrefix"] = upstreamPath.TrimEnd('/')
            });
        }

        return transforms;
    }

    private static string CreateDestinationAddress(Uri upstream)
    {
        var builder = new UriBuilder(upstream)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeRoutePrefix(string route)
    {
        var normalized = route.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }
}

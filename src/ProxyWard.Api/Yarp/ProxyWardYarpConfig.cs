using ProxyWard.Policy.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Yarp;

public static class ProxyWardYarpConfig
{
    private static readonly string[] WellKnownMetadataPrefixes =
    [
        "/.well-known/oauth-protected-resource",
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration"
    ];

    public static IReadOnlyList<RouteConfig> CreateRoutes(ProxyWardPolicy policy) =>
        CreateRoutes(policy.Servers.Values)
            .ToArray();

    private static IEnumerable<RouteConfig> CreateRoutes(IEnumerable<ServerPolicy> servers)
    {
        var serverList = servers.ToArray();

        foreach (var route in serverList.SelectMany(CreateRoutes))
        {
            yield return route;
        }

        if (serverList.Length == 1)
        {
            foreach (var route in CreateOriginWellKnownRoutes(serverList[0]))
            {
                yield return route;
            }
        }
    }

    public static IReadOnlyList<ClusterConfig> CreateClusters(ProxyWardPolicy policy) =>
        policy.Servers.Values
            .Select(CreateCluster)
            .ToArray();

    private static IEnumerable<RouteConfig> CreateRoutes(ServerPolicy server)
    {
        foreach (var route in CreateMcpRoutes(server))
        {
            yield return route;
        }

        foreach (var route in CreateRouteScopedWellKnownRoutes(server))
        {
            yield return route;
        }
    }

    private static IEnumerable<RouteConfig> CreateMcpRoutes(ServerPolicy server)
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

    private static IEnumerable<RouteConfig> CreateRouteScopedWellKnownRoutes(ServerPolicy server) =>
        WellKnownMetadataPrefixes.Select((metadataPrefix, index) =>
            CreateWellKnownRoute(
                server,
                routeId: $"{server.Id}-well-known-{index}",
                sourcePrefix: $"{metadataPrefix}{NormalizeRoutePrefix(server.Route)}",
                metadataPrefix,
                order: -100 + index));

    private static IEnumerable<RouteConfig> CreateOriginWellKnownRoutes(ServerPolicy server) =>
        WellKnownMetadataPrefixes.Select((metadataPrefix, index) =>
            CreateWellKnownRoute(
                server,
                routeId: $"{server.Id}-origin-well-known-{index}",
                sourcePrefix: metadataPrefix,
                metadataPrefix,
                order: -200 + index));

    private static RouteConfig CreateWellKnownRoute(
        ServerPolicy server,
        string routeId,
        string sourcePrefix,
        string metadataPrefix,
        int order) =>
        new()
        {
            RouteId = routeId,
            ClusterId = server.Id,
            Order = order,
            Match = new RouteMatch
            {
                Path = sourcePrefix
            },
            Transforms = CreatePathTransforms(
                sourcePrefix,
                JoinPath(metadataPrefix, server.Upstream.AbsolutePath))
        };

    private static string JoinPath(string prefix, string suffix)
    {
        var normalizedPrefix = prefix.TrimEnd('/');
        var normalizedSuffix = NormalizeRoutePrefix(suffix);
        return normalizedSuffix == "/"
            ? normalizedPrefix
            : $"{normalizedPrefix}{normalizedSuffix}";
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

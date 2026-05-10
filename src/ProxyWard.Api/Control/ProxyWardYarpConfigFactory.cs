using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Control;

public sealed class ProxyWardYarpConfigFactory
{
    public ProxyWardYarpConfigBuildResult Build(YarpConfigApplyRequest? request)
    {
        if (request is null)
        {
            return ProxyWardYarpConfigBuildResult.Failed(["request body is required"]);
        }

        var validationErrors = new List<string>();
        var clusterResult = BuildClusters(request.Clusters, validationErrors);
        var routeResult = BuildRoutes(request.Routes, clusterResult.ClusterIds, validationErrors);

        return validationErrors.Count == 0
            ? ProxyWardYarpConfigBuildResult.Successful(routeResult, clusterResult.Clusters)
            : ProxyWardYarpConfigBuildResult.Failed(validationErrors);
    }

    private static YarpClusterBuildResult BuildClusters(
        IReadOnlyCollection<YarpClusterApplyRequest>? requestedClusters,
        List<string> validationErrors)
    {
        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
        var clusters = new List<ClusterConfig>();

        foreach (var cluster in requestedClusters ?? [])
        {
            if (string.IsNullOrWhiteSpace(cluster.ClusterId))
            {
                validationErrors.Add("clusters[].clusterId is required");
                continue;
            }

            var clusterId = cluster.ClusterId.Trim();
            if (!clusterIds.Add(clusterId))
            {
                validationErrors.Add($"clusters.{clusterId} is duplicated");
                continue;
            }

            var destinations = BuildDestinations(clusterId, cluster.Destinations, validationErrors);
            if (destinations.Count > 0)
            {
                clusters.Add(new ClusterConfig
                {
                    ClusterId = clusterId,
                    Destinations = destinations
                });
            }
        }

        return new YarpClusterBuildResult(clusters, clusterIds);
    }

    private static IReadOnlyList<RouteConfig> BuildRoutes(
        IReadOnlyCollection<YarpRouteApplyRequest>? requestedRoutes,
        IReadOnlySet<string> clusterIds,
        List<string> validationErrors)
    {
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var routes = new List<RouteConfig>();

        foreach (var route in requestedRoutes ?? [])
        {
            if (!TryCreateRoute(route, clusterIds, routeIds, validationErrors, out var routeConfig))
            {
                continue;
            }

            routes.Add(routeConfig);
        }

        return routes;
    }

    private static bool TryCreateRoute(
        YarpRouteApplyRequest route,
        IReadOnlySet<string> clusterIds,
        HashSet<string> routeIds,
        List<string> validationErrors,
        out RouteConfig routeConfig)
    {
        routeConfig = new RouteConfig();

        if (string.IsNullOrWhiteSpace(route.RouteId))
        {
            validationErrors.Add("routes[].routeId is required");
            return false;
        }

        var routeId = route.RouteId.Trim();
        if (!routeIds.Add(routeId))
        {
            validationErrors.Add($"routes.{routeId} is duplicated");
            return false;
        }

        if (string.IsNullOrWhiteSpace(route.ClusterId))
        {
            validationErrors.Add($"routes.{routeId}.clusterId is required");
            return false;
        }

        var clusterId = route.ClusterId.Trim();
        if (!clusterIds.Contains(clusterId))
        {
            validationErrors.Add($"routes.{routeId}.clusterId '{clusterId}' does not reference a configured cluster");
            return false;
        }

        if (string.IsNullOrWhiteSpace(route.Match?.Path) || !IsSupportedRoutePath(route.Match.Path))
        {
            validationErrors.Add($"routes.{routeId}.match.path must be an absolute path, optionally ending with '/{{**catchAll}}'");
            return false;
        }

        routeConfig = new RouteConfig
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Order = route.Order,
            Match = new RouteMatch
            {
                Path = route.Match.Path
            },
            Transforms = BuildTransforms(routeId, route.Transforms, validationErrors)
        };
        return true;
    }

    private static Dictionary<string, DestinationConfig> BuildDestinations(
        string clusterId,
        Dictionary<string, YarpDestinationApplyRequest>? requestedDestinations,
        List<string> validationErrors)
    {
        var destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal);
        if (requestedDestinations is null || requestedDestinations.Count == 0)
        {
            validationErrors.Add($"clusters.{clusterId}.destinations must contain at least one destination");
            return destinations;
        }

        foreach (var (destinationId, destination) in requestedDestinations)
        {
            if (string.IsNullOrWhiteSpace(destinationId))
            {
                validationErrors.Add($"clusters.{clusterId}.destinations contains an empty destination id");
                continue;
            }

            if (destination is null)
            {
                validationErrors.Add($"clusters.{clusterId}.destinations.{destinationId} section is required");
                continue;
            }

            if (!IsHttpDestination(destination.Address))
            {
                validationErrors.Add($"clusters.{clusterId}.destinations.{destinationId}.address must be an absolute http or https URL");
                continue;
            }

            destinations[destinationId] = new DestinationConfig
            {
                Address = destination.Address!
            };
        }

        return destinations;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>>? BuildTransforms(
        string routeId,
        IReadOnlyList<Dictionary<string, string>>? requestedTransforms,
        List<string> validationErrors)
    {
        if (requestedTransforms is null)
        {
            return null;
        }

        var transforms = new List<IReadOnlyDictionary<string, string>>();
        for (var index = 0; index < requestedTransforms.Count; index++)
        {
            var requestedTransform = requestedTransforms[index];
            if (requestedTransform is null)
            {
                validationErrors.Add($"routes.{routeId}.transforms[{index}] section is required");
                continue;
            }

            if (requestedTransform.Count == 0)
            {
                validationErrors.Add($"routes.{routeId}.transforms[{index}] must contain at least one transform property");
                continue;
            }

            transforms.Add(BuildTransform(routeId, index, requestedTransform, validationErrors));
        }

        return transforms;
    }

    private static Dictionary<string, string> BuildTransform(
        string routeId,
        int index,
        Dictionary<string, string> requestedTransform,
        List<string> validationErrors)
    {
        var transform = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in requestedTransform)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                validationErrors.Add($"routes.{routeId}.transforms[{index}] contains an empty key or value");
                continue;
            }

            if (!IsSupportedTransform(key, value))
            {
                validationErrors.Add($"routes.{routeId}.transforms[{index}] contains unsupported transform '{key}'");
                continue;
            }

            transform[key] = value;
        }

        return transform;
    }

    private static bool IsHttpDestination(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && Uri.TryCreate(address, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSupportedRoutePath(string path)
    {
        const string catchAllSuffix = "/{**catchAll}";
        if (!path.StartsWith('/'))
        {
            return false;
        }

        if (!path.Contains('{') && !path.Contains('}'))
        {
            return true;
        }

        return path.EndsWith(catchAllSuffix, StringComparison.Ordinal)
            && path.Count(ch => ch == '{') == 1
            && path.Count(ch => ch == '}') == 1;
    }

    private static bool IsSupportedTransform(string key, string value) =>
        (key.Equals("PathRemovePrefix", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PathPrefix", StringComparison.OrdinalIgnoreCase))
        && value.StartsWith('/');

    private sealed record YarpClusterBuildResult(
        IReadOnlyList<ClusterConfig> Clusters,
        IReadOnlySet<string> ClusterIds);
}

public sealed record ProxyWardYarpConfigBuildResult(
    bool IsValid,
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    IReadOnlyCollection<string> Errors)
{
    public static ProxyWardYarpConfigBuildResult Successful(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters) =>
        new(true, routes, clusters, []);

    public static ProxyWardYarpConfigBuildResult Failed(IReadOnlyCollection<string> errors) =>
        new(false, [], [], errors);
}

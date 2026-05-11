using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

internal static class ManagementPolicyYarpConfigFactory
{
    private static readonly string[] WellKnownMetadataPrefixes =
    [
        "/.well-known/oauth-protected-resource",
        "/.well-known/oauth-authorization-server",
        "/.well-known/openid-configuration"
    ];

    public static ProxyControlYarpConfigRequest Create(ProxyWardPolicy policy) =>
        new(
            Routes: CreateRoutes(policy.Servers.Values).ToArray(),
            Clusters: policy.Servers.Values
                .Select(CreateCluster)
                .ToArray());

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateRoutes(
        IEnumerable<ServerPolicy> servers)
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

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateRoutes(ServerPolicy server)
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

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateMcpRoutes(ServerPolicy server)
    {
        var routePrefix = NormalizeRoutePrefix(server.Route);
        var transforms = CreatePathTransforms(routePrefix, server.Upstream.AbsolutePath, server.Upstream.Query);

        yield return new ProxyControlYarpRouteRequest(
            RouteId: $"{server.Id}-exact",
            ClusterId: server.Id,
            Order: 0,
            Match: new ProxyControlYarpRouteMatchRequest(routePrefix),
            Transforms: transforms);

        yield return new ProxyControlYarpRouteRequest(
            RouteId: $"{server.Id}-catch-all",
            ClusterId: server.Id,
            Order: 1,
            Match: new ProxyControlYarpRouteMatchRequest($"{routePrefix}/{{**catchAll}}"),
            Transforms: transforms);
    }

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateRouteScopedWellKnownRoutes(ServerPolicy server) =>
        WellKnownMetadataPrefixes.Select((metadataPrefix, index) =>
            CreateWellKnownRoute(
                server,
                routeId: $"{server.Id}-well-known-{index}",
                sourcePrefix: $"{metadataPrefix}{NormalizeRoutePrefix(server.Route)}",
                metadataPrefix,
                order: -100 + index));

    private static IEnumerable<ProxyControlYarpRouteRequest> CreateOriginWellKnownRoutes(ServerPolicy server) =>
        WellKnownMetadataPrefixes.Select((metadataPrefix, index) =>
            CreateWellKnownRoute(
                server,
                routeId: $"{server.Id}-origin-well-known-{index}",
                sourcePrefix: metadataPrefix,
                metadataPrefix,
                order: -200 + index));

    private static ProxyControlYarpRouteRequest CreateWellKnownRoute(
        ServerPolicy server,
        string routeId,
        string sourcePrefix,
        string metadataPrefix,
        int order) =>
        new(
            RouteId: routeId,
            ClusterId: server.Id,
            Order: order,
            Match: new ProxyControlYarpRouteMatchRequest(sourcePrefix),
            Transforms: CreatePathTransforms(
                sourcePrefix,
                JoinPath(metadataPrefix, server.Upstream.AbsolutePath)));

    private static string JoinPath(string prefix, string suffix)
    {
        var normalizedPrefix = prefix.TrimEnd('/');
        var normalizedSuffix = NormalizeRoutePrefix(suffix);
        return normalizedSuffix == "/"
            ? normalizedPrefix
            : $"{normalizedPrefix}{normalizedSuffix}";
    }

    private static ProxyControlYarpClusterRequest CreateCluster(ServerPolicy server) =>
        new(
            ClusterId: server.Id,
            Destinations: new Dictionary<string, ProxyControlYarpDestinationRequest>(StringComparer.Ordinal)
            {
                ["primary"] = new(CreateDestinationAddress(server.Upstream))
            });

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> CreatePathTransforms(
        string routePrefix,
        string upstreamPath,
        string upstreamQuery = "")
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

        foreach (var (name, value) in ParseQueryParameters(upstreamQuery))
        {
            transforms.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["QueryValueParameter"] = name,
                ["Set"] = value
            });
        }

        return transforms;
    }

    private static IEnumerable<(string Name, string Value)> ParseQueryParameters(string query)
    {
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrEmpty(trimmed))
        {
            yield break;
        }

        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            var name = separatorIndex < 0 ? segment : segment[..separatorIndex];
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = separatorIndex < 0 ? string.Empty : segment[(separatorIndex + 1)..];
            yield return (DecodeQueryValue(name), DecodeQueryValue(value));
        }
    }

    private static string DecodeQueryValue(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

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

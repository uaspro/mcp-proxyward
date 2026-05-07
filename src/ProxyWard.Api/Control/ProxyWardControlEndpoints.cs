using System.Security.Cryptography;
using System.Text;
using ProxyWard.Api.Runtime;
using ProxyWard.Api.Yarp;
using ProxyWard.Policy.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Control;

public static class ProxyWardControlEndpoints
{
    public static void MapProxyWardControlEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<ProxyWardControlOptions>();
        if (!options.Enabled)
        {
            return;
        }

        var group = app.MapGroup("/control");

        group.MapGet("/status", (
            HttpContext context,
            IProxyWardPolicyProvider policyProvider,
            IProxyWardYarpConfigProvider yarpConfigProvider,
            ProxyWardControlOptions controlOptions) =>
        {
            if (!IsAuthorized(context, controlOptions))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(CreateStatusResponse(policyProvider.Current, yarpConfigProvider.Version));
        });

        group.MapPut("/policy-snapshot", async (
            HttpContext context,
            IProxyWardPolicyProvider policyProvider,
            IProxyWardYarpConfigProvider yarpConfigProvider,
            ProxyWardControlOptions controlOptions) =>
        {
            if (!IsAuthorized(context, controlOptions))
            {
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var yaml = await reader.ReadToEndAsync(context.RequestAborted);
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return Results.BadRequest(new
                {
                    error = "policy_validation_failed",
                    errors = new[] { "policy snapshot body is required" }
                });
            }

            try
            {
                var replacement = ProxyWardPolicyLoader.Load(yaml);
                policyProvider.Replace(replacement);
                return Results.Ok(CreateStatusResponse(replacement, yarpConfigProvider.Version));
            }
            catch (PolicyValidationException ex)
            {
                return Results.BadRequest(new
                {
                    error = "policy_validation_failed",
                    errors = ex.Errors
                });
            }
        });

        group.MapPut("/yarp-config", async (
            HttpContext context,
            IProxyWardYarpConfigProvider yarpConfigProvider,
            ProxyWardControlOptions controlOptions) =>
        {
            if (!IsAuthorized(context, controlOptions))
            {
                return Results.Unauthorized();
            }

            YarpConfigApplyRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<YarpConfigApplyRequest>(
                    cancellationToken: context.RequestAborted);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return CreateYarpValidationError([$"JSON could not be parsed: {ex.Message}"]);
            }

            if (!TryBuildYarpConfig(request, out var routes, out var clusters, out var errors))
            {
                return CreateYarpValidationError(errors);
            }

            yarpConfigProvider.Replace(routes, clusters);
            return Results.Ok(CreateYarpStatusResponse(yarpConfigProvider));
        });
    }

    private static object CreateStatusResponse(ProxyWardPolicy policy, int routeVersion) => new
    {
        status = "healthy",
        service = "MCP ProxyWard",
        mode = policy.Mode.ToString().ToLowerInvariant(),
        policyVersion = policy.VersionHash,
        serverCount = policy.Servers.Count,
        routeVersion
    };

    private static object CreateYarpStatusResponse(IProxyWardYarpConfigProvider provider) => new
    {
        status = "accepted",
        routeVersion = provider.Version,
        routeCount = provider.RouteCount,
        clusterCount = provider.ClusterCount
    };

    private static IResult CreateYarpValidationError(IReadOnlyCollection<string> errors) =>
        Results.BadRequest(new
        {
            error = "yarp_config_validation_failed",
            errors
        });

    private static bool TryBuildYarpConfig(
        YarpConfigApplyRequest? request,
        out IReadOnlyList<RouteConfig> routes,
        out IReadOnlyList<ClusterConfig> clusters,
        out IReadOnlyCollection<string> errors)
    {
        var validationErrors = new List<string>();
        var routeConfigs = new List<RouteConfig>();
        var clusterConfigs = new List<ClusterConfig>();

        if (request is null)
        {
            routes = [];
            clusters = [];
            errors = ["request body is required"];
            return false;
        }

        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in request.Clusters ?? [])
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
                clusterConfigs.Add(new ClusterConfig
                {
                    ClusterId = clusterId,
                    Destinations = destinations
                });
            }
        }

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in request.Routes ?? [])
        {
            if (string.IsNullOrWhiteSpace(route.RouteId))
            {
                validationErrors.Add("routes[].routeId is required");
                continue;
            }

            var routeId = route.RouteId.Trim();
            if (!routeIds.Add(routeId))
            {
                validationErrors.Add($"routes.{routeId} is duplicated");
                continue;
            }

            if (string.IsNullOrWhiteSpace(route.ClusterId))
            {
                validationErrors.Add($"routes.{routeId}.clusterId is required");
                continue;
            }

            var clusterId = route.ClusterId.Trim();
            if (!clusterIds.Contains(clusterId))
            {
                validationErrors.Add($"routes.{routeId}.clusterId '{clusterId}' does not reference a configured cluster");
                continue;
            }

            if (string.IsNullOrWhiteSpace(route.Match?.Path) || !IsSupportedRoutePath(route.Match.Path))
            {
                validationErrors.Add($"routes.{routeId}.match.path must be an absolute path, optionally ending with '/{{**catchAll}}'");
                continue;
            }

            routeConfigs.Add(new RouteConfig
            {
                RouteId = routeId,
                ClusterId = clusterId,
                Order = route.Order,
                Match = new RouteMatch
                {
                    Path = route.Match.Path
                },
                Transforms = BuildTransforms(routeId, route.Transforms, validationErrors)
            });
        }

        routes = validationErrors.Count == 0 ? routeConfigs : [];
        clusters = validationErrors.Count == 0 ? clusterConfigs : [];
        errors = validationErrors;
        return validationErrors.Count == 0;
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

            if (string.IsNullOrWhiteSpace(destination.Address)
                || !Uri.TryCreate(destination.Address, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                validationErrors.Add($"clusters.{clusterId}.destinations.{destinationId}.address must be an absolute http or https URL");
                continue;
            }

            destinations[destinationId] = new DestinationConfig
            {
                Address = destination.Address
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

            transforms.Add(transform);
        }

        return transforms;
    }

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

    private static bool IsAuthorized(HttpContext context, ProxyWardControlOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            return false;
        }

        var headerValue = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedToken = headerValue[bearerPrefix.Length..].Trim();
        if (suppliedToken.Length == 0)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(options.Token);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);

        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private sealed class YarpConfigApplyRequest
    {
        public List<YarpRouteApplyRequest>? Routes { get; set; }

        public List<YarpClusterApplyRequest>? Clusters { get; set; }
    }

    private sealed class YarpRouteApplyRequest
    {
        public string? RouteId { get; set; }

        public string? ClusterId { get; set; }

        public int? Order { get; set; }

        public YarpRouteMatchApplyRequest? Match { get; set; }

        public List<Dictionary<string, string>>? Transforms { get; set; }
    }

    private sealed class YarpRouteMatchApplyRequest
    {
        public string? Path { get; set; }
    }

    private sealed class YarpClusterApplyRequest
    {
        public string? ClusterId { get; set; }

        public Dictionary<string, YarpDestinationApplyRequest>? Destinations { get; set; }
    }

    private sealed class YarpDestinationApplyRequest
    {
        public string? Address { get; set; }
    }
}

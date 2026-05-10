using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Primitives;
using ProxyWard.Api.Middleware;
using ProxyWard.Api.Runtime;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.OAuth;

public static class ProxyWardOAuthMetadataEndpoints
{
    private const string ProtectedResourceMetadataPrefix = "/.well-known/oauth-protected-resource";
    private const string WwwAuthenticateHeader = "WWW-Authenticate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapProxyWardOAuthMetadataEndpoints(this WebApplication app)
    {
        PreferOverReverseProxy(app.MapGet(
            ProtectedResourceMetadataPrefix,
            HandleRootProtectedResourceMetadataAsync));

        PreferOverReverseProxy(app.MapGet(
            $"{ProtectedResourceMetadataPrefix}/{{**routePath}}",
            HandleRouteProtectedResourceMetadataAsync));
    }

    public static void UseProxyWardOAuthChallengeMetadataRewrite(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                RewriteWwwAuthenticateMetadata(context);
                return Task.CompletedTask;
            });

            await next(context);
        });
    }

    private static async Task<IResult> HandleRootProtectedResourceMetadataAsync(
        HttpContext context,
        IProxyWardPolicyProvider policyProvider,
        IHttpClientFactory httpClientFactory)
    {
        var servers = policyProvider.Current.Servers.Values.ToArray();
        return servers.Length switch
        {
            0 => Results.NotFound(new
            {
                error = "No MCP proxy route configured for protected resource metadata."
            }),
            1 => await HandleProtectedResourceMetadataAsync(
                context,
                servers[0],
                httpClientFactory,
                context.RequestAborted),
            _ => Results.NotFound(new
            {
                error = "Route-scoped protected resource metadata is required when multiple MCP servers are configured.",
                metadataRoutes = servers
                    .OrderBy(server => server.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(server => CreatePublicProtectedResourceMetadataUri(context.Request, server))
                    .ToArray()
            })
        };
    }

    private static async Task<IResult> HandleRouteProtectedResourceMetadataAsync(
        string routePath,
        HttpContext context,
        IProxyWardPolicyProvider policyProvider,
        IHttpClientFactory httpClientFactory)
    {
        var server = ResolveServer(routePath, policyProvider.Current);
        if (server is null)
        {
            return Results.NotFound(new
            {
                error = "No MCP proxy route configured for this protected resource metadata request."
            });
        }

        return await HandleProtectedResourceMetadataAsync(
            context,
            server,
            httpClientFactory,
            context.RequestAborted);
    }

    private static async Task<IResult> HandleProtectedResourceMetadataAsync(
        HttpContext context,
        ServerPolicy server,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var upstreamMetadataUri = CreateUpstreamProtectedResourceMetadataUri(server, context.Request.QueryString);
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, upstreamMetadataUri);

        if (context.Request.Headers.TryGetValue("Accept", out var accept))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        }

        using var response = await httpClientFactory
            .CreateClient()
            .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (!response.IsSuccessStatusCode)
        {
            return Results.Text(
                content,
                contentType,
                Encoding.UTF8,
                statusCode: (int)response.StatusCode);
        }

        JsonNode? metadata;
        try
        {
            metadata = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return Results.Text(
                content,
                contentType,
                Encoding.UTF8,
                statusCode: (int)response.StatusCode);
        }

        if (metadata is JsonObject metadataObject)
        {
            metadataObject["resource"] = CreatePublicResourceUri(context.Request, server);
        }

        return Results.Text(
            metadata?.ToJsonString(JsonOptions) ?? "{}",
            "application/json",
            Encoding.UTF8,
            statusCode: (int)response.StatusCode);
    }

    private static void RewriteWwwAuthenticateMetadata(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServerResolutionItems.ServerPolicy, out var serverItem)
            || serverItem is not ServerPolicy server
            || !context.Response.Headers.TryGetValue(WwwAuthenticateHeader, out var values)
            || values.Count == 0)
        {
            return;
        }

        var upstreamMetadataUri = CreateUpstreamProtectedResourceMetadataUri(server, QueryString.Empty).AbsoluteUri;
        var publicMetadataUri = CreatePublicProtectedResourceMetadataUri(context.Request, server);
        var rewrittenValues = values
            .Select(value => RewriteMetadataUri(value ?? string.Empty, upstreamMetadataUri, publicMetadataUri))
            .ToArray();

        context.Response.Headers[WwwAuthenticateHeader] = new StringValues(rewrittenValues);
    }

    private static string RewriteMetadataUri(string value, string upstreamMetadataUri, string publicMetadataUri)
    {
        var rewritten = value.Replace(
            upstreamMetadataUri,
            publicMetadataUri,
            StringComparison.OrdinalIgnoreCase);

        return rewritten.Replace(
            Uri.EscapeDataString(upstreamMetadataUri),
            Uri.EscapeDataString(publicMetadataUri),
            StringComparison.OrdinalIgnoreCase);
    }

    private static ServerPolicy? ResolveServer(string routePath, ProxyWardPolicy policy)
    {
        var metadataRoutePath = NormalizeRoutePrefix($"/{routePath}");
        return policy.Servers.Values.FirstOrDefault(server =>
            string.Equals(
                NormalizeRoutePrefix(server.Route),
                metadataRoutePath,
                StringComparison.OrdinalIgnoreCase));
    }

    private static Uri CreateUpstreamProtectedResourceMetadataUri(ServerPolicy server, QueryString queryString)
    {
        var builder = new UriBuilder(server.Upstream)
        {
            Path = JoinPath(ProtectedResourceMetadataPrefix, server.Upstream.AbsolutePath),
            Query = queryString.HasValue ? queryString.Value!.TrimStart('?') : string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static string CreatePublicProtectedResourceMetadataUri(HttpRequest request, ServerPolicy server) =>
        $"{CreatePublicOrigin(request)}{ProtectedResourceMetadataPrefix}{NormalizeRoutePrefix(server.Route)}";

    private static string CreatePublicResourceUri(HttpRequest request, ServerPolicy server) =>
        $"{CreatePublicOrigin(request)}{NormalizeRoutePrefix(server.Route)}";

    private static string CreatePublicOrigin(HttpRequest request)
    {
        var scheme = ResolveHeaderValue(request.Headers, "X-Forwarded-Proto") ?? request.Scheme;
        var host = ResolveHeaderValue(request.Headers, "X-Forwarded-Host") ?? request.Host.Value;
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;

        return $"{scheme}://{host}{pathBase}";
    }

    private static string? ResolveHeaderValue(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;

    private static RouteHandlerBuilder PreferOverReverseProxy(RouteHandlerBuilder builder)
    {
        builder.WithOrder(-1_000);
        return builder;
    }

    private static string JoinPath(string prefix, string suffix)
    {
        var normalizedPrefix = prefix.TrimEnd('/');
        var normalizedSuffix = NormalizeRoutePrefix(suffix);
        return normalizedSuffix == "/"
            ? normalizedPrefix
            : $"{normalizedPrefix}{normalizedSuffix}";
    }

    private static string NormalizeRoutePrefix(string route)
    {
        var normalized = route.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }
}

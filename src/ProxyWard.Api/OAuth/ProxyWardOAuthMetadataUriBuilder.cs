using Microsoft.Extensions.Primitives;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.OAuth;

internal static class ProxyWardOAuthMetadataUriBuilder
{
    public const string ProtectedResourceMetadataPrefix = "/.well-known/oauth-protected-resource";

    public static Uri CreateUpstreamProtectedResourceMetadataUri(ServerPolicy server, QueryString queryString)
    {
        var builder = new UriBuilder(server.Upstream)
        {
            Path = JoinPath(ProtectedResourceMetadataPrefix, server.Upstream.AbsolutePath),
            Query = queryString.HasValue ? queryString.Value!.TrimStart('?') : string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    public static string CreatePublicProtectedResourceMetadataUri(HttpRequest request, ServerPolicy server) =>
        $"{CreatePublicOrigin(request)}{ProtectedResourceMetadataPrefix}{NormalizeRoutePrefix(server.Route)}";

    public static string CreatePublicResourceUri(HttpRequest request, ServerPolicy server) =>
        $"{CreatePublicOrigin(request)}{NormalizeRoutePrefix(server.Route)}";

    public static string NormalizeRoutePrefix(string route)
    {
        var normalized = route.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }

    public static string? ResolveHeaderValue(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;

    public static string RewriteMetadataUri(string value, string upstreamMetadataUri, string publicMetadataUri)
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

    private static string CreatePublicOrigin(HttpRequest request)
    {
        var scheme = ResolveHeaderValue(request.Headers, "X-Forwarded-Proto") ?? request.Scheme;
        var host = ResolveHeaderValue(request.Headers, "X-Forwarded-Host") ?? request.Host.Value;
        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;

        return $"{scheme}://{host}{pathBase}";
    }

    private static string JoinPath(string prefix, string suffix)
    {
        var normalizedPrefix = prefix.TrimEnd('/');
        var normalizedSuffix = NormalizeRoutePrefix(suffix);
        return normalizedSuffix == "/"
            ? normalizedPrefix
            : $"{normalizedPrefix}{normalizedSuffix}";
    }
}

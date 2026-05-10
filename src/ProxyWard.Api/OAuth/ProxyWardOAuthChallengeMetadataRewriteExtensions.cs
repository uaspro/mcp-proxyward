using Microsoft.Extensions.Primitives;
using ProxyWard.Api.Middleware;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.OAuth;

public static class ProxyWardOAuthChallengeMetadataRewriteExtensions
{
    private const string WwwAuthenticateHeader = "WWW-Authenticate";

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

    private static void RewriteWwwAuthenticateMetadata(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServerResolutionItems.ServerPolicy, out var serverItem)
            || serverItem is not ServerPolicy server
            || !context.Response.Headers.TryGetValue(WwwAuthenticateHeader, out var values)
            || values.Count == 0)
        {
            return;
        }

        var upstreamMetadataUri = ProxyWardOAuthMetadataUriBuilder
            .CreateUpstreamProtectedResourceMetadataUri(server, QueryString.Empty)
            .AbsoluteUri;
        var publicMetadataUri = ProxyWardOAuthMetadataUriBuilder.CreatePublicProtectedResourceMetadataUri(
            context.Request,
            server);
        var rewrittenValues = values
            .Select(value => ProxyWardOAuthMetadataUriBuilder.RewriteMetadataUri(
                value ?? string.Empty,
                upstreamMetadataUri,
                publicMetadataUri))
            .ToArray();

        context.Response.Headers[WwwAuthenticateHeader] = new StringValues(rewrittenValues);
    }
}

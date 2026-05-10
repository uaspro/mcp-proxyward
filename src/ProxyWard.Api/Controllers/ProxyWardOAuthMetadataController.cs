using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using ProxyWard.Api.OAuth;
using ProxyWard.Api.Runtime;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Controllers;

[ApiController]
public sealed class ProxyWardOAuthMetadataController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet(ProxyWardOAuthMetadataUriBuilder.ProtectedResourceMetadataPrefix, Order = -1_000)]
    public async Task<IActionResult> GetRootProtectedResourceMetadata(
        [FromServices] IProxyWardPolicyProvider policyProvider,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var servers = policyProvider.Current.Servers.Values.ToArray();
        return servers.Length switch
        {
            0 => NotFound(new
            {
                error = "No MCP proxy route configured for protected resource metadata."
            }),
            1 => await HandleProtectedResourceMetadataAsync(
                servers[0],
                httpClientFactory,
                cancellationToken).ConfigureAwait(false),
            _ => NotFound(new
            {
                error = "Route-scoped protected resource metadata is required when multiple MCP servers are configured.",
                metadataRoutes = servers
                    .OrderBy(server => server.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(server => ProxyWardOAuthMetadataUriBuilder.CreatePublicProtectedResourceMetadataUri(Request, server))
                    .ToArray()
            })
        };
    }

    [HttpGet($"{ProxyWardOAuthMetadataUriBuilder.ProtectedResourceMetadataPrefix}/{{**routePath}}", Order = -1_000)]
    public async Task<IActionResult> GetRouteProtectedResourceMetadata(
        string routePath,
        [FromServices] IProxyWardPolicyProvider policyProvider,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var server = ResolveServer(routePath, policyProvider.Current);
        if (server is null)
        {
            return NotFound(new
            {
                error = "No MCP proxy route configured for this protected resource metadata request."
            });
        }

        return await HandleProtectedResourceMetadataAsync(
            server,
            httpClientFactory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IActionResult> HandleProtectedResourceMetadataAsync(
        ServerPolicy server,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        var upstreamMetadataUri = ProxyWardOAuthMetadataUriBuilder.CreateUpstreamProtectedResourceMetadataUri(
            server,
            Request.QueryString);
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, upstreamMetadataUri);

        if (Request.Headers.TryGetValue("Accept", out var accept))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        }

        using var response = await httpClientFactory
            .CreateClient()
            .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (!response.IsSuccessStatusCode)
        {
            return new ContentResult
            {
                Content = content,
                ContentType = contentType,
                StatusCode = (int)response.StatusCode
            };
        }

        JsonNode? metadata;
        try
        {
            metadata = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return new ContentResult
            {
                Content = content,
                ContentType = contentType,
                StatusCode = (int)response.StatusCode
            };
        }

        if (metadata is JsonObject metadataObject)
        {
            metadataObject["resource"] = ProxyWardOAuthMetadataUriBuilder.CreatePublicResourceUri(Request, server);
        }

        return new ContentResult
        {
            Content = metadata?.ToJsonString(JsonOptions) ?? "{}",
            ContentType = "application/json",
            StatusCode = (int)response.StatusCode
        };
    }

    private static ServerPolicy? ResolveServer(string routePath, ProxyWardPolicy policy)
    {
        var metadataRoutePath = ProxyWardOAuthMetadataUriBuilder.NormalizeRoutePrefix($"/{routePath}");
        return policy.Servers.Values.FirstOrDefault(server =>
            string.Equals(
                ProxyWardOAuthMetadataUriBuilder.NormalizeRoutePrefix(server.Route),
                metadataRoutePath,
                StringComparison.OrdinalIgnoreCase));
    }
}

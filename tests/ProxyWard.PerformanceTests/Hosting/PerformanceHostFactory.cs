using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.PerformanceTests;

internal static class PerformanceHostFactory
{
    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = []
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0);
        });

        return builder;
    }

    public static string GetBoundAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        var address = addresses?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Kestrel did not expose a bound address for the performance host.");
        }

        return address.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<RouteConfig> CreateMcpRoutes(string routeId) =>
    [
        new RouteConfig
        {
            RouteId = routeId,
            ClusterId = "upstream",
            Match = new RouteMatch
            {
                Path = "/mcp"
            }
        }
    ];

    public static IReadOnlyList<ClusterConfig> CreateUpstreamClusters(string upstreamBaseAddress) =>
    [
        new ClusterConfig
        {
            ClusterId = "upstream",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["primary"] = new()
                {
                    Address = $"{upstreamBaseAddress.TrimEnd('/')}/"
                }
            }
        }
    ];

    public static async Task WriteJsonWithContentLengthAsync(HttpContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}

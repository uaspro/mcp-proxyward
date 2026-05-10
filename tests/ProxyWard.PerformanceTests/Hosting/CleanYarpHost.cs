using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ProxyWard.PerformanceTests;

internal static class CleanYarpHost
{
    public static async Task<StartedHost> StartAsync(string upstreamBaseAddress)
    {
        var builder = PerformanceHostFactory.CreateBuilder();

        builder.Services
            .AddReverseProxy()
            .LoadFromMemory(
                PerformanceHostFactory.CreateMcpRoutes("clean-yarp"),
                PerformanceHostFactory.CreateUpstreamClusters(upstreamBaseAddress));

        var app = builder.Build();
        app.MapReverseProxy();

        await app.StartAsync().ConfigureAwait(false);
        return new StartedHost(PerformanceHostFactory.GetBoundAddress(app), app);
    }
}

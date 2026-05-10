using Microsoft.AspNetCore.Builder;

namespace ProxyWard.PerformanceTests;

internal sealed class StartedHost(string baseAddress, WebApplication app) : IAsyncDisposable
{
    public string BaseAddress { get; } = baseAddress;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}

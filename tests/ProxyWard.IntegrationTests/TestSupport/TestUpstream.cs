using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ProxyWard.IntegrationTests;

internal sealed class TestUpstream(
    string baseAddress,
    WebApplication app,
    TestRequestCounter counter) : IAsyncDisposable
{
    public string BaseAddress { get; } = baseAddress;

    public int RequestCount => counter.Count;

    public static Task<TestUpstream> StartEchoAsync() =>
        StartJsonAsync(request => new
        {
            method = request.Method,
            path = request.Path.Value,
            query = request.QueryString.Value
        });

    public static Task<TestUpstream> StartJsonAsync(Func<HttpRequest, object> responseFactory) =>
        StartAsync(context => Results.Json(responseFactory(context.Request)).ExecuteAsync(context));

    public static Task<TestUpstream> StartJsonAsync(Func<string, HttpRequest, object> responseFactory) =>
        StartAsync((baseAddress, context) => Results.Json(responseFactory(baseAddress, context.Request)).ExecuteAsync(context));

    public static async Task<TestUpstream> StartAsync(Func<HttpContext, Task> handleRequestAsync)
    {
        return await StartAsync((_, context) => handleRequestAsync(context)).ConfigureAwait(false);
    }

    public static async Task<TestUpstream> StartAsync(Func<string, HttpContext, Task> handleRequestAsync)
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var counter = new TestRequestCounter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", async context =>
        {
            counter.Increment();
            await handleRequestAsync(baseAddress, context).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        return new TestUpstream(baseAddress, app, counter);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

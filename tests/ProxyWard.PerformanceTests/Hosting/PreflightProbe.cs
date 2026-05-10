using System.Net.Http.Headers;
using System.Text.Json;

namespace ProxyWard.PerformanceTests;

internal static class PreflightProbe
{
    private static readonly byte[] Payload = JsonSerializer.SerializeToUtf8Bytes(new
    {
        jsonrpc = "2.0",
        id = 0,
        method = "tools/call",
        @params = new
        {
            name = "preflight",
            arguments = new
            {
                message = "ready"
            }
        }
    });

    public static async Task EnsureReadyAsync(StartedHost host, string name)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4
        })
        {
            BaseAddress = new Uri(host.BaseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };

        Exception? lastException = null;

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(Payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await http.PostAsync("/mcp", content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastException = new HttpRequestException(
                    $"{name} preflight returned HTTP {(int)response.StatusCode} from {host.BaseAddress}/mcp.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"{name} preflight failed for {host.BaseAddress}/mcp after 20 attempts. " +
            "The performance host did not accept local loopback traffic.",
            lastException);
    }
}

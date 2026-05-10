using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProxyWard.IntegrationTests;

public class ObservabilityExportIntegrationTests
{
    [Fact]
    public async Task OtlpEnabledPolicyStartsWithoutLiveCollector()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            """
              otlp:
                enabled: true
                endpoint: http://127.0.0.1:4317
              applicationInsights:
                enabled: false
                connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();

            Assert.NotNull(factory.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(policyPath);
        }
    }

    [Fact]
    public async Task ApplicationInsightsEnabledWithConnectionStringStarts()
    {
        const string connectionString =
            "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.com/";
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            """
              otlp:
                enabled: false
                endpoint: http://127.0.0.1:4317
              applicationInsights:
                enabled: true
                connectionStringEnv: PROXYWARD_TEST_APPLICATIONINSIGHTS_CONNECTION_STRING
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_TEST_APPLICATIONINSIGHTS_CONNECTION_STRING", connectionString);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();

            Assert.NotNull(factory.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            Environment.SetEnvironmentVariable("PROXYWARD_TEST_APPLICATIONINSIGHTS_CONNECTION_STRING", null);
            DeleteIfExists(policyPath);
        }
    }

    [Fact]
    public async Task ApplicationInsightsDisabledDoesNotRequireConnectionString()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(
            upstream.BaseAddress,
            """
              otlp:
                enabled: false
                endpoint: http://127.0.0.1:4317
              applicationInsights:
                enabled: false
                connectionStringEnv: PROXYWARD_TEST_APPLICATIONINSIGHTS_CONNECTION_STRING
            """));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_TEST_APPLICATIONINSIGHTS_CONNECTION_STRING", null);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();

            Assert.NotNull(factory.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            DeleteIfExists(policyPath);
        }
    }

    private static async Task<UpstreamApp> StartUpstreamAsync()
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);
        var app = builder.Build();
        app.Map("/{**path}", () => Results.Ok());

        await app.StartAsync();
        return new UpstreamApp(baseAddress, app);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-export-{Guid.NewGuid():N}.yaml");
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; Windows can briefly hold temp files after host disposal.
        }
    }

    private static string CreatePolicy(string upstreamBaseAddress, string exporterOptions) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 4096
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db").Replace("\\", "/")}}
        observability:
          serviceName: mcp-proxyward-test
          console:
            enabled: false
        {{exporterOptions}}
          sampling:
            tracesRatio: 1.0
        servers:
          github:
            route: /github/mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: allow
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots: []
                blockTraversal: false
              hosts:
                allow: []
                blockPrivateNetworks: false
              commands:
                blockShell: false
                dangerous: []
        """;

    private sealed class UpstreamApp(string baseAddress, WebApplication app) : IAsyncDisposable
    {
        public string BaseAddress { get; } = baseAddress;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

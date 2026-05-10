using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProxyWard.IntegrationTests;

public class YarpProxyTests
{
    [Fact]
    public async Task ConfiguredRouteProxiesToUpstreamWithMappedPathAndQuery()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcp/tools/list?cursor=abc");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("/mcp/tools/list", payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("?cursor=abc", payload.RootElement.GetProperty("query").GetString());
            Assert.Equal("GET", payload.RootElement.GetProperty("method").GetString());

            using var exactResponse = await client.GetAsync("/github/mcp?ping=1");

            Assert.Equal(HttpStatusCode.OK, exactResponse.StatusCode);

            await using var exactStream = await exactResponse.Content.ReadAsStreamAsync();
            using var exactPayload = await JsonDocument.ParseAsync(exactStream);

            Assert.Equal("/mcp", exactPayload.RootElement.GetProperty("path").GetString());
            Assert.Equal("?ping=1", exactPayload.RootElement.GetProperty("query").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Theory]
    [InlineData(
        "/.well-known/oauth-protected-resource/github/mcp?resource=http%3A%2F%2F127.0.0.1%3A8080%2Fgithub%2Fmcp",
        "/.well-known/oauth-protected-resource/mcp")]
    [InlineData(
        "/.well-known/oauth-authorization-server/github/mcp",
        "/.well-known/oauth-authorization-server/mcp")]
    [InlineData(
        "/.well-known/openid-configuration/github/mcp",
        "/.well-known/openid-configuration/mcp")]
    [InlineData(
        "/.well-known/oauth-protected-resource",
        "/.well-known/oauth-protected-resource/mcp")]
    public async Task WellKnownMetadataRoutesProxyToConfiguredUpstreamMetadataPath(
        string requestPath,
        string expectedUpstreamPath)
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(requestPath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal(expectedUpstreamPath, payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("GET", payload.RootElement.GetProperty("method").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task ProtectedResourceMetadataPublishesProxyRouteAsResource()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/.well-known/oauth-protected-resource/github/mcp");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("http://localhost/github/mcp", payload.RootElement.GetProperty("resource").GetString());
            Assert.Equal(
                "https://github.com/login/oauth",
                payload.RootElement.GetProperty("authorization_servers")[0].GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task AuthChallengePublishesProxyMetadataUrl()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcp/auth-required");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var challenge = Assert.Single(response.Headers.WwwAuthenticate).ToString();
            Assert.Contains(
                "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/github/mcp\"",
                challenge,
                StringComparison.Ordinal);
            Assert.DoesNotContain(upstream.BaseAddress, challenge, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    [Fact]
    public async Task UnknownRouteReturnsClearNotFoundError()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/unknown/mcp");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("No MCP proxy route configured for this request.", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    private static async Task<UpstreamApp> StartUpstreamAsync()
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", (HttpRequest request) =>
        {
            if (request.Path.StartsWithSegments("/mcp/auth-required"))
            {
                request.HttpContext.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"invalid_request\", resource_metadata=\"{CreateUpstreamProtectedResourceMetadataUri()}\"";
                return Results.Text(
                    "missing required Authorization header",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(new
            {
                method = request.Method,
                path = request.Path.Value,
                query = request.QueryString.Value,
                resource = $"{baseAddress}/mcp",
                authorization_servers = new[] { "https://github.com/login/oauth" }
            });
        });

        await app.StartAsync();
        return new UpstreamApp(baseAddress, app);

        string CreateUpstreamProtectedResourceMetadataUri() =>
            $"{baseAddress}/.well-known/oauth-protected-resource/mcp";
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.yaml");
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string CreatePolicy(string upstreamBaseAddress) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
        audit:
          sink: sqlite
          sqlitePath: ./data/proxyward.db
        observability:
          serviceName: mcp-proxyward
          console:
            enabled: true
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          github:
            route: /github/mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
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

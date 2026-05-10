using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProxyWard.IntegrationTests;

public class YarpProxyTests
{
    [Fact]
    public async Task ConfiguredRouteProxiesToUpstreamWithMappedPathAndQuery()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/github/mcp/tools/list?cursor=abc");

        using var payload = await TestJson.ReadOkAsync(response);

        Assert.Equal("/mcp/tools/list", payload.RootElement.GetProperty("path").GetString());
        Assert.Equal("?cursor=abc", payload.RootElement.GetProperty("query").GetString());
        Assert.Equal("GET", payload.RootElement.GetProperty("method").GetString());

        using var exactResponse = await client.GetAsync("/github/mcp?ping=1");

        using var exactPayload = await TestJson.ReadOkAsync(exactResponse);

        Assert.Equal("/mcp", exactPayload.RootElement.GetProperty("path").GetString());
        Assert.Equal("?ping=1", exactPayload.RootElement.GetProperty("query").GetString());
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
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(requestPath);

        using var payload = await TestJson.ReadOkAsync(response);

        Assert.Equal(expectedUpstreamPath, payload.RootElement.GetProperty("path").GetString());
        Assert.Equal("GET", payload.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ProtectedResourceMetadataPublishesProxyRouteAsResource()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/.well-known/oauth-protected-resource/github/mcp");

        using var payload = await TestJson.ReadOkAsync(response);

        Assert.Equal("http://localhost/github/mcp", payload.RootElement.GetProperty("resource").GetString());
        Assert.Equal(
            "https://github.com/login/oauth",
            payload.RootElement.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact]
    public async Task AuthChallengePublishesProxyMetadataUrl()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress)));

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

    [Fact]
    public async Task UnknownRouteReturnsClearNotFoundError()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/unknown/mcp");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No MCP proxy route configured for this request.", body, StringComparison.Ordinal);
    }

    private static Task<TestUpstream> StartUpstreamAsync() =>
        TestUpstream.StartAsync((baseAddress, context) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/mcp/auth-required"))
            {
                context.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"invalid_request\", resource_metadata=\"{CreateUpstreamProtectedResourceMetadataUri()}\"";
                return Results.Text(
                    "missing required Authorization header",
                    statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            }

            return Results.Json(new
            {
                method = request.Method,
                path = request.Path.Value,
                query = request.QueryString.Value,
                resource = $"{baseAddress}/mcp",
                authorization_servers = new[] { "https://github.com/login/oauth" }
            }).ExecuteAsync(context);

            string CreateUpstreamProtectedResourceMetadataUri() =>
                $"{baseAddress}/.well-known/oauth-protected-resource/mcp";
        });

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
}

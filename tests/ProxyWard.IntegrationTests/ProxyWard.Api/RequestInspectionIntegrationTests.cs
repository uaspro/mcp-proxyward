using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ProxyWard.IntegrationTests;

public class RequestInspectionIntegrationTests
{
    [Fact]
    public async Task InspectableJsonPostReachesUpstreamWithOriginalBody()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", "warn", maxBodyBytes: 1024, upstream.BaseAddress)));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"cursor":"abc"}}""";

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/github/mcp",
            new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);

        using var payload = await TestJson.ReadAsync(response);

        Assert.Equal(HttpMethod.Post.Method, payload.RootElement.GetProperty("method").GetString());
        Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task ChunkedJsonPostWithinMaxBodyBytesReachesUpstreamWithOriginalBody()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", "block", maxBodyBytes: 1024, upstream.BaseAddress)));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"cursor":"streamed"}}""";

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var content = new StreamContent(new NonSeekableMemoryStream(Encoding.UTF8.GetBytes(body)));
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

        using var response = await client.PostAsync("/github/mcp", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);

        using var payload = await TestJson.ReadAsync(response);

        Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task UnsupportedContentTypeWarnsAndReachesUpstreamWithOriginalBody()
    {
        await using var upstream = await StartUpstreamAsync();
        var logs = new CapturingLoggerProvider();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", "warn", maxBodyBytes: 1024, upstream.BaseAddress)));
        var body = "not-json-but-should-pass";

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            }));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/github/mcp",
            new StringContent(body, Encoding.UTF8, MediaTypeNames.Text.Plain));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);

        using var payload = await TestJson.ReadAsync(response);

        Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
        Assert.Contains(logs.Entries, entry =>
            entry.State.TryGetValue("EventType", out var eventType)
            && eventType == "request_inspection"
            && entry.State.TryGetValue("Decision", out var decision)
            && decision == "warn"
            && entry.State.TryGetValue("Reasons", out var reasons)
            && reasons.Contains("inspection_unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedBodyWithBlockBehaviorIsBlockedBeforeUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", "block", maxBodyBytes: 10, upstream.BaseAddress)));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/github/mcp",
            new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, upstream.RequestCount);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("inspection_unsupported", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRouteWithOversizedBodyUsesFallbackInsteadOfInspectionBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", "block", maxBodyBytes: 10, upstream.BaseAddress)));
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/unknown/mcp",
            new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, upstream.RequestCount);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains("No MCP proxy route configured for this request.", responseBody, StringComparison.Ordinal);
    }

    private static Task<TestUpstream> StartUpstreamAsync() =>
        TestUpstream.StartAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

            await Results.Json(new
            {
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                contentType = context.Request.ContentType,
                body
            }).ExecuteAsync(context).ConfigureAwait(false);
        });

    private static string CreatePolicy(
        string mode,
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress) =>
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: {{maxBodyBytes}}
          unsupportedStreaming: {{unsupportedStreaming}}
        audit:
          enabled: true
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
    private sealed class NonSeekableMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) =>
            throw new NotSupportedException();
    }
}

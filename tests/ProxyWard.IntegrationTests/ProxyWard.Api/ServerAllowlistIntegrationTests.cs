using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ProxyWard.IntegrationTests;

public class ServerAllowlistIntegrationTests
{
    [Fact]
    public async Task AllowedServerInEnforceModeReachesUpstream()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", allowed: true, upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/github/mcp?ping=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);

        using var payload = await TestJson.ReadAsync(response);

        Assert.Equal("/mcp", payload.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task DisallowedServerInEnforceModeBlocksBeforeUpstream()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", allowed: false, upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/github/mcp");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, upstream.RequestCount);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("server_not_allowed", body, StringComparison.Ordinal);
        Assert.Contains("github", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimilarPrefixDoesNotMatchConfiguredServerRoute()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("enforce", allowed: false, upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/github/mcpish");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, upstream.RequestCount);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No MCP proxy route configured for this request.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisallowedServerInAuditModeLogsWouldBlockAndReachesUpstream()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var logs = new CapturingLoggerProvider();
        using var environment = TestEnvironment
            .Create()
            .Set("PROXYWARD_DB_PATH", TestFiles.SavePolicy(CreatePolicy("audit", allowed: false, upstream.BaseAddress)));

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(logs);
            }));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/github/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);

        Assert.Contains(logs.Entries, entry =>
            entry.State.TryGetValue("EventType", out var eventType)
            && eventType == "server_allowlist_policy"
            && entry.State.TryGetValue("Decision", out var decision)
            && decision == "would_block"
            && entry.State.TryGetValue("ServerId", out var serverId)
            && serverId == "github"
            && entry.State.TryGetValue("Reasons", out var reasons)
            && reasons.Contains("server_not_allowed", StringComparison.Ordinal));
    }

    private static string CreatePolicy(string mode, bool allowed, string upstreamBaseAddress) =>
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
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
            allowed: {{allowed.ToString().ToLowerInvariant()}}
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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ProxyWard.IntegrationTests;

public class ServerAllowlistIntegrationTests
{
    [Fact]
    public async Task AllowedServerInEnforceModeReachesUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", allowed: true, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcp?ping=1");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("/mcp", payload.RootElement.GetProperty("path").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task DisallowedServerInEnforceModeBlocksBeforeUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", allowed: false, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcp");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("server_not_allowed", body, StringComparison.Ordinal);
            Assert.Contains("github", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task SimilarPrefixDoesNotMatchConfiguredServerRoute()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", allowed: false, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/github/mcpish");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("No MCP proxy route configured for this request.", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task DisallowedServerInAuditModeLogsWouldBlockAndReachesUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var logs = new CapturingLoggerProvider();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", allowed: false, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);

        try
        {
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
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    private static async Task<UpstreamApp> StartUpstreamAsync()
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var counter = new RequestCounter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", (HttpRequest request) =>
        {
            counter.Increment();

            return Results.Json(new
            {
                method = request.Method,
                path = request.Path.Value,
                query = request.QueryString.Value
            });
        });

        await app.StartAsync();
        return new UpstreamApp(baseAddress, app, counter);
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
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string CreatePolicy(string mode, bool allowed, string upstreamBaseAddress) =>
        $$"""
        mode: {{mode}}
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

    private sealed class RequestCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class UpstreamApp(
        string baseAddress,
        WebApplication app,
        RequestCounter counter) : IAsyncDisposable
    {
        public string BaseAddress { get; } = baseAddress;

        public int RequestCount => counter.Count;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed record CapturedLog(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, string> State);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyCollection<CapturedLog> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(
        string categoryName,
        ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var structuredState = new Dictionary<string, string>(StringComparer.Ordinal);

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    structuredState[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                }
            }

            entries.Enqueue(new CapturedLog(
                categoryName,
                logLevel,
                eventId,
                formatter(state, exception),
                structuredState));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", maxBodyBytes: 1024, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"cursor":"abc"}}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("POST", payload.RootElement.GetProperty("method").GetString());
            Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task UnsupportedContentTypeWarnsAndReachesUpstreamWithOriginalBody()
    {
        await using var upstream = await StartUpstreamAsync();
        var logs = new CapturingLoggerProvider();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", maxBodyBytes: 1024, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);
        var body = "not-json-but-should-pass";

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(logs);
                }));
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "text/plain"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, upstream.RequestCount);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
            Assert.Contains(logs.Entries, entry =>
                entry.State.TryGetValue("EventType", out var eventType)
                && eventType == "request_inspection"
                && entry.State.TryGetValue("Decision", out var decision)
                && decision == "warn"
                && entry.State.TryGetValue("Reasons", out var reasons)
                && reasons.Contains("inspection_unsupported", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task OversizedBodyWithBlockBehaviorIsBlockedBeforeUpstream()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", maxBodyBytes: 10, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("inspection_unsupported", responseBody, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", null);
        }
    }

    [Fact]
    public async Task UnknownRouteWithOversizedBodyUsesFallbackInsteadOfInspectionBlock()
    {
        await using var upstream = await StartUpstreamAsync();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", maxBodyBytes: 10, upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_POLICY_PATH", policyPath);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/unknown/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(0, upstream.RequestCount);

            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("No MCP proxy route configured for this request.", responseBody, StringComparison.Ordinal);
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
        app.Map("/{**path}", async (HttpRequest request) =>
        {
            counter.Increment();

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            return Results.Json(new
            {
                method = request.Method,
                path = request.Path.Value,
                query = request.QueryString.Value,
                contentType = request.ContentType,
                body
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

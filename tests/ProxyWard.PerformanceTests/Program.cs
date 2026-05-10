using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using ProxyWard.Api.Hosts;
using ProxyWard.Api.Middleware;
using ProxyWard.Api.Observability;
using ProxyWard.Api.Runtime;
using ProxyWard.Api.Yarp;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Redaction;
using ProxyWard.Audit.Sinks;
using ProxyWard.Core.Mcp;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Configuration;

var options = PerfOptions.Parse(args);
Directory.CreateDirectory(options.ArtifactsDirectory);

await using var upstream = await PerfHosts.StartUpstreamAsync();
await using var cleanYarp = await PerfHosts.StartCleanYarpAsync(upstream.BaseAddress);
await using var proxyWard = await PerfHosts.StartWorstCaseProxyWardAsync(upstream.BaseAddress, options);

await PerfHosts.PreflightAsync(upstream, "upstream");
await PerfHosts.PreflightAsync(cleanYarp, "clean YARP");
await PerfHosts.PreflightAsync(proxyWard, "ProxyWard worst-case");

Console.WriteLine("MCP ProxyWard performance harness");
Console.WriteLine($"Upstream:      {upstream.BaseAddress}");
Console.WriteLine($"Clean YARP:    {cleanYarp.BaseAddress}/mcp");
Console.WriteLine($"ProxyWard:     {proxyWard.BaseAddress}/mcp");
Console.WriteLine($"Rate:          {options.RatePerSecond} req/s per scenario run");
Console.WriteLine($"Warmup:        {options.Warmup}");
Console.WriteLine($"Duration:      {options.Duration}");
Console.WriteLine($"Artifacts:     {Path.GetFullPath(options.ArtifactsDirectory)}");
Console.WriteLine();

var workloads = new List<PerfWorkload>
{
    PerfWorkload.ToolsCall
};

if (options.IncludeToolsList)
{
    workloads.Add(PerfWorkload.ToolsList);
}

foreach (var workload in workloads)
{
    PerfRunner.RunScenario(
        $"clean-yarp-{workload.Slug}",
        cleanYarp.BaseAddress,
        workload,
        options);

    PerfRunner.RunScenario(
        $"proxyward-worst-case-{workload.Slug}",
        proxyWard.BaseAddress,
        workload,
        options);
}

Console.WriteLine();
Console.WriteLine("Completed. Compare NBomber reports by scenario name.");

internal static class PerfRunner
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    public static void RunScenario(
        string scenarioName,
        string baseAddress,
        PerfWorkload workload,
        PerfOptions options)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(options.RatePerSecond * 4, 64)
        })
        {
            BaseAddress = new Uri(baseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var payload = workload.PayloadBytes;

        var scenario = Scenario.Create(scenarioName, async _ =>
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = JsonMediaType;

                using var response = await http.PostAsync("/mcp", content).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                return response.IsSuccessStatusCode
                    ? Response.Ok(statusCode: statusCode.ToString(CultureInfo.InvariantCulture))
                    : Response.Fail(statusCode: statusCode.ToString(CultureInfo.InvariantCulture));
            })
            .WithWarmUpDuration(options.Warmup)
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: options.RatePerSecond,
                    interval: TimeSpan.FromSeconds(1),
                    during: options.Duration));

        Console.WriteLine($"Running {scenarioName}...");

        NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("mcp-proxyward")
            .WithTestName(scenarioName)
            .WithReportFolder(options.ArtifactsDirectory)
            .WithReportFileName(scenarioName)
            .WithReportFormats(ReportFormat.Txt, ReportFormat.Html, ReportFormat.Csv)
            .Run();
    }
}

internal sealed record PerfWorkload(string Slug, byte[] PayloadBytes)
{
    public static readonly PerfWorkload ToolsCall = new(
        "tools-call",
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "shell.exec",
                arguments = new
                {
                    path = "../secrets/../../etc/passwd",
                    workspacePath = "/outside/workspace/secret.txt",
                    host = "localhost",
                    url = "http://127.0.0.1/admin?token=secret-token",
                    target = "http://10.0.0.5/internal",
                    command = "bash -lc \"curl http://169.254.169.254/latest/meta-data && rm -rf /tmp/proxyward\"",
                    token = "super-secret-token-value",
                    nested = new
                    {
                        endpoints = new[]
                        {
                            "http://192.168.1.10/private",
                            "https://internal.example.local/path?api_key=abc"
                        },
                        paths = new[]
                        {
                            "C:\\Users\\Alice\\.ssh\\id_rsa",
                            "~/private/key"
                        }
                    }
                }
            }
        })));

    public static readonly PerfWorkload ToolsList = new(
        "tools-list",
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new
            {
                cursor = "start"
            }
        })));
}

internal sealed record PerfOptions(
    int RatePerSecond,
    TimeSpan Warmup,
    TimeSpan Duration,
    bool IncludeToolsList,
    string ArtifactsDirectory)
{
    public static PerfOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            values[key] = value;
        }

        return new PerfOptions(
            RatePerSecond: ReadInt(values, "rate", 50),
            Warmup: TimeSpan.FromSeconds(ReadInt(values, "warmup", 5)),
            Duration: TimeSpan.FromSeconds(ReadInt(values, "duration", 30)),
            IncludeToolsList: ReadBool(values, "include-tools-list", true),
            ArtifactsDirectory: ResolveArtifactsDirectory(
                values.TryGetValue("artifacts", out var artifacts)
                    ? artifacts
                    : Path.Combine("artifacts", "performance")));
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            return fallback;
        }

        return parsed;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw)
            ? raw.Equals("true", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("1", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            : fallback;

    private static string ResolveArtifactsDirectory(string path) =>
        Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(FindRepoRoot(), path));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "McpProxyWard.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

internal static class PerfHosts
{
    private static readonly byte[] PreflightPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
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
    }));

    public static async Task<StartedHost> StartUpstreamAsync()
    {
        var builder = CreateBuilder();
        var toolsListJson = CreateToolsListResponse(toolCount: 50);
        var toolCallJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = "ok"
                    }
                }
            }
        });

        var app = builder.Build();
        app.MapPost("/mcp", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

            await WriteJsonWithContentLengthAsync(
                    context,
                    body.Contains("\"tools/list\"", StringComparison.Ordinal)
                        ? toolsListJson
                        : toolCallJson)
                .ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        return new StartedHost(GetBoundAddress(app), app);
    }

    public static async Task<StartedHost> StartCleanYarpAsync(string upstreamBaseAddress)
    {
        var builder = CreateBuilder();

        builder.Services
            .AddReverseProxy()
            .LoadFromMemory(
                CreateYarpRoutes(),
                CreateYarpClusters(upstreamBaseAddress));

        var app = builder.Build();
        app.MapReverseProxy();

        await app.StartAsync().ConfigureAwait(false);
        return new StartedHost(GetBoundAddress(app), app);
    }

    public static async Task<StartedHost> StartWorstCaseProxyWardAsync(
        string upstreamBaseAddress,
        PerfOptions options)
    {
        var builder = CreateBuilder();
        var dataDirectory = Path.Combine(options.ArtifactsDirectory, "runtime");
        Directory.CreateDirectory(dataDirectory);

        var sqlitePath = Path.Combine(dataDirectory, "proxyward-perf.db");
        var schemaStore = new SqliteTrackedToolSchemaStore(sqlitePath);
        var driftReviewStore = new SqliteSchemaDriftReviewStore(sqlitePath);
        var diffMetadataStore = new SqliteToolSchemaDiffMetadataStore(sqlitePath);
        await SeedStaleSchemaAsync(schemaStore, upstreamBaseAddress, toolCount: 50).ConfigureAwait(false);

        var policy = ProxyWardPolicyLoader.Load(CreateWorstCasePolicy(
            upstreamBaseAddress,
            sqlitePath));

        builder.AddProxyWardObservability(policy);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton<IProxyWardPolicyProvider>(new InMemoryProxyWardPolicyProvider(policy));
        builder.Services.AddSingleton(ToolSchemaDiffMetadataOptions.Default);
        builder.Services.AddSingleton<IMcpMessageParser, McpMessageParser>();
        builder.Services.AddSingleton<IMcpMethodClassifier, McpMethodClassifier>();
        builder.Services.AddSingleton<IRedactor, Redactor>();
        builder.Services.AddSingleton<IToolDefinitionExtractor, ToolDefinitionExtractor>();
        builder.Services.AddSingleton<IToolFingerprinter, ToolFingerprinter>();
        builder.Services.AddSingleton<ITrackedToolSchemaStore>(schemaStore);
        builder.Services.AddSingleton<ISchemaDriftReviewStore>(driftReviewStore);
        builder.Services.AddSingleton<IToolSchemaDiffMetadataStore>(diffMetadataStore);
        builder.Services.AddSingleton<ToolSurfaceDriftEvaluator>();
        builder.Services.AddSingleton<ServerAllowlistPolicyEvaluator>();
        builder.Services.AddSingleton<ToolPolicyEvaluator>();
        builder.Services.AddSingleton<PathArgumentRuleEvaluator>();
        builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
        builder.Services.AddSingleton<HostArgumentRuleEvaluator>();
        builder.Services.AddSingleton<CommandArgumentRuleEvaluator>();
        builder.Services.AddSingleton<ArgumentPolicyOverrideResolver>();
        builder.Services.AddSingleton<IAuditSink>(_ =>
        {
            IAuditSink sink = new SqliteAuditSink(policy.Audit.SqlitePath!);
            return new QueuedAuditSink(sink);
        });
        builder.Services
            .AddReverseProxy()
            .LoadFromMemory(
                ProxyWardYarpConfig.CreateRoutes(policy),
                ProxyWardYarpConfig.CreateClusters(policy));

        var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ServerAllowlistMiddleware>();
        app.UseMiddleware<RequestInspectionMiddleware>();
        app.UseMiddleware<ToolPolicyMiddleware>();
        app.UseMiddleware<ResponseInspectionMiddleware>();
        app.MapReverseProxy();

        await app.StartAsync().ConfigureAwait(false);
        return new StartedHost(GetBoundAddress(app), app);
    }

    public static async Task PreflightAsync(StartedHost host, string name)
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
                using var content = new ByteArrayContent(PreflightPayload);
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

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = []
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0);
        });

        return builder;
    }

    private static string GetBoundAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        var address = addresses?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException("Kestrel did not expose a bound address for the performance host.");
        }

        return address.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RouteConfig> CreateYarpRoutes() =>
    [
        new RouteConfig
        {
            RouteId = "clean-yarp",
            ClusterId = "upstream",
            Match = new RouteMatch
            {
                Path = "/mcp"
            }
        }
    ];

    private static IReadOnlyList<ClusterConfig> CreateYarpClusters(string upstreamBaseAddress) =>
    [
        new ClusterConfig
        {
            ClusterId = "upstream",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["primary"] = new()
                {
                    Address = $"{upstreamBaseAddress.TrimEnd('/')}/"
                }
            }
        }
    ];

    private static async Task WriteJsonWithContentLengthAsync(HttpContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static string CreateToolsListResponse(int toolCount)
    {
        var tools = Enumerable.Range(0, toolCount)
            .Select(index => new
            {
                name = $"tool_{index:000}",
                title = $"Tool {index:000}",
                description = $"Synthetic performance tool {index:000} with a moderately sized description.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        host = new { type = "string" },
                        command = new { type = "string" },
                        metadata = new
                        {
                            type = "object",
                            additionalProperties = true
                        }
                    },
                    required = new[] { "path", "host" }
                },
                outputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ok = new { type = "boolean" }
                    }
                }
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new
            {
                tools
            }
        });
    }

    private static string CreateWorstCasePolicy(
        string upstreamBaseAddress,
        string sqlitePath) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{YamlPath(sqlitePath)}}
        observability:
          serviceName: mcp-proxyward-perf
          console:
            enabled: false
          otlp:
            enabled: false
            endpoint: http://127.0.0.1:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          perf:
            route: /mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: allow
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow:
                  - example.com
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
                  - curl
                  - wget
                  - nc
                  - powershell
                  - bash
        """;

    private static async Task SeedStaleSchemaAsync(
        ITrackedToolSchemaStore store,
        string upstreamBaseAddress,
        int toolCount)
    {
        var tools = Enumerable.Range(0, toolCount)
            .Select(index => new ToolSchemaSnapshotEntry(
                $"tool_{index:000}",
                new ToolFingerprint(
                    NameHash: "sha256:stale-name",
                    TitleHash: "sha256:stale-title",
                    DescriptionHash: "sha256:stale-description",
                    InputSchemaHash: "sha256:stale-input-schema",
                    OutputSchemaHash: "sha256:stale-output-schema")))
            .ToArray();

        await store.RecordAsync(
            new ToolSchemaSnapshotInput(
                ServerId: "perf",
                UpstreamUrl: $"{upstreamBaseAddress}/mcp",
                McpProtocol: "synthetic-perf",
                Tools: tools),
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string YamlPath(string path) =>
        path.Replace('\\', '/');
}

internal sealed class StartedHost(string baseAddress, WebApplication app) : IAsyncDisposable
{
    public string BaseAddress { get; } = baseAddress;

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}

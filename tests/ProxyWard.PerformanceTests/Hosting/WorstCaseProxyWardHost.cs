using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
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

namespace ProxyWard.PerformanceTests;

internal static class WorstCaseProxyWardHost
{
    private const string HiddenToolName = "tool_000";
    private const string BlockedToolName = "tool_001";

    public static async Task<StartedHost> StartAsync(
        string upstreamBaseAddress,
        PerformanceOptions options)
    {
        var builder = PerformanceHostFactory.CreateBuilder();
        var dataDirectory = Path.Combine(options.ArtifactsDirectory, "runtime");
        Directory.CreateDirectory(dataDirectory);

        var sqlitePath = Path.Combine(dataDirectory, "proxyward-perf.db");
        var schemaStore = new SqliteTrackedToolSchemaStore(sqlitePath);
        var driftReviewStore = new SqliteSchemaDriftReviewStore(sqlitePath);
        var diffMetadataStore = new SqliteToolSchemaDiffMetadataStore(sqlitePath);
        await SeedStaleSchemaAsync(schemaStore, upstreamBaseAddress).ConfigureAwait(false);

        var policy = ProxyWardPolicyLoader.Load(CreatePolicyYaml(upstreamBaseAddress, sqlitePath));

        builder.AddProxyWardObservability(policy);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton<IProxyWardPolicyProvider>(new InMemoryProxyWardPolicyProvider(policy));
        builder.Services.AddSingleton(ToolSchemaDiffMetadataOptions.Default);
        builder.Services.AddSingleton<IMcpMessageParser, McpMessageParser>();
        builder.Services.AddSingleton<IMcpMethodClassifier, McpMethodClassifier>();
        builder.Services.AddSingleton<IRedactor, Redactor>();
        builder.Services.AddSingleton<IToolDefinitionExtractor, ToolDefinitionExtractor>();
        builder.Services.AddSingleton<ResponseInspectionTargetResolver>();
        builder.Services.AddSingleton<ResponseInspectionDriftReviewCoordinator>();
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
        return new StartedHost(PerformanceHostFactory.GetBoundAddress(app), app);
    }

    private static string CreatePolicyYaml(
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
              block:
                - {{BlockedToolName}}
              hide:
                - {{HiddenToolName}}
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
        string upstreamBaseAddress)
    {
        var tools = Enumerable.Range(0, SyntheticUpstreamHost.ToolCount)
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

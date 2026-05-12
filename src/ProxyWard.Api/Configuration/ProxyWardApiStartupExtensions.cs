using ProxyWard.Api.Control;
using ProxyWard.Api.Hosts;
using ProxyWard.Api.Middleware;
using ProxyWard.Api.OAuth;
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
using ProxyWard.Policy.Persistence;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Configuration;

public static class ProxyWardApiStartupExtensions
{
    public static async Task AddProxyWardApiAsync(this WebApplicationBuilder builder)
    {
        var databasePath = ResolveDatabasePath(builder.Configuration);
        var policyStore = new SqlitePolicyStore(databasePath);
        var snapshot = await policyStore.InitializeAndReadCurrentAsync(
            ProxyWardDefaultPolicy.CreateYaml(databasePath),
            CancellationToken.None);
        var policy = snapshot.Policy;
        var yarpConfigProvider = new DynamicProxyWardYarpConfigProvider(
            ProxyWardYarpConfig.CreateRoutes(policy),
            ProxyWardYarpConfig.CreateClusters(policy));

        builder.AddProxyWardObservability(policy);

        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(policyStore);
        builder.Services.AddSingleton<IProxyWardPolicyProvider>(new InMemoryProxyWardPolicyProvider(policy));
        builder.Services.AddSingleton(ProxyWardControlOptions.Load(builder.Configuration));
        builder.Services.AddSingleton(CreateToolSchemaDiffMetadataOptions(builder.Configuration));
        builder.Services.AddSingleton<ProxyWardYarpConfigFactory>();
        builder.Services.AddSingleton<IProxyWardYarpConfigProvider>(yarpConfigProvider);
        builder.Services.AddSingleton<IProxyConfigProvider>(yarpConfigProvider);
        builder.Services.AddSingleton<IMcpMessageParser, McpMessageParser>();
        builder.Services.AddSingleton<IMcpMethodClassifier, McpMethodClassifier>();
        builder.Services.AddSingleton<IRedactor, Redactor>();
        builder.Services.AddSingleton<IToolDefinitionExtractor, ToolDefinitionExtractor>();
        builder.Services.AddSingleton<ResponseInspectionTargetResolver>();
        builder.Services.AddSingleton<ResponseInspectionDriftReviewCoordinator>();
        builder.Services.AddSingleton<IToolFingerprinter, ToolFingerprinter>();
        builder.Services.AddSingleton<ITrackedToolSchemaStore>(_ =>
            CreateTrackedToolSchemaStore(policy));
        builder.Services.AddSingleton<ISchemaDriftReviewStore>(_ =>
            CreateSchemaDriftReviewStore(policy));
        builder.Services.AddSingleton<IToolSchemaDiffMetadataStore>(_ =>
            CreateToolSchemaDiffMetadataStore(policy));
        builder.Services.AddSingleton<ToolSurfaceDriftEvaluator>();
        builder.Services.AddSingleton<ServerAllowlistPolicyEvaluator>();
        builder.Services.AddSingleton<ToolPolicyEvaluator>();
        builder.Services.AddSingleton<PathArgumentRuleEvaluator>();
        builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
        builder.Services.AddSingleton<HostArgumentRuleEvaluator>();
        builder.Services.AddSingleton<CommandArgumentRuleEvaluator>();
        builder.Services.AddSingleton<ArgumentPolicyOverrideResolver>();
        builder.Services.AddSingleton<IAuditSink>(services =>
            CreateAuditSink(policy, services.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddScoped<ProxyWardControlAuthorizationService>();
        builder.Services.AddHttpClient();
        builder.Services.AddControllers();
        builder.Services.AddReverseProxy();
    }

    public static void UseProxyWardApi(this WebApplication app)
    {
        app.MapControllers();

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ServerAllowlistMiddleware>();
        app.UseProxyWardOAuthChallengeMetadataRewrite();
        app.UseMiddleware<RequestInspectionMiddleware>();
        app.UseMiddleware<ToolPolicyMiddleware>();
        app.UseMiddleware<ResponseInspectionMiddleware>();

        app.MapReverseProxy();
    }

    private static string ResolveDatabasePath(IConfiguration configuration) =>
        Environment.GetEnvironmentVariable("PROXYWARD_DB_PATH")
        ?? configuration["ProxyWard:DatabasePath"]
        ?? "./data/proxyward.db";

    private static ITrackedToolSchemaStore CreateTrackedToolSchemaStore(ProxyWardPolicy policy) =>
        new SqliteTrackedToolSchemaStore(ResolveSchemaSqlitePath(policy));

    private static ISchemaDriftReviewStore CreateSchemaDriftReviewStore(ProxyWardPolicy policy) =>
        new SqliteSchemaDriftReviewStore(ResolveSchemaSqlitePath(policy));

    private static IToolSchemaDiffMetadataStore CreateToolSchemaDiffMetadataStore(ProxyWardPolicy policy) =>
        new SqliteToolSchemaDiffMetadataStore(ResolveSchemaSqlitePath(policy));

    private static string ResolveSchemaSqlitePath(ProxyWardPolicy policy)
    {
        if (!string.IsNullOrWhiteSpace(policy.Audit.SqlitePath))
        {
            return policy.Audit.SqlitePath;
        }

        return Path.Combine(Path.GetTempPath(), $"proxyward-schema-{Environment.ProcessId}.db");
    }

    private static ToolSchemaDiffMetadataOptions CreateToolSchemaDiffMetadataOptions(
        IConfiguration configuration)
    {
        var captureValues = ReadBool(
            configuration,
            "PROXYWARD_SCHEMA_DIFF_CAPTURE_VALUES",
            "ProxyWard:SchemaDiff:CaptureValues",
            defaultValue: true);
        var maxValueBytes = ReadInt(
            configuration,
            "PROXYWARD_SCHEMA_DIFF_MAX_VALUE_BYTES",
            "ProxyWard:SchemaDiff:MaxValueBytes",
            ToolSchemaDiffMetadataOptions.Default.MaxValueBytes);

        return new ToolSchemaDiffMetadataOptions(captureValues, maxValueBytes).Normalize();
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey,
        bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[configurationKey];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static int ReadInt(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey,
        int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[configurationKey];
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static IAuditSink CreateAuditSink(ProxyWardPolicy policy, ILoggerFactory loggerFactory)
    {
        if (!string.Equals(policy.Audit.Sink, "sqlite", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(policy.Audit.SqlitePath))
        {
            return new NullAuditSink();
        }

        var sink = new SqliteAuditSink(policy.Audit.SqlitePath);
        var logger = loggerFactory.CreateLogger("ProxyWard.Audit.Sinks.QueuedAuditSink");
        return new QueuedAuditSink(
            sink,
            onFailure: (auditEvent, exception) =>
            {
                ProxyWardTelemetry.RecordAuditSinkFailure(new TelemetryMetadata(
                    CorrelationId: auditEvent.CorrelationId,
                    ServerId: auditEvent.ServerId,
                    Method: auditEvent.Method,
                    ToolName: auditEvent.ToolName,
                    Mode: auditEvent.Mode,
                    Decision: FormatAuditDecision(auditEvent.Decision),
                    Reasons: auditEvent.Reasons,
                    PolicyVersion: auditEvent.PolicyVersion,
                    AuditEventType: auditEvent.EventType));

                logger.LogWarning(
                    exception,
                    "ProxyWard queued audit sink failed to persist {AuditEventType} event for server {ServerId}.",
                    auditEvent.EventType,
                    auditEvent.ServerId);
            });
    }

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };
}

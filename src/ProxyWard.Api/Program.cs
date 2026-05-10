using ProxyWard.Api.Hosts;
using ProxyWard.Api.Control;
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
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

var policyPath = Environment.GetEnvironmentVariable("PROXYWARD_POLICY_PATH")
    ?? builder.Configuration["ProxyWard:PolicyPath"]
    ?? "proxyward.yaml";

var policy = ProxyWardPolicyLoader.LoadFile(policyPath);
var controlOptions = ProxyWardControlOptions.Load(builder.Configuration);
var diffMetadataOptions = CreateToolSchemaDiffMetadataOptions(builder.Configuration);
var yarpConfigProvider = new DynamicProxyWardYarpConfigProvider(
    ProxyWardYarpConfig.CreateRoutes(policy),
    ProxyWardYarpConfig.CreateClusters(policy));
builder.AddProxyWardObservability(policy);
builder.Services.AddSingleton(policy);
builder.Services.AddSingleton<IProxyWardPolicyProvider>(new InMemoryProxyWardPolicyProvider(policy));
builder.Services.AddSingleton(controlOptions);
builder.Services.AddSingleton(diffMetadataOptions);
builder.Services.AddSingleton<IProxyWardYarpConfigProvider>(yarpConfigProvider);
builder.Services.AddSingleton<IProxyConfigProvider>(yarpConfigProvider);
builder.Services.AddSingleton<IMcpMessageParser, McpMessageParser>();
builder.Services.AddSingleton<IMcpMethodClassifier, McpMethodClassifier>();
builder.Services.AddSingleton<IRedactor, Redactor>();
builder.Services.AddSingleton<IToolDefinitionExtractor, ToolDefinitionExtractor>();
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
builder.Services.AddReverseProxy();

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", (IProxyWardPolicyProvider policyProvider) =>
{
    var loadedPolicy = policyProvider.Current;
    return Results.Ok(new
    {
        status = "healthy",
        service = "MCP ProxyWard",
        mode = loadedPolicy.Mode.ToString().ToLowerInvariant(),
        policyVersion = loadedPolicy.VersionHash,
        serverCount = loadedPolicy.Servers.Count
    });
});

app.MapProxyWardControlEndpoints();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ServerAllowlistMiddleware>();
app.UseMiddleware<RequestInspectionMiddleware>();
app.UseMiddleware<ToolPolicyMiddleware>();
app.UseMiddleware<ResponseInspectionMiddleware>();

app.MapReverseProxy();

app.MapFallback(() => Results.NotFound(new
{
    error = "No MCP proxy route configured for this request."
}));

app.Run();

static ITrackedToolSchemaStore CreateTrackedToolSchemaStore(ProxyWardPolicy policy)
{
    return new SqliteTrackedToolSchemaStore(ResolveSchemaSqlitePath(policy));
}

static ISchemaDriftReviewStore CreateSchemaDriftReviewStore(ProxyWardPolicy policy)
{
    return new SqliteSchemaDriftReviewStore(ResolveSchemaSqlitePath(policy));
}

static IToolSchemaDiffMetadataStore CreateToolSchemaDiffMetadataStore(ProxyWardPolicy policy)
{
    return new SqliteToolSchemaDiffMetadataStore(ResolveSchemaSqlitePath(policy));
}

static string ResolveSchemaSqlitePath(ProxyWardPolicy policy)
{
    var sqlitePath = policy.Audit.SqlitePath;
    if (string.IsNullOrWhiteSpace(sqlitePath))
    {
        sqlitePath = Path.Combine(Path.GetTempPath(), $"proxyward-schema-{Environment.ProcessId}.db");
    }

    return sqlitePath;
}

static ToolSchemaDiffMetadataOptions CreateToolSchemaDiffMetadataOptions(ConfigurationManager configuration)
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

static bool ReadBool(
    ConfigurationManager configuration,
    string environmentVariable,
    string configurationKey,
    bool defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(environmentVariable)
        ?? configuration[configurationKey];
    return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
}

static int ReadInt(
    ConfigurationManager configuration,
    string environmentVariable,
    string configurationKey,
    int defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(environmentVariable)
        ?? configuration[configurationKey];
    return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
}

static IAuditSink CreateAuditSink(ProxyWardPolicy policy, ILoggerFactory loggerFactory)
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

static string FormatAuditDecision(AuditDecision decision) =>
    decision switch
    {
        AuditDecision.Block => "block",
        AuditDecision.WouldBlock => "would_block",
        AuditDecision.Warn => "warn",
        _ => "allow"
    };

public partial class Program;

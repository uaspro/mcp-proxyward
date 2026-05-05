using System.Diagnostics;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.Api.Middleware;

public sealed class ServerAllowlistMiddleware(
    RequestDelegate next,
    ProxyWardPolicy policy,
    ServerAllowlistPolicyEvaluator evaluator,
    IAuditSink auditSink,
    ILogger<ServerAllowlistMiddleware> logger)
{
    private static readonly EventId ServerAllowlistEvent = new(1001, "ServerAllowlistPolicy");
    private static readonly EventId AuditSinkFailureEvent = new(1202, "AuditSinkFailure");

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var server = ResolveServer(context.Request.Path);

        if (server is null)
        {
            await next(context);
            return;
        }

        context.Items[ServerResolutionItems.ServerPolicy] = server;

        PolicyDecision decision;
        using (var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.PolicyEvaluationActivity,
            CreateTelemetryMetadata(context, server)))
        {
            decision = evaluator.Evaluate(policy.Mode, server);
            var telemetry = CreateTelemetryMetadata(
                context,
                server,
                decision: FormatDecision(decision.Type),
                reasons: decision.Reasons);
            ProxyWardTelemetry.Enrich(activity, telemetry);
            ProxyWardTelemetry.RecordPolicyDecision(telemetry);
        }

        if (decision.Type == PolicyDecisionType.Allow)
        {
            await next(context);
            return;
        }

        stopwatch.Stop();

        LogDecision(context, server, decision);
        await EmitAuditAsync(context, server, decision, stopwatch.ElapsedMilliseconds);

        if (decision.Type == PolicyDecisionType.WouldBlock)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "MCP server is not allowed by policy.",
            decision = "block",
            serverId = server.Id,
            reasons = decision.Reasons
        });
    }

    private ServerPolicy? ResolveServer(PathString path)
    {
        var requestPath = path.Value ?? "/";

        return policy.Servers.Values
            .Select(server => new
            {
                Server = server,
                Route = NormalizeRoutePrefix(server.Route)
            })
            .Where(candidate => RouteMatches(requestPath, candidate.Route))
            .OrderByDescending(candidate => candidate.Route.Length)
            .Select(candidate => candidate.Server)
            .FirstOrDefault();
    }

    private void LogDecision(HttpContext context, ServerPolicy server, PolicyDecision decision)
    {
        logger.LogWarning(
            ServerAllowlistEvent,
            "ProxyWard audit event {EventType}: {Decision} for server {ServerId} in {Mode} mode with reasons {Reasons} and policy {PolicyVersion}; service {ServiceName}; correlation {CorrelationId}",
            "server_allowlist_policy",
            FormatDecision(decision.Type),
            server.Id,
            FormatMode(policy.Mode),
            string.Join(",", decision.Reasons),
            policy.VersionHash,
            ProxyWardTelemetry.ServiceName,
            ResolveCorrelationId(context));
    }

    private async Task EmitAuditAsync(HttpContext context, ServerPolicy server, PolicyDecision decision, long durationMs)
    {
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "server_allowlist_policy",
            Mode: FormatMode(policy.Mode),
            Decision: ToAuditDecision(decision.Type),
            ServerId: server.Id,
            Method: null,
            ToolName: null,
            Reasons: decision.Reasons,
            PolicyVersion: policy.VersionHash,
            CorrelationId: ResolveCorrelationId(context),
            RequestBytes: context.Request.ContentLength ?? 0,
            DurationMs: durationMs,
            ArgumentSummary: null,
            BatchSize: 0);

        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.AuditWriteActivity,
            CreateTelemetryMetadata(
                context,
                server,
                decision: FormatDecision(decision.Type),
                reasons: decision.Reasons,
                eventType: auditEvent.EventType));

        try
        {
            await auditSink.WriteAsync(auditEvent, context.RequestAborted);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "audit_sink_failure");
            ProxyWardTelemetry.RecordAuditSinkFailure(CreateTelemetryMetadata(
                context,
                server,
                decision: FormatDecision(decision.Type),
                reasons: decision.Reasons,
                eventType: auditEvent.EventType));
            logger.LogWarning(
                AuditSinkFailureEvent,
                ex,
                "ProxyWard audit sink failed to record server_allowlist_policy event for server {ServerId}.",
                server.Id);
        }
    }

    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[AuditItems.CorrelationId] as string ?? context.TraceIdentifier;

    private TelemetryMetadata CreateTelemetryMetadata(
        HttpContext context,
        ServerPolicy server,
        string? decision = null,
        IReadOnlyCollection<string>? reasons = null,
        string? eventType = null) =>
        new(
            CorrelationId: ResolveCorrelationId(context),
            ServerId: server.Id,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            AuditEventType: eventType);

    private static AuditDecision ToAuditDecision(PolicyDecisionType type) =>
        type switch
        {
            PolicyDecisionType.Block => AuditDecision.Block,
            PolicyDecisionType.WouldBlock => AuditDecision.WouldBlock,
            PolicyDecisionType.Warn => AuditDecision.Warn,
            _ => AuditDecision.Allow
        };

    private static bool RouteMatches(string requestPath, string routePrefix) =>
        routePrefix == "/"
        || requestPath.Equals(routePrefix, StringComparison.OrdinalIgnoreCase)
        || requestPath.StartsWith($"{routePrefix}/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRoutePrefix(string route)
    {
        var normalized = route.TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? "/" : normalized;
    }

    private static string FormatDecision(PolicyDecisionType type) =>
        type switch
        {
            PolicyDecisionType.Block => "block",
            PolicyDecisionType.WouldBlock => "would_block",
            PolicyDecisionType.Warn => "warn",
            _ => "allow"
        };

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";
}

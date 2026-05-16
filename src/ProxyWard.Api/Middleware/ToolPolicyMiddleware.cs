using System.Diagnostics;
using System.Text.Json.Nodes;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Redaction;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;
using ProxyWard.Proxy.Application.Runtime;

namespace ProxyWard.Api.Middleware;

public sealed class ToolPolicyMiddleware(
    RequestDelegate next,
    IProxyWardPolicyProvider policyProvider,
    ToolPolicyEvaluator evaluator,
    PathArgumentRuleEvaluator pathRules,
    HostArgumentRuleEvaluator hostRules,
    CommandArgumentRuleEvaluator commandRules,
    ArgumentPolicyOverrideResolver argumentOverrides,
    IMcpMethodClassifier classifier,
    IRedactor redactor,
    IAuditSink auditSink,
    ILogger<ToolPolicyMiddleware> logger)
{
    private static readonly EventId AuditSinkFailureEvent = new(1203, "AuditSinkFailure");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServerResolutionItems.ServerPolicy, out var serverItem)
            || serverItem is not ServerPolicy server)
        {
            await next(context);
            return;
        }

        if (!context.Items.TryGetValue(RequestInspectionItems.JsonRpcParseResult, out var parseItem)
            || parseItem is not JsonRpcParseResult parseResult
            || parseResult.Status != JsonRpcParseStatus.Parsed
            || parseResult.Messages.Count == 0)
        {
            await next(context);
            return;
        }

        var policy = ResolvePolicySnapshot(context);
        var evaluations = new List<ToolPolicyEvaluation>();

        foreach (var message in parseResult.Messages)
        {
            var classification = classifier.Classify(message);
            if (classification.Kind != McpMessageKind.ToolCall)
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var argumentSummary = redactor.Redact("params", message.Params, CreateSecretRedactionOptions(server)).Value;
            ToolPolicyDecisionResult decisionResult;
            using (var activity = ProxyWardTelemetry.StartActivity(
                ProxyWardTelemetry.PolicyEvaluationActivity,
                CreateTelemetryMetadata(
                    context,
                    policy,
                    server,
                    classification.ToolName)))
            {
                decisionResult = await EvaluateToolCallAsync(
                        context,
                        policy,
                        server,
                        classification.ToolName,
                        message)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                var telemetry = CreateTelemetryMetadata(
                    context,
                    policy,
                    server,
                    classification.ToolName,
                    FormatDecision(decisionResult.Decision.Type),
                    decisionResult.Decision.Reasons,
                    argumentSummary: argumentSummary);
                ProxyWardTelemetry.Enrich(activity, telemetry);
                ProxyWardTelemetry.RecordPolicyDecision(telemetry);
            }

            await EmitAuditAsync(
                context,
                policy,
                server,
                classification.ToolName,
                decisionResult.Decision,
                argumentSummary,
                parseResult.Messages.Count,
                parseResult.IsBatch ? message.BatchIndex : null,
                stopwatch.ElapsedMilliseconds,
                decisionResult.ArgumentOverrideApplied);

            evaluations.Add(new ToolPolicyEvaluation(message, classification.ToolName, decisionResult.Decision));
        }

        if (evaluations.Count == 0)
        {
            await next(context);
            return;
        }

        var blockedEvaluations = evaluations
            .Where(evaluation => evaluation.Decision.Type == PolicyDecisionType.Block)
            .ToArray();

        if (blockedEvaluations.Length > 0)
        {
            if (parseResult.IsBatch && policy.Inspection.BatchToolCalls == BatchToolCallBehavior.FailClosed)
            {
                await WriteBatchBlockResponseAsync(context, server, parseResult, evaluations, blockedEvaluations);
                return;
            }

            var blocked = blockedEvaluations[0];
            await WriteBlockResponseAsync(
                context,
                server,
                blocked.ToolName,
                blocked.Decision.Reasons,
                parseResult,
                blocked.Message);
            return;
        }

        await next(context);
    }

    private async Task<ToolPolicyDecisionResult> EvaluateToolCallAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        string? toolName,
        JsonRpcMessage message)
    {
        var toolDecision = evaluator.Evaluate(policy.Mode, server.Tools, toolName);
        if (toolDecision.Type != PolicyDecisionType.Allow)
        {
            return new ToolPolicyDecisionResult(toolDecision, ArgumentOverrideApplied: false);
        }

        var effectiveArguments = argumentOverrides.Resolve(server.Arguments, toolName);
        var argumentReasons = await EvaluateAllArgumentRulesAsync(
                context,
                policy,
                effectiveArguments,
                message.Params)
            .ConfigureAwait(false);
        var decision = argumentReasons.Count == 0
            ? toolDecision
            : policy.Mode.AsBlockDecision(argumentReasons);

        return new ToolPolicyDecisionResult(decision, effectiveArguments.OverrideApplied);
    }

    private async Task<IReadOnlyList<string>> EvaluateAllArgumentRulesAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ResolvedArgumentPolicy effectiveArguments,
        JsonNode? arguments)
    {
        var reasons = new List<string>();
        AddBlockingReasons(
            reasons,
            pathRules.Evaluate(policy.Mode, effectiveArguments.Paths, arguments));
        AddBlockingReasons(
            reasons,
            await hostRules
                .EvaluateAsync(policy.Mode, effectiveArguments.Hosts, arguments, context.RequestAborted)
                .ConfigureAwait(false));
        AddBlockingReasons(
            reasons,
            commandRules.Evaluate(policy.Mode, effectiveArguments.Commands, arguments));

        return reasons;
    }

    private static void AddBlockingReasons(List<string> reasons, PolicyDecision decision)
    {
        if (decision.Type != PolicyDecisionType.Allow)
        {
            reasons.AddRange(decision.Reasons);
        }
    }

    private async Task EmitAuditAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        string? toolName,
        PolicyDecision decision,
        System.Text.Json.Nodes.JsonNode? argumentSummary,
        int batchSize,
        int? batchIndex,
        long durationMs,
        bool argumentOverrideApplied)
    {
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "tool_call_policy",
            Mode: FormatMode(policy.Mode),
            Decision: ToAuditDecision(decision.Type),
            ServerId: server.Id,
            Method: "tools/call",
            ToolName: toolName,
            Reasons: decision.Reasons,
            PolicyVersion: policy.VersionHash,
            CorrelationId: ResolveCorrelationId(context),
            RequestBytes: context.Request.ContentLength ?? 0,
            DurationMs: durationMs,
            ArgumentSummary: argumentSummary,
            BatchSize: batchSize,
            BatchIndex: batchIndex,
            ArgumentOverrideApplied: argumentOverrideApplied);

        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.AuditWriteActivity,
            CreateTelemetryMetadata(
                context,
                policy,
                server,
                toolName,
                FormatDecision(decision.Type),
                decision.Reasons,
                eventType: auditEvent.EventType,
                argumentSummary: argumentSummary));

        try
        {
            await auditSink.WriteAsync(auditEvent, context.RequestAborted);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "audit_sink_failure");
            ProxyWardTelemetry.RecordAuditSinkFailure(CreateTelemetryMetadata(
                context,
                policy,
                server,
                toolName,
                FormatDecision(decision.Type),
                decision.Reasons,
                eventType: auditEvent.EventType));
            logger.LogWarning(
                AuditSinkFailureEvent,
                ex,
                "ProxyWard audit sink failed to record tool_call_policy event for server {ServerId} tool {ToolName}.",
                server.Id,
                toolName ?? string.Empty);
        }
    }

    private static async Task WriteBlockResponseAsync(
        HttpContext context,
        ServerPolicy server,
        string? toolName,
        IReadOnlyCollection<string> reasons,
        JsonRpcParseResult parseResult,
        JsonRpcMessage message)
    {
        if (CanWriteJsonRpcError(parseResult, message))
        {
            await WriteJsonRpcErrorAsync(context, message.Id!, reasons);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Invalid JSON-RPC request.",
            decision = "block",
            serverId = server.Id,
            toolName,
            reasons
        });
    }

    private static async Task WriteBatchBlockResponseAsync(
        HttpContext context,
        ServerPolicy server,
        JsonRpcParseResult parseResult,
        IReadOnlyCollection<ToolPolicyEvaluation> evaluations,
        IReadOnlyCollection<ToolPolicyEvaluation> blockedEvaluations)
    {
        var evaluationByBatchIndex = evaluations.ToDictionary(
            evaluation => evaluation.Message.BatchIndex,
            evaluation => evaluation);

        var responses = new JsonArray();

        foreach (var message in parseResult.Messages)
        {
            if (message.Id is null || !JsonRpcPolicyError.HasSupportedRequestId(message.Id))
            {
                continue;
            }

            evaluationByBatchIndex.TryGetValue(message.BatchIndex, out var evaluation);
            var reasons = evaluation?.Decision.Type == PolicyDecisionType.Block
                ? evaluation.Decision.Reasons
                : [PolicyReasonCodes.BatchBlocked];

            responses.Add(JsonRpcPolicyError.Create(
                message.Id,
                reasons,
                "MCP ProxyWard blocked this batch",
                message.BatchIndex,
                evaluation?.ToolName));
        }

        if (responses.Count > 0)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = MediaTypeNames.Application.Json;
            await context.Response.WriteAsync(responses.ToJsonString(), context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "JSON-RPC batch blocked by policy.",
            decision = "block",
            serverId = server.Id,
            batchSize = parseResult.Messages.Count,
            reasons = blockedEvaluations
                .SelectMany(evaluation => evaluation.Decision.Reasons)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            messageDecisions = evaluations.Select(evaluation => new
            {
                batchIndex = evaluation.Message.BatchIndex,
                toolName = evaluation.ToolName,
                decision = FormatDecision(evaluation.Decision.Type),
                reasons = evaluation.Decision.Reasons
            }).ToArray()
        });
    }

    private static bool CanWriteJsonRpcError(JsonRpcParseResult parseResult, JsonRpcMessage message) =>
        !parseResult.IsBatch
        && string.Equals(message.JsonRpc, "2.0", StringComparison.Ordinal)
        && string.Equals(message.Method, "tools/call", StringComparison.Ordinal)
        && message.Id is not null
        && JsonRpcPolicyError.HasSupportedRequestId(message.Id);

    private static async Task WriteJsonRpcErrorAsync(
        HttpContext context,
        JsonNode id,
        IReadOnlyCollection<string> reasons)
    {
        var response = JsonRpcPolicyError.Create(
            id,
            reasons,
            "MCP ProxyWard blocked this tool call",
            batchIndex: null,
            toolName: null);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);
    }

    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[AuditItems.CorrelationId] as string ?? context.TraceIdentifier;

    private ProxyWardPolicy ResolvePolicySnapshot(HttpContext context) =>
        context.Items.TryGetValue(ServerResolutionItems.PolicySnapshot, out var snapshot)
            && snapshot is ProxyWardPolicy policy
                ? policy
                : policyProvider.Current;

    private TelemetryMetadata CreateTelemetryMetadata(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        string? toolName,
        string? decision = null,
        IReadOnlyCollection<string>? reasons = null,
        string? eventType = null,
        JsonNode? argumentSummary = null) =>
        new(
            CorrelationId: ResolveCorrelationId(context),
            ServerId: server.Id,
            Method: "tools/call",
            ToolName: toolName,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            AuditEventType: eventType,
            ArgumentSummary: FormatArgumentSummary(argumentSummary));

    private static string? FormatArgumentSummary(JsonNode? argumentSummary) =>
        argumentSummary?.ToJsonString();

    private static SecretRedactionOptions CreateSecretRedactionOptions(ServerPolicy server) =>
        new(
            RedactInLogs: server.Secrets.RedactInLogs,
            Patterns: server.Secrets.Patterns);

    private static AuditDecision ToAuditDecision(PolicyDecisionType type) =>
        type switch
        {
            PolicyDecisionType.Block => AuditDecision.Block,
            PolicyDecisionType.WouldBlock => AuditDecision.WouldBlock,
            PolicyDecisionType.Warn => AuditDecision.Warn,
            _ => AuditDecision.Allow
        };

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string FormatDecision(PolicyDecisionType type) =>
        type switch
        {
            PolicyDecisionType.Block => "block",
            PolicyDecisionType.WouldBlock => "would_block",
            PolicyDecisionType.Warn => "warn",
            _ => "allow"
        };

    private sealed record ToolPolicyEvaluation(
        JsonRpcMessage Message,
        string? ToolName,
        PolicyDecision Decision);

    private sealed record ToolPolicyDecisionResult(
        PolicyDecision Decision,
        bool ArgumentOverrideApplied);
}

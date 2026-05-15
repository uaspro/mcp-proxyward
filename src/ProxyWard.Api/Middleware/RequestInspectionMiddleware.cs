using System.Diagnostics;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Redaction;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Proxy.Application.Runtime;

namespace ProxyWard.Api.Middleware;

public sealed class RequestInspectionMiddleware(
    RequestDelegate next,
    IProxyWardPolicyProvider policyProvider,
    IMcpMessageParser parser,
    IMcpMethodClassifier classifier,
    IRedactor redactor,
    IAuditSink auditSink,
    ILogger<RequestInspectionMiddleware> logger)
{
    private static readonly EventId RequestInspectionEvent = new(1101, "RequestInspection");
    private static readonly EventId AuditSinkFailureEvent = new(1201, "AuditSinkFailure");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.ContainsKey(ServerResolutionItems.ServerPolicy))
        {
            await next(context);
            return;
        }

        var server = (ServerPolicy)context.Items[ServerResolutionItems.ServerPolicy]!;
        var policy = ResolvePolicySnapshot(context);
        var stopwatch = Stopwatch.StartNew();
        var headerBytes = context.Request.ContentLength ?? 0;
        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.RequestInspectionActivity,
            CreateTelemetryMetadata(context, policy, server));
        ProxyWardTelemetry.SetTag(activity, "request.bytes", headerBytes);

        if (!HasBody(context.Request))
        {
            stopwatch.Stop();
            ProxyWardTelemetry.Enrich(
                activity,
                CreateTelemetryMetadata(context, policy, server, decision: "allow", reasons: []));
            await EmitAuditAsync(
                context,
                policy,
                server,
                AuditDecision.Allow,
                method: null,
                toolName: null,
                argumentSummary: null,
                reasons: [],
                batchSize: 0,
                requestBytes: headerBytes,
                durationMs: stopwatch.ElapsedMilliseconds);
            await next(context);
            return;
        }

        var contentLength = context.Request.ContentLength;
        if (contentLength > policy.Inspection.MaxBodyBytes)
        {
            await HandleUnsupportedAsync(
                context,
                policy,
                server,
                stopwatch,
                activity,
                unsupportedKind: "body_too_large",
                blockStatusCode: StatusCodes.Status413PayloadTooLarge,
                requestBytes: contentLength.Value);
            return;
        }

        if (!HttpContentTypes.IsJson(context.Request.ContentType))
        {
            await HandleUnsupportedAsync(
                context,
                policy,
                server,
                stopwatch,
                activity,
                unsupportedKind: "unsupported_content_type",
                blockStatusCode: StatusCodes.Status415UnsupportedMediaType,
                requestBytes: contentLength ?? 0);
            return;
        }

        context.Request.EnableBuffering(bufferThreshold: Math.Min(policy.Inspection.MaxBodyBytes, 64 * 1024));

        using var inspectedBody = new MemoryStream(capacity: CreateInspectionBufferCapacity(contentLength, policy.Inspection.MaxBodyBytes));
        var readResult = await CopyRequestBodyWithinLimitAsync(
            context.Request.Body,
            inspectedBody,
            policy.Inspection.MaxBodyBytes,
            context.RequestAborted);
        context.Request.Body.Position = 0;

        if (!readResult.WithinLimit)
        {
            await HandleUnsupportedAsync(
                context,
                policy,
                server,
                stopwatch,
                activity,
                unsupportedKind: "body_too_large",
                blockStatusCode: StatusCodes.Status413PayloadTooLarge,
                requestBytes: readResult.BytesRead);
            return;
        }

        var inspectedBytes = inspectedBody.Length;
        var result = parser.Parse(GetWrittenMemory(inspectedBody), context.Request.ContentType);
        context.Items[RequestInspectionItems.JsonRpcParseResult] = result;

        var (method, toolName, argumentSummary, batchSize) = ExtractAuditMetadata(result, server);
        var (decision, reasons) = result.Status switch
        {
            JsonRpcParseStatus.Malformed => (AuditDecision.Warn, result.Reasons),
            JsonRpcParseStatus.UnsupportedContentType => (AuditDecision.Warn, (IReadOnlyCollection<string>)[PolicyReasonCodes.InspectionUnsupported]),
            _ => (AuditDecision.Allow, (IReadOnlyCollection<string>)[])
        };

        stopwatch.Stop();
        var telemetry = CreateTelemetryMetadata(
            context,
            policy,
            server,
            method,
            toolName,
            FormatAuditDecision(decision),
            reasons,
            argumentSummary: argumentSummary);
        ProxyWardTelemetry.Enrich(activity, telemetry);
        if (result.Status == JsonRpcParseStatus.Malformed)
        {
            ProxyWardTelemetry.RecordInspectionSkip(telemetry, "request", "json_malformed");
        }
        else if (result.Status == JsonRpcParseStatus.UnsupportedContentType)
        {
            ProxyWardTelemetry.RecordInspectionSkip(telemetry, "request", "unsupported_content_type");
        }

        if (ShouldEmitAudit(result, decision))
        {
            await EmitAuditAsync(
                context,
                policy,
                server,
                decision,
                method,
                toolName,
                argumentSummary,
                reasons,
                batchSize,
                requestBytes: inspectedBytes,
                durationMs: stopwatch.ElapsedMilliseconds);
        }

        await next(context);
    }

    private bool ShouldEmitAudit(JsonRpcParseResult result, AuditDecision decision) =>
        decision != AuditDecision.Allow || ContainsMessageWithoutDedicatedAudit(result);

    private bool ContainsMessageWithoutDedicatedAudit(JsonRpcParseResult result) =>
        result.Status != JsonRpcParseStatus.Parsed
        || result.Messages.Count == 0
        || result.Messages.Any(message =>
        {
            var classification = classifier.Classify(message);
            return classification.Kind is not McpMessageKind.ToolCall and not McpMessageKind.ToolsList;
        });

    private (string? Method, string? ToolName, System.Text.Json.Nodes.JsonNode? Summary, int BatchSize) ExtractAuditMetadata(
        JsonRpcParseResult result,
        ServerPolicy server)
    {
        if (result.Status != JsonRpcParseStatus.Parsed || result.Messages.Count == 0)
        {
            return (Method: null, ToolName: null, Summary: null, BatchSize: 0);
        }

        var first = result.Messages[0];
        var classification = classifier.Classify(first);
        var redacted = redactor.Redact("params", first.Params, CreateSecretRedactionOptions(server));
        return (Method: first.Method, ToolName: classification.ToolName, Summary: redacted.Value, BatchSize: result.Messages.Count);
    }

    private async Task HandleUnsupportedAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        Stopwatch stopwatch,
        Activity? activity,
        string unsupportedKind,
        int blockStatusCode,
        long requestBytes)
    {
        var decision = GetUnsupportedDecision(policy);
        var reasons = (IReadOnlyCollection<string>)[PolicyReasonCodes.InspectionUnsupported];

        if (decision is not UnsupportedInspectionDecision.PassThrough)
        {
            LogUnsupported(context, policy, unsupportedKind, decision, requestBytes);
        }

        var auditDecision = decision switch
        {
            UnsupportedInspectionDecision.Block => AuditDecision.Block,
            UnsupportedInspectionDecision.WouldBlock => AuditDecision.WouldBlock,
            UnsupportedInspectionDecision.Warn => AuditDecision.Warn,
            _ => AuditDecision.Allow
        };

        stopwatch.Stop();
        var telemetry = CreateTelemetryMetadata(
            context,
            policy,
            server,
            decision: FormatAuditDecision(auditDecision),
            reasons: reasons);
        ProxyWardTelemetry.Enrich(activity, telemetry);
        ProxyWardTelemetry.RecordInspectionSkip(telemetry, "request", unsupportedKind);

        await EmitAuditAsync(
            context,
            policy,
            server,
            auditDecision,
            method: null,
            toolName: null,
            argumentSummary: null,
            reasons: reasons,
            batchSize: 0,
            requestBytes: requestBytes,
            durationMs: stopwatch.ElapsedMilliseconds);

        if (decision is not UnsupportedInspectionDecision.Block)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = blockStatusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "MCP request inspection is unsupported by policy.",
            decision = "block",
            reasons = new[] { PolicyReasonCodes.InspectionUnsupported }
        });
    }

    private async Task EmitAuditAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        AuditDecision decision,
        string? method,
        string? toolName,
        System.Text.Json.Nodes.JsonNode? argumentSummary,
        IReadOnlyCollection<string> reasons,
        int batchSize,
        long requestBytes,
        long durationMs)
    {
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "request_inspection",
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            ServerId: server.Id,
            Method: method,
            ToolName: toolName,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            CorrelationId: ResolveCorrelationId(context),
            RequestBytes: requestBytes,
            DurationMs: durationMs,
            ArgumentSummary: argumentSummary,
            BatchSize: batchSize);

        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.AuditWriteActivity,
            CreateTelemetryMetadata(
                context,
                policy,
                server,
                method,
                toolName,
                FormatAuditDecision(decision),
                reasons,
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
                method,
                toolName,
                FormatAuditDecision(decision),
                reasons,
                eventType: auditEvent.EventType));
            logger.LogWarning(
                AuditSinkFailureEvent,
                ex,
                "ProxyWard audit sink failed to record request_inspection event for server {ServerId}.",
                server.Id);
        }
    }

    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[AuditItems.CorrelationId] as string ?? context.TraceIdentifier;

    private ProxyWardPolicy ResolvePolicySnapshot(HttpContext context) =>
        context.Items.TryGetValue(ServerResolutionItems.PolicySnapshot, out var snapshot)
            && snapshot is ProxyWardPolicy policy
                ? policy
                : policyProvider.Current;

    private UnsupportedInspectionDecision GetUnsupportedDecision(ProxyWardPolicy policy) =>
        policy.Inspection.UnsupportedStreaming switch
        {
            UnsupportedInspectionBehavior.Block when policy.Mode == ProxyWardMode.Enforce =>
                UnsupportedInspectionDecision.Block,
            UnsupportedInspectionBehavior.Block =>
                UnsupportedInspectionDecision.WouldBlock,
            UnsupportedInspectionBehavior.PassThrough =>
                UnsupportedInspectionDecision.PassThrough,
            _ => UnsupportedInspectionDecision.Warn
        };

    private void LogUnsupported(
        HttpContext context,
        ProxyWardPolicy policy,
        string unsupportedKind,
        UnsupportedInspectionDecision decision,
        long requestBytes)
    {
        logger.LogWarning(
            RequestInspectionEvent,
            "ProxyWard inspection event {EventType}: {Decision} for {UnsupportedKind} with reasons {Reasons}; content type {ContentType}; request bytes {RequestBytes}; mode {Mode}; policy {PolicyVersion}; service {ServiceName}; correlation {CorrelationId}",
            "request_inspection",
            FormatDecision(decision),
            unsupportedKind,
            PolicyReasonCodes.InspectionUnsupported,
            HttpContentTypes.Sanitize(context.Request.ContentType),
            requestBytes,
            FormatMode(policy.Mode),
            policy.VersionHash,
            ProxyWardTelemetry.ServiceName,
            ResolveCorrelationId(context));
    }

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding");

    private static ReadOnlyMemory<byte> GetWrittenMemory(MemoryStream stream) =>
        stream.TryGetBuffer(out var buffer)
            ? buffer.AsMemory(0, (int)stream.Length)
            : stream.ToArray();

    private static int CreateInspectionBufferCapacity(long? contentLength, int maxBodyBytes)
    {
        if (contentLength is > 0 and <= int.MaxValue)
        {
            return (int)Math.Min(contentLength.Value, maxBodyBytes);
        }

        return Math.Min(maxBodyBytes, 64 * 1024);
    }

    private static async Task<BoundedBodyReadResult> CopyRequestBodyWithinLimitAsync(
        Stream source,
        MemoryStream destination,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(maxBodyBytes, 81920)];
        long bytesRead = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return new BoundedBodyReadResult(bytesRead, WithinLimit: true);
            }

            bytesRead += read;
            if (bytesRead > maxBodyBytes)
            {
                return new BoundedBodyReadResult(bytesRead, WithinLimit: false);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string FormatDecision(UnsupportedInspectionDecision decision) =>
        decision switch
        {
            UnsupportedInspectionDecision.Block => "block",
            UnsupportedInspectionDecision.WouldBlock => "would_block",
            UnsupportedInspectionDecision.Warn => "warn",
            _ => "pass_through"
        };

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private TelemetryMetadata CreateTelemetryMetadata(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        string? method = null,
        string? toolName = null,
        string? decision = null,
        IReadOnlyCollection<string>? reasons = null,
        string? eventType = null,
        System.Text.Json.Nodes.JsonNode? argumentSummary = null) =>
        new(
            CorrelationId: ResolveCorrelationId(context),
            ServerId: server.Id,
            Method: method,
            ToolName: toolName,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            AuditEventType: eventType,
            ArgumentSummary: FormatArgumentSummary(argumentSummary));

    private static string? FormatArgumentSummary(System.Text.Json.Nodes.JsonNode? argumentSummary) =>
        argumentSummary?.ToJsonString();

    private static SecretRedactionOptions CreateSecretRedactionOptions(ServerPolicy server) =>
        new(
            RedactInLogs: server.Secrets.RedactInLogs,
            Patterns: server.Secrets.Patterns);

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };

    private enum UnsupportedInspectionDecision
    {
        PassThrough,
        Warn,
        WouldBlock,
        Block
    }

    private sealed record BoundedBodyReadResult(long BytesRead, bool WithinLimit);
}

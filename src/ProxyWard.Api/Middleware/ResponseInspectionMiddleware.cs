using System.Diagnostics;
using System.Text.Json.Nodes;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Core.Policies;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Middleware;

public sealed class ResponseInspectionMiddleware(
    RequestDelegate next,
    ProxyWardPolicy policy,
    IMcpMethodClassifier classifier,
    IToolDefinitionExtractor extractor,
    ToolSurfaceDriftEvaluator driftEvaluator,
    IAuditSink auditSink,
    ILogger<ResponseInspectionMiddleware> logger)
{
    private const string DefaultMcpProtocol = "2025-11-25";

    private static readonly EventId ResponseInspectionEvent = new(1301, "ResponseInspection");
    private static readonly EventId AuditSinkFailureEvent = new(1302, "AuditSinkFailure");
    private static readonly EventId SchemaLockWriteFailureEvent = new(1303, "SchemaLockWriteFailure");
    private static readonly EventId SchemaLockUpstreamChangedEvent = new(1304, "SchemaLockUpstreamChanged");
    private static readonly EventId SchemaDriftEvent = new(1305, "SchemaDrift");

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServerResolutionItems.ServerPolicy, out var serverItem)
            || serverItem is not ServerPolicy server)
        {
            await next(context);
            return;
        }

        var method = ResolveFirstMcpMethod(context);
        if (!ShouldInspectToolsListResponse(context))
        {
            using var proxyActivity = ProxyWardTelemetry.StartActivity(
                ProxyWardTelemetry.YarpProxyActivity,
                CreateTelemetryMetadata(context, server, method));
            await next(context);
            ProxyWardTelemetry.SetTag(proxyActivity, ProxyWardTelemetry.HttpStatusCodeTag, context.Response.StatusCode);
            ProxyWardTelemetry.RecordProxiedRequest(
                CreateTelemetryMetadata(context, server, method),
                context.Response.StatusCode);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new ResponseInspectionStream(
            originalBody,
            context.Response,
            policy.Inspection.MaxBodyBytes,
            ShouldBlockUnsupported());

        context.Response.Body = capture;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var proxyActivity = ProxyWardTelemetry.StartActivity(
                ProxyWardTelemetry.YarpProxyActivity,
                CreateTelemetryMetadata(context, server, method));
            await next(context);
            ProxyWardTelemetry.SetTag(proxyActivity, ProxyWardTelemetry.HttpStatusCodeTag, context.Response.StatusCode);
            ProxyWardTelemetry.RecordProxiedRequest(
                CreateTelemetryMetadata(context, server, method),
                context.Response.StatusCode);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        stopwatch.Stop();

        if (capture.UnsupportedReason is not null)
        {
            await HandleUnsupportedAsync(
                context,
                server,
                capture.UnsupportedReason!,
                capture.ObservedBytes,
                stopwatch.ElapsedMilliseconds,
                capture);
            return;
        }

        var body = capture.GetBufferedBody();
        var result = extractor.Extract(body);
        if (!result.Success)
        {
            await EmitAuditAsync(
                context,
                server,
                AuditDecision.Warn,
                result.Reasons.Count == 0 ? [PolicyReasonCodes.InspectionUnsupported] : result.Reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary([]));

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        var driftResult = await EvaluateDriftAsync(context, server, result.Tools);

        if (driftResult.Skipped)
        {
            LogSchemaWriteFailure(server, driftResult.WriteFailure!.Reason);
            ProxyWardTelemetry.RecordSchemaWriteFailed(server.Id, driftResult.WriteFailure.Reason);

            await EmitAuditAsync(
                context,
                server,
                AuditDecision.Allow,
                [],
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult));

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        if (driftResult.UpstreamChanged)
        {
            LogSchemaUpstreamChanged(
                server,
                driftResult.PreviousUpstreamUrl!,
                driftResult.CurrentUpstreamUrl!,
                driftResult.Version);
            ProxyWardTelemetry.RecordSchemaUpstreamChanged(
                server.Id,
                driftResult.PreviousUpstreamUrl!,
                driftResult.CurrentUpstreamUrl!);
        }

        if (driftResult.HasDrift)
        {
            var decision = policy.Mode == ProxyWardMode.Enforce
                ? AuditDecision.Block
                : AuditDecision.Warn;
            var telemetry = CreateTelemetryMetadata(
                context,
                server,
                method,
                FormatAuditDecision(decision),
                driftResult.Reasons,
                schemaVersion: driftResult.Version);
            LogSchemaDrift(server, decision, driftResult);
            ProxyWardTelemetry.RecordSchemaDrift(telemetry);

            await EmitAuditAsync(
                context,
                server,
                decision,
                driftResult.Reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult));

            if (decision == AuditDecision.Block)
            {
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "MCP tool surface drift detected.",
                    decision = "block",
                    reasons = driftResult.Reasons,
                    tools = driftResult.Drifts.Select(drift => new
                    {
                        name = drift.ToolName,
                        reasons = drift.Reasons
                    }).ToArray()
                }, context.RequestAborted);
                return;
            }

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        await EmitAuditAsync(
            context,
            server,
            AuditDecision.Allow,
            [],
            body.Length,
            stopwatch.ElapsedMilliseconds,
            CreateToolSummary(result.Tools, driftResult));

        await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
    }

    private bool ShouldInspectToolsListResponse(HttpContext context)
    {
        if (!context.Items.TryGetValue(RequestInspectionItems.JsonRpcParseResult, out var parseItem)
            || parseItem is not JsonRpcParseResult parseResult
            || parseResult.Status != JsonRpcParseStatus.Parsed)
        {
            return false;
        }

        if (!parseResult.Messages.Any(message => classifier.Classify(message).Kind == McpMessageKind.ToolsList))
        {
            return false;
        }

        return true;
    }

    private async Task HandleUnsupportedAsync(
        HttpContext context,
        ServerPolicy server,
        string unsupportedReason,
        long responseBytes,
        long durationMs,
        ResponseInspectionStream? capture = null)
    {
        var decision = GetUnsupportedDecision();
        var auditDecision = decision switch
        {
            UnsupportedInspectionDecision.Block => AuditDecision.Block,
            UnsupportedInspectionDecision.WouldBlock => AuditDecision.WouldBlock,
            UnsupportedInspectionDecision.Warn => AuditDecision.Warn,
            _ => AuditDecision.Allow
        };

        LogUnsupported(context, unsupportedReason, decision, responseBytes);
        var telemetry = CreateTelemetryMetadata(
            context,
            server,
            "tools/list",
            FormatAuditDecision(auditDecision),
            [PolicyReasonCodes.InspectionUnsupported]);
        ProxyWardTelemetry.RecordInspectionSkip(telemetry, "response", unsupportedReason);

        await EmitAuditAsync(
            context,
            server,
            auditDecision,
            [PolicyReasonCodes.InspectionUnsupported],
            responseBytes,
            durationMs,
            CreateUnsupportedSummary(unsupportedReason));

        if (capture?.WasPassedThrough == true)
        {
            return;
        }

        if (decision is not UnsupportedInspectionDecision.Block)
        {
            if (capture is not null)
            {
                await capture.CopyBufferedBodyToAsync(context.Response.Body, context.RequestAborted);
            }

            return;
        }

        context.Response.Headers.Clear();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "MCP response inspection is unsupported by policy.",
            decision = "block",
            reasons = new[] { PolicyReasonCodes.InspectionUnsupported }
        }, context.RequestAborted);
    }

    private async Task EmitAuditAsync(
        HttpContext context,
        ServerPolicy server,
        AuditDecision decision,
        IReadOnlyCollection<string> reasons,
        long responseBytes,
        long durationMs,
        JsonNode? summary)
    {
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "tools_list_response_inspection",
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            ServerId: server.Id,
            Method: "tools/list",
            ToolName: null,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            CorrelationId: ResolveCorrelationId(context),
            RequestBytes: responseBytes,
            DurationMs: durationMs,
            ArgumentSummary: summary,
            BatchSize: 0);

        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.AuditWriteActivity,
            CreateTelemetryMetadata(
                context,
                server,
                "tools/list",
                FormatAuditDecision(decision),
                reasons,
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
                "tools/list",
                FormatAuditDecision(decision),
                reasons,
                eventType: auditEvent.EventType));
            logger.LogWarning(
                AuditSinkFailureEvent,
                ex,
                "ProxyWard audit sink failed to record tools_list_response_inspection event for server {ServerId}.",
                server.Id);
        }
    }

    private async Task<ToolSurfaceDriftResult> EvaluateDriftAsync(
        HttpContext context,
        ServerPolicy server,
        IReadOnlyList<DiscoveredTool> tools)
    {
        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.SchemaLockCheckActivity,
            CreateTelemetryMetadata(context, server, "tools/list"));

        var result = await driftEvaluator.EvaluateAsync(
            serverId: server.Id,
            upstreamUrl: server.Upstream.ToString(),
            mcpProtocol: DefaultMcpProtocol,
            discoveredTools: tools,
            policyVersion: policy.VersionHash,
            sourceCorrelationId: ResolveCorrelationId(context),
            capturedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: context.RequestAborted);

        ProxyWardTelemetry.Enrich(
            activity,
            CreateTelemetryMetadata(
                context,
                server,
                "tools/list",
                result.HasDrift ? "warn" : "allow",
                result.Reasons,
                schemaVersion: result.Version > 0 ? result.Version : null));
        if (result.Version > 0)
        {
            ProxyWardTelemetry.SetTag(activity, ProxyWardTelemetry.SchemaVersionTag, result.Version);
        }

        return result;
    }

    private void LogSchemaWriteFailure(ServerPolicy server, string reason)
    {
        logger.LogWarning(
            SchemaLockWriteFailureEvent,
            "ProxyWard schema-lock write failed for server {ServerId} with reason {Reason}. Drift evaluation skipped for this response.",
            server.Id,
            reason);
    }

    private void LogSchemaUpstreamChanged(
        ServerPolicy server,
        string previousUrl,
        string currentUrl,
        int schemaVersion)
    {
        logger.LogWarning(
            SchemaLockUpstreamChangedEvent,
            "ProxyWard schema-lock upstream URL changed for server {ServerId}: {PreviousUrl} -> {CurrentUrl}. Existing schema version {SchemaVersion} retained.",
            server.Id,
            previousUrl,
            currentUrl,
            schemaVersion);
    }

    private void LogSchemaDrift(
        ServerPolicy server,
        AuditDecision decision,
        ToolSurfaceDriftResult driftResult)
    {
        logger.LogWarning(
            SchemaDriftEvent,
            "ProxyWard schema drift detected for server {ServerId} at schema version {SchemaVersion}; decision {Decision}; reasons {Reasons}.",
            server.Id,
            driftResult.Version,
            FormatAuditDecision(decision),
            string.Join(',', driftResult.Reasons));
    }

    private UnsupportedInspectionDecision GetUnsupportedDecision() =>
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

    private bool ShouldBlockUnsupported() =>
        GetUnsupportedDecision() is UnsupportedInspectionDecision.Block;

    private void LogUnsupported(
        HttpContext context,
        string unsupportedKind,
        UnsupportedInspectionDecision decision,
        long responseBytes)
    {
        logger.LogWarning(
            ResponseInspectionEvent,
            "ProxyWard inspection event {EventType}: {Decision} for {UnsupportedKind} with reasons {Reasons}; content type {ContentType}; response bytes {ResponseBytes}; mode {Mode}; policy {PolicyVersion}; service {ServiceName}; correlation {CorrelationId}",
            "tools_list_response_inspection",
            FormatDecision(decision),
            unsupportedKind,
            PolicyReasonCodes.InspectionUnsupported,
            SanitizeMediaType(context.Response.ContentType),
            responseBytes,
            FormatMode(policy.Mode),
            policy.VersionHash,
            ProxyWardTelemetry.ServiceName,
            ResolveCorrelationId(context));
    }

    private static JsonObject CreateToolSummary(
        IReadOnlyList<DiscoveredTool> tools,
        ToolSurfaceDriftResult? driftResult = null)
    {
        var summary = new JsonObject
        {
            ["toolCount"] = tools.Count,
            ["toolNames"] = new JsonArray(
                tools
                    .Select(tool => tool.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Order(StringComparer.Ordinal)
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray())
        };

        if (driftResult is not null)
        {
            if (driftResult.Version > 0)
            {
                summary["schemaVersion"] = driftResult.Version;
            }

            if (driftResult.Skipped)
            {
                summary["schemaLockSkipped"] = true;
                summary["schemaLockSkipReason"] = driftResult.WriteFailure!.Reason;
            }

            summary["driftedToolNames"] = new JsonArray(
                driftResult.Drifts
                    .Select(drift => drift.ToolName)
                    .Order(StringComparer.Ordinal)
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray());
        }

        return summary;
    }

    private static JsonObject CreateUnsupportedSummary(string unsupportedKind) =>
        new()
        {
            ["unsupportedKind"] = unsupportedKind
        };

    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[AuditItems.CorrelationId] as string ?? context.TraceIdentifier;

    private static string? ResolveFirstMcpMethod(HttpContext context)
    {
        if (!context.Items.TryGetValue(RequestInspectionItems.JsonRpcParseResult, out var parseItem)
            || parseItem is not JsonRpcParseResult parseResult
            || parseResult.Status != JsonRpcParseStatus.Parsed
            || parseResult.Messages.Count == 0)
        {
            return null;
        }

        return parseResult.Messages[0].Method;
    }

    private TelemetryMetadata CreateTelemetryMetadata(
        HttpContext context,
        ServerPolicy server,
        string? method = null,
        string? decision = null,
        IReadOnlyCollection<string>? reasons = null,
        string? eventType = null,
        int? schemaVersion = null) =>
        new(
            CorrelationId: ResolveCorrelationId(context),
            ServerId: server.Id,
            Method: method,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            SchemaVersion: schemaVersion,
            AuditEventType: eventType);

    private static string SanitizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = separatorIndex >= 0 ? contentType[..separatorIndex] : contentType;
        return mediaType.Trim();
    }

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };

    private static string FormatDecision(UnsupportedInspectionDecision decision) =>
        decision switch
        {
            UnsupportedInspectionDecision.Block => "block",
            UnsupportedInspectionDecision.WouldBlock => "would_block",
            UnsupportedInspectionDecision.Warn => "warn",
            _ => "pass_through"
        };

    private enum UnsupportedInspectionDecision
    {
        PassThrough,
        Warn,
        WouldBlock,
        Block
    }

    private sealed class ResponseInspectionStream(
        Stream passthrough,
        HttpResponse response,
        int maxBodyBytes,
        bool blockUnsupported) : Stream
    {
        private readonly MemoryStream _buffer = new(capacity: Math.Min(maxBodyBytes, 64 * 1024));
        private CaptureMode _mode = CaptureMode.Undecided;

        public string? UnsupportedReason { get; private set; }
        public long ObservedBytes { get; private set; }
        public bool WasPassedThrough => _mode == CaptureMode.PassThrough;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] GetBufferedBody() => _buffer.ToArray();

        public async Task CopyBufferedBodyToAsync(Stream destination, CancellationToken cancellationToken)
        {
            _buffer.Position = 0;
            await _buffer.CopyToAsync(destination, cancellationToken);
        }

        public override void Flush()
        {
            if (_mode == CaptureMode.PassThrough)
            {
                passthrough.Flush();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _mode == CaptureMode.PassThrough
                ? passthrough.FlushAsync(cancellationToken)
                : Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            DecideIfNeeded();
            ObservedBytes += buffer.Length;

            switch (_mode)
            {
                case CaptureMode.PassThrough:
                    await passthrough.WriteAsync(buffer, cancellationToken);
                    break;
                case CaptureMode.Buffer:
                    if (_buffer.Length + buffer.Length <= maxBodyBytes)
                    {
                        await _buffer.WriteAsync(buffer, cancellationToken);
                        break;
                    }

                    UnsupportedReason = "response_too_large";
                    if (blockUnsupported)
                    {
                        _buffer.SetLength(0);
                        _mode = CaptureMode.Discard;
                        break;
                    }

                    _buffer.Position = 0;
                    await _buffer.CopyToAsync(passthrough, cancellationToken);
                    _buffer.SetLength(0);
                    _mode = CaptureMode.PassThrough;
                    await passthrough.WriteAsync(buffer, cancellationToken);
                    break;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private void DecideIfNeeded()
        {
            if (_mode != CaptureMode.Undecided)
            {
                return;
            }

            var contentLength = response.ContentLength;
            if (IsStreamingContentType(response.ContentType))
            {
                SetUnsupported("streaming_response");
                return;
            }

            if (!IsJsonContentType(response.ContentType))
            {
                SetUnsupported("unsupported_content_type");
                return;
            }

            if (contentLength is null)
            {
                SetUnsupported("unknown_size");
                return;
            }

            if (contentLength > maxBodyBytes)
            {
                ObservedBytes = contentLength.Value;
                SetUnsupported("response_too_large");
                return;
            }

            _mode = CaptureMode.Buffer;
        }

        private void SetUnsupported(string reason)
        {
            UnsupportedReason = reason;
            _mode = blockUnsupported ? CaptureMode.Discard : CaptureMode.PassThrough;
        }

        private static bool IsStreamingContentType(string? contentType) =>
            SanitizeMediaType(contentType).Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);

        private static bool IsJsonContentType(string? contentType)
        {
            var mediaType = SanitizeMediaType(contentType);
            return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        }

        private enum CaptureMode
        {
            Undecided,
            Buffer,
            PassThrough,
            Discard
        }
    }
}

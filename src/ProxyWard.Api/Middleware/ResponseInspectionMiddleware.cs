using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Api.Observability;
using ProxyWard.Api.Runtime;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Redaction;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Core.Policies;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Middleware;

public sealed class ResponseInspectionMiddleware(
    RequestDelegate next,
    IProxyWardPolicyProvider policyProvider,
    ResponseInspectionTargetResolver targetResolver,
    IToolDefinitionExtractor extractor,
    ToolSurfaceDriftEvaluator driftEvaluator,
    ResponseInspectionDriftReviewCoordinator driftReviews,
    IAuditSink auditSink,
    ILogger<ResponseInspectionMiddleware> logger)
{
    private const string DefaultMcpProtocol = "2025-11-25";
    private const int MaxDetailedDriftReviewsInAudit = 20;
    private const int MaxDriftReviewIdsInAudit = 20;

    private static readonly TimeSpan ToolsListInspectionCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly EventId ResponseInspectionEvent = new(1301, "ResponseInspection");
    private static readonly EventId AuditSinkFailureEvent = new(1302, "AuditSinkFailure");
    private static readonly EventId SchemaLockWriteFailureEvent = new(1303, "SchemaLockWriteFailure");
    private static readonly EventId SchemaLockUpstreamChangedEvent = new(1304, "SchemaLockUpstreamChanged");
    private static readonly EventId SchemaDriftEvent = new(1305, "SchemaDrift");

    private readonly ConcurrentDictionary<string, ToolsListInspectionCacheEntry> _toolsListInspectionCache = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Items.TryGetValue(ServerResolutionItems.ServerPolicy, out var serverItem)
            || serverItem is not ServerPolicy server)
        {
            await next(context);
            return;
        }

        var policy = ResolvePolicySnapshot(context);
        var fallbackMethod = ResolveFirstMcpMethod(context);
        var target = targetResolver.Resolve(context, server);
        if (target.Kind == ResponseInspectionKind.None)
        {
            using var proxyActivity = ProxyWardTelemetry.StartActivity(
                ProxyWardTelemetry.YarpProxyActivity,
                CreateTelemetryMetadata(context, policy, server, fallbackMethod));
            await next(context);
            ProxyWardTelemetry.SetTag(proxyActivity, ProxyWardTelemetry.HttpStatusCodeTag, context.Response.StatusCode);
            ProxyWardTelemetry.RecordProxiedRequest(
                CreateTelemetryMetadata(context, policy, server, fallbackMethod),
                context.Response.StatusCode);
            return;
        }

        var originalBody = context.Response.Body;
        await using var capture = new ResponseInspectionStream(
            originalBody,
            context.Response,
            policy.Inspection.MaxBodyBytes,
            ShouldBlockUnsupported(policy));

        context.Response.Body = capture;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var proxyActivity = ProxyWardTelemetry.StartActivity(
                ProxyWardTelemetry.YarpProxyActivity,
                CreateTelemetryMetadata(context, policy, server, target.Method, toolName: target.ToolName));
            await next(context);
            ProxyWardTelemetry.SetTag(proxyActivity, ProxyWardTelemetry.HttpStatusCodeTag, context.Response.StatusCode);
            ProxyWardTelemetry.RecordProxiedRequest(
                CreateTelemetryMetadata(context, policy, server, target.Method, toolName: target.ToolName),
                context.Response.StatusCode);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (capture.UnsupportedReason is not null)
        {
            await HandleUnsupportedAsync(
                context,
                policy,
                server,
                target,
                capture.UnsupportedReason!,
                capture.ObservedBytes,
                stopwatch.ElapsedMilliseconds,
                capture);
            return;
        }

        var body = capture.GetBufferedBody();
        if (target.Kind == ResponseInspectionKind.ToolCallSecretReturn)
        {
            await HandleToolCallSecretResponseAsync(
                context,
                policy,
                server,
                target,
                body,
                stopwatch,
                capture,
                originalBody);
            return;
        }

        var toolsListCacheKey = CreateToolsListInspectionCacheKey(server, policy, context.Response.ContentType, body);
        ToolListExtractionResult result;
        ToolSurfaceDriftResult driftResult;
        if (_toolsListInspectionCache.TryGetValue(toolsListCacheKey, out var cachedInspection)
            && DateTimeOffset.UtcNow - cachedInspection.CachedAtUtc <= ToolsListInspectionCacheTtl)
        {
            result = ToolListExtractionResult.Extracted(cachedInspection.Tools);
            driftResult = cachedInspection.DriftResult;
        }
        else
        {
            result = ExtractToolsListResponse(
                body,
                context.Response.ContentType,
                context.Response.Headers["Content-Encoding"].ToString(),
                policy.Inspection.MaxBodyBytes);
            if (result.Skipped)
            {
                await EmitAuditAsync(
                    context,
                    policy,
                    server,
                    ResponseInspectionEventTypes.ToolsListResponseInspection,
                    "tools/list",
                    null,
                    AuditDecision.Allow,
                    [],
                    body.Length,
                    stopwatch.ElapsedMilliseconds,
                    CreateToolSummary([], inspectionSkipReason: result.SkipReason));

                await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
                return;
            }

            if (!result.Success)
            {
                await EmitAuditAsync(
                    context,
                    policy,
                    server,
                    ResponseInspectionEventTypes.ToolsListResponseInspection,
                    "tools/list",
                    null,
                    AuditDecision.Warn,
                    result.Reasons.Count == 0 ? [PolicyReasonCodes.InspectionUnsupported] : result.Reasons,
                    body.Length,
                    stopwatch.ElapsedMilliseconds,
                    CreateToolSummary([]));

                await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
                return;
            }

            driftResult = await EvaluateDriftAsync(context, policy, server, result.Tools);
            if (!driftResult.Skipped && !driftResult.HasDrift)
            {
                StoreToolsListInspectionCache(toolsListCacheKey, result.Tools, driftResult);
            }
        }

        if (driftResult.Skipped)
        {
            LogSchemaWriteFailure(server, driftResult.WriteFailure!.Reason);
            ProxyWardTelemetry.RecordSchemaWriteFailed(server.Id, driftResult.WriteFailure.Reason);

            await EmitAuditAsync(
                context,
                policy,
                server,
                ResponseInspectionEventTypes.ToolsListResponseInspection,
                "tools/list",
                null,
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
            var driftReviewResults = await driftReviews.RecordDriftReviewsAsync(context, policy, server, driftResult);
            await driftReviews.RecordDiffMetadataAsync(context, server, driftResult, driftReviewResults);
            if (driftResult.WasNewVersion)
            {
                StoreToolsListInspectionCache(toolsListCacheKey, result.Tools, driftResult);
            }

            var decision = policy.Mode == ProxyWardMode.Enforce
                ? AuditDecision.Block
                : AuditDecision.Warn;
            var telemetry = CreateTelemetryMetadata(
                context,
                policy,
                server,
                target.Method,
                FormatAuditDecision(decision),
                driftResult.Reasons,
                schemaVersion: driftResult.Version);
            LogSchemaDrift(server, decision, driftResult);
            ProxyWardTelemetry.RecordSchemaDrift(telemetry);

            await EmitAuditAsync(
                context,
                policy,
                server,
                ResponseInspectionEventTypes.ToolsListResponseInspection,
                "tools/list",
                null,
                decision,
                driftResult.Reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult, driftReviewResults));

            if (decision == AuditDecision.Block)
            {
                var blockedToolNames = ToolsListResponseFilter.CreateBlockedToolNameSet(
                    driftResult.Drifts.Select(drift => drift.ToolName));
                if (ToolsListResponseFilter.TryCreateFilteredBody(
                    body,
                    context.Response.ContentType,
                    context.Response.Headers["Content-Encoding"].ToString(),
                    policy.Inspection.MaxBodyBytes,
                    blockedToolNames,
                    out var filteredBody))
                {
                    await WriteFilteredToolsListAsync(context, originalBody, filteredBody);
                    return;
                }

                await WriteUnfilterableToolsListBlockAsync(
                    context,
                    "MCP tool surface drift detected.",
                    driftResult.Reasons,
                    driftResult.Drifts.Select(drift => new
                    {
                        name = drift.ToolName,
                        reasons = drift.Reasons
                    }).ToArray());
                return;
            }

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        var currentUnapprovedReviews = await driftReviews.GetCurrentUnapprovedDriftReviewsAsync(
            context,
            server,
            driftResult);
        if (currentUnapprovedReviews.Count > 0)
        {
            var reasons = currentUnapprovedReviews
                .SelectMany(review => review.Row.Reasons)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var decision = policy.Mode == ProxyWardMode.Enforce
                ? AuditDecision.Block
                : AuditDecision.Warn;
            var telemetry = CreateTelemetryMetadata(
                context,
                policy,
                server,
                target.Method,
                FormatAuditDecision(decision),
                reasons,
                schemaVersion: driftResult.Version);

            driftReviews.LogSchemaDriftReviewState(server, decision, driftResult.Version, currentUnapprovedReviews);
            ProxyWardTelemetry.RecordSchemaDrift(telemetry);

            await EmitAuditAsync(
                context,
                policy,
                server,
                ResponseInspectionEventTypes.ToolsListResponseInspection,
                "tools/list",
                null,
                decision,
                reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult, currentUnapprovedReviews));

            if (decision == AuditDecision.Block)
            {
                var blockedToolNames = ToolsListResponseFilter.CreateBlockedToolNameSet(
                    currentUnapprovedReviews.Select(review => review.Row.ToolName));
                if (ToolsListResponseFilter.TryCreateFilteredBody(
                    body,
                    context.Response.ContentType,
                    context.Response.Headers["Content-Encoding"].ToString(),
                    policy.Inspection.MaxBodyBytes,
                    blockedToolNames,
                    out var filteredBody))
                {
                    await WriteFilteredToolsListAsync(context, originalBody, filteredBody);
                    return;
                }

                await WriteUnfilterableToolsListBlockAsync(
                    context,
                    "MCP tool surface drift review is not approved.",
                    reasons,
                    currentUnapprovedReviews
                        .GroupBy(review => review.Row.ToolName, StringComparer.Ordinal)
                        .Select(group => new
                        {
                            name = group.Key,
                            statuses = group
                                .Select(review => FormatDriftReviewStatus(review.Row.Status))
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                                .ToArray(),
                            reasons = group
                                .SelectMany(review => review.Row.Reasons)
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)
                                .ToArray()
                        })
                        .OrderBy(tool => tool.name, StringComparer.Ordinal)
                        .ToArray());
                return;
            }

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        await EmitAuditAsync(
            context,
            policy,
            server,
            ResponseInspectionEventTypes.ToolsListResponseInspection,
            "tools/list",
            null,
            AuditDecision.Allow,
            [],
            body.Length,
            stopwatch.ElapsedMilliseconds,
            CreateToolSummary(result.Tools, driftResult));

        await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
    }

    private ToolListExtractionResult ExtractToolsListResponse(
        byte[] body,
        string? contentType,
        string? contentEncoding,
        int maxDecodedBytes)
    {
        return ResponseBodyDecoder.TryDecode(body, contentEncoding, maxDecodedBytes, out var decodedBody)
            ? extractor.Extract(decodedBody, contentType)
            : ToolListExtractionResult.Failed(PolicyReasonCodes.InspectionUnsupported);
    }

    private static async Task WriteFilteredToolsListAsync(
        HttpContext context,
        Stream destination,
        byte[] body)
    {
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Content-MD5");

        await destination.WriteAsync(body, context.RequestAborted);
    }

    private static async Task WriteUnfilterableToolsListBlockAsync(
        HttpContext context,
        string error,
        IReadOnlyCollection<string> reasons,
        object tools)
    {
        context.Response.Headers.Clear();
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new
        {
            error,
            decision = "block",
            reasons,
            tools
        }, context.RequestAborted);
    }

    private async Task HandleToolCallSecretResponseAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        ResponseInspectionTarget target,
        byte[] body,
        Stopwatch stopwatch,
        ResponseInspectionStream capture,
        Stream originalBody)
    {
        var matchResult = MatchToolResponseSecrets(server, body);
        if (!matchResult.WasMatched)
        {
            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        var decision = policy.Mode == ProxyWardMode.Enforce
            ? AuditDecision.Block
            : AuditDecision.WouldBlock;
        var reasons = (IReadOnlyCollection<string>)[PolicyReasonCodes.SecretReturnBlocked];
        var summary = CreateSecretResponseSummary(matchResult, target.ParseResult?.IsBatch == true ? "batch" : "single");
        var telemetry = CreateTelemetryMetadata(
            context,
            policy,
            server,
            target.Method,
            FormatAuditDecision(decision),
            reasons,
            eventType: target.EventType,
            argumentSummary: summary,
            toolName: target.ToolName);

        using (var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.PolicyEvaluationActivity,
            telemetry))
        {
            ProxyWardTelemetry.RecordPolicyDecision(telemetry);
            ProxyWardTelemetry.Enrich(activity, telemetry);
        }

        await EmitAuditAsync(
            context,
            policy,
            server,
            target.EventType,
            target.Method,
            target.ToolName,
            decision,
            reasons,
            body.Length,
            stopwatch.ElapsedMilliseconds,
            summary,
            target.ParseResult?.Messages.Count ?? 0,
            target.ParseResult?.IsBatch == true ? target.Message?.BatchIndex : null);

        if (decision != AuditDecision.Block)
        {
            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        await WriteToolResponseSecretBlockAsync(context, target, reasons);
    }

    private async Task HandleUnsupportedAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        ResponseInspectionTarget target,
        string unsupportedReason,
        long responseBytes,
        long durationMs,
        ResponseInspectionStream? capture = null)
    {
        var decision = GetUnsupportedDecision(policy);
        var auditDecision = decision switch
        {
            UnsupportedInspectionDecision.Block => AuditDecision.Block,
            UnsupportedInspectionDecision.WouldBlock => AuditDecision.WouldBlock,
            UnsupportedInspectionDecision.Warn => AuditDecision.Warn,
            _ => AuditDecision.Allow
        };

        LogUnsupported(context, policy, target, unsupportedReason, decision, responseBytes);
        var telemetry = CreateTelemetryMetadata(
            context,
            policy,
            server,
            target.Method,
            FormatAuditDecision(auditDecision),
            [PolicyReasonCodes.InspectionUnsupported],
            eventType: target.EventType,
            toolName: target.ToolName);
        ProxyWardTelemetry.RecordInspectionSkip(telemetry, "response", unsupportedReason);

        await EmitAuditAsync(
            context,
            policy,
            server,
            target.EventType,
            target.Method,
            target.ToolName,
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
        ProxyWardPolicy policy,
        ServerPolicy server,
        string eventType,
        string method,
        string? toolName,
        AuditDecision decision,
        IReadOnlyCollection<string> reasons,
        long responseBytes,
        long durationMs,
        JsonNode? summary,
        int batchSize = 0,
        int? batchIndex = null)
    {
        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: eventType,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            ServerId: server.Id,
            Method: method,
            ToolName: toolName,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            CorrelationId: ResolveCorrelationId(context),
            RequestBytes: responseBytes,
            DurationMs: durationMs,
            ArgumentSummary: summary,
            BatchSize: batchSize,
            BatchIndex: batchIndex);

        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.AuditWriteActivity,
            CreateTelemetryMetadata(
                context,
                policy,
                server,
                method,
                FormatAuditDecision(decision),
                reasons,
                eventType: auditEvent.EventType,
                toolName: toolName));

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
                FormatAuditDecision(decision),
                reasons,
                eventType: auditEvent.EventType,
                toolName: toolName));
            logger.LogWarning(
                AuditSinkFailureEvent,
                ex,
                "ProxyWard audit sink failed to record {EventType} event for server {ServerId}.",
                eventType,
                server.Id);
        }
    }

    private async Task<ToolSurfaceDriftResult> EvaluateDriftAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        IReadOnlyList<DiscoveredTool> tools)
    {
        using var activity = ProxyWardTelemetry.StartActivity(
            ProxyWardTelemetry.SchemaLockCheckActivity,
            CreateTelemetryMetadata(context, policy, server, "tools/list"));

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
                policy,
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

    private void StoreToolsListInspectionCache(
        string cacheKey,
        IReadOnlyList<DiscoveredTool> tools,
        ToolSurfaceDriftResult driftResult)
    {
        var cachedDriftResult = driftResult is { HasDrift: true, WasNewVersion: true }
            ? ToolSurfaceDriftResult.NoDrift(driftResult.Version, wasNewVersion: false)
            : driftResult;

        _toolsListInspectionCache[cacheKey] = new ToolsListInspectionCacheEntry(
            DateTimeOffset.UtcNow,
            tools,
            cachedDriftResult);
    }

    private static string CreateToolsListInspectionCacheKey(
        ServerPolicy server,
        ProxyWardPolicy policy,
        string? contentType,
        byte[] body)
    {
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{server.Id}\u001f{server.Upstream}\u001f{policy.VersionHash}\u001f{HttpContentTypes.Sanitize(contentType)}\u001f{bodyHash}");
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
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        logger.LogWarning(
            SchemaDriftEvent,
            "ProxyWard schema drift detected for server {ServerId} at schema version {SchemaVersion}; decision {Decision}; reasons {Reasons}.",
            server.Id,
            driftResult.Version,
            FormatAuditDecision(decision),
            string.Join(',', driftResult.Reasons));
    }

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

    private bool ShouldBlockUnsupported(ProxyWardPolicy policy) =>
        GetUnsupportedDecision(policy) is UnsupportedInspectionDecision.Block;

    private void LogUnsupported(
        HttpContext context,
        ProxyWardPolicy policy,
        ResponseInspectionTarget target,
        string unsupportedKind,
        UnsupportedInspectionDecision decision,
        long responseBytes)
    {
        logger.LogWarning(
            ResponseInspectionEvent,
            "ProxyWard inspection event {EventType}: {Decision} for {UnsupportedKind} with reasons {Reasons}; content type {ContentType}; response bytes {ResponseBytes}; mode {Mode}; policy {PolicyVersion}; service {ServiceName}; correlation {CorrelationId}",
            target.EventType,
            FormatDecision(decision),
            unsupportedKind,
            PolicyReasonCodes.InspectionUnsupported,
            HttpContentTypes.Sanitize(context.Response.ContentType),
            responseBytes,
            FormatMode(policy.Mode),
            policy.VersionHash,
            ProxyWardTelemetry.ServiceName,
            ResolveCorrelationId(context));
    }

    private static JsonObject CreateToolSummary(
        IReadOnlyList<DiscoveredTool> tools,
        ToolSurfaceDriftResult? driftResult = null,
        IReadOnlyCollection<DriftReviewRecordResult>? driftReviewResults = null,
        string? inspectionSkipReason = null)
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

        if (!string.IsNullOrWhiteSpace(inspectionSkipReason))
        {
            summary["inspectionSkipped"] = true;
            summary["inspectionSkipReason"] = inspectionSkipReason;
        }

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

        if (driftReviewResults is { Count: > 0 })
        {
            var reviewCount = driftReviewResults.Count;
            var cappedReviews = driftReviewResults
                .Take(MaxDriftReviewIdsInAudit)
                .ToArray();

            summary["driftReviewIds"] = new JsonArray(
                cappedReviews
                    .Take(MaxDriftReviewIdsInAudit)
                    .Select(result => (JsonNode?)JsonValue.Create(result.Row.Id))
                    .ToArray());

            summary["driftReviewCount"] = reviewCount;
            if (reviewCount > MaxDriftReviewIdsInAudit)
            {
                summary["driftReviewIdsTruncated"] = true;
                summary["driftReviewIdLimit"] = MaxDriftReviewIdsInAudit;
            }

            if (reviewCount <= MaxDetailedDriftReviewsInAudit)
            {
                var orderedReviews = driftReviewResults
                    .OrderBy(result => result.Row.Id)
                    .ToArray();

                summary["driftReviews"] = new JsonArray(
                    orderedReviews
                        .Select(result => (JsonNode?)new JsonObject
                        {
                            ["id"] = result.Row.Id,
                            ["toolName"] = result.Row.ToolName,
                            ["fieldName"] = result.Row.FieldName,
                            ["fromVersion"] = result.Row.FromVersion,
                            ["toVersion"] = result.Row.ToVersion,
                            ["status"] = FormatDriftReviewStatus(result.Row.Status),
                            ["wasNewlyCreated"] = result.WasNewlyCreated
                        })
                        .ToArray());
            }
            else
            {
                summary["driftReviewsTruncated"] = true;
                summary["driftReviewDetailLimit"] = MaxDetailedDriftReviewsInAudit;
            }
        }

        return summary;
    }

    private static JsonObject CreateUnsupportedSummary(string unsupportedKind) =>
        new()
        {
            ["unsupportedKind"] = unsupportedKind
        };

    private static SecretPatternMatchResult MatchToolResponseSecrets(ServerPolicy server, byte[] body)
    {
        var patternSet = SecretPatternSet.Create(new SecretRedactionOptions(
            RedactInLogs: true,
            Patterns: server.Secrets.Patterns));
        if (patternSet.IsEmpty)
        {
            return SecretPatternMatchResult.None;
        }

        try
        {
            var root = JsonNode.Parse(body);
            var result = patternSet.MatchJson(root);
            if (result.WasMatched)
            {
                return result;
            }
        }
        catch (JsonException)
        {
            return patternSet.MatchText(Encoding.UTF8.GetString(body));
        }

        return patternSet.MatchText(Encoding.UTF8.GetString(body));
    }

    private static JsonObject CreateSecretResponseSummary(
        SecretPatternMatchResult matchResult,
        string responseShape)
    {
        var matchTypes = new JsonArray();
        foreach (var matchType in matchResult.MatchTypes)
        {
            matchTypes.Add(JsonValue.Create(matchType));
        }

        return new JsonObject
        {
            ["matched"] = matchResult.WasMatched,
            ["matchTypes"] = matchTypes,
            ["responseShape"] = responseShape
        };
    }

    private static async Task WriteToolResponseSecretBlockAsync(
        HttpContext context,
        ResponseInspectionTarget target,
        IReadOnlyCollection<string> reasons)
    {
        context.Response.Headers.Clear();

        if (CanWriteJsonRpcToolResponseError(target))
        {
            var response = JsonRpcPolicyError.Create(
                target.Message!.Id!,
                reasons,
                "MCP ProxyWard blocked this tool response");

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "MCP ProxyWard blocked this tool response.",
            decision = "block",
            reasons
        }, context.RequestAborted);
    }

    private static bool CanWriteJsonRpcToolResponseError(ResponseInspectionTarget target) =>
        target.ParseResult is { IsBatch: false }
        && target.Message is not null
        && string.Equals(target.Message.JsonRpc, "2.0", StringComparison.Ordinal)
        && string.Equals(target.Message.Method, "tools/call", StringComparison.Ordinal)
        && target.Message.Id is not null
        && JsonRpcPolicyError.HasSupportedRequestId(target.Message.Id);

    private static string ResolveCorrelationId(HttpContext context) =>
        context.Items[AuditItems.CorrelationId] as string ?? context.TraceIdentifier;

    private ProxyWardPolicy ResolvePolicySnapshot(HttpContext context) =>
        context.Items.TryGetValue(ServerResolutionItems.PolicySnapshot, out var snapshot)
            && snapshot is ProxyWardPolicy policy
                ? policy
                : policyProvider.Current;

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
        ProxyWardPolicy policy,
        ServerPolicy server,
        string? method = null,
        string? decision = null,
        IReadOnlyCollection<string>? reasons = null,
        string? eventType = null,
        int? schemaVersion = null,
        JsonNode? argumentSummary = null,
        string? toolName = null) =>
        new(
            CorrelationId: ResolveCorrelationId(context),
            ServerId: server.Id,
            Method: method,
            ToolName: toolName,
            Mode: FormatMode(policy.Mode),
            Decision: decision,
            Reasons: reasons,
            PolicyVersion: policy.VersionHash,
            SchemaVersion: schemaVersion,
            AuditEventType: eventType,
            ArgumentSummary: FormatArgumentSummary(argumentSummary));

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string? FormatArgumentSummary(JsonNode? argumentSummary) =>
        argumentSummary?.ToJsonString();

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };

    private static string FormatDriftReviewStatus(DriftReviewStatus status) =>
        status switch
        {
            DriftReviewStatus.Approved => "approved",
            DriftReviewStatus.Rejected => "rejected",
            DriftReviewStatus.Blocked => "blocked",
            _ => "pending"
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

    private sealed record ToolsListInspectionCacheEntry(
        DateTimeOffset CachedAtUtc,
        IReadOnlyList<DiscoveredTool> Tools,
        ToolSurfaceDriftResult DriftResult);

}

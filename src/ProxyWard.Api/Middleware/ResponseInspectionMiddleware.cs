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
    IMcpMethodClassifier classifier,
    IToolDefinitionExtractor extractor,
    ToolSurfaceDriftEvaluator driftEvaluator,
    ISchemaDriftReviewStore driftReviewStore,
    IToolSchemaDiffMetadataStore diffMetadataStore,
    ToolSchemaDiffMetadataOptions diffMetadataOptions,
    IAuditSink auditSink,
    ILogger<ResponseInspectionMiddleware> logger)
{
    private const string DefaultMcpProtocol = "2025-11-25";
    private const string ToolsListResponseInspectionEventType = "tools_list_response_inspection";
    private const string ToolResponseSecretInspectionEventType = "tool_response_secret_inspection";
    private const int MaxDetailedDriftReviewsInAudit = 20;
    private const int MaxDriftReviewIdsInAudit = 20;

    private static readonly TimeSpan CurrentUnapprovedReviewCacheTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ToolsListInspectionCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly EventId ResponseInspectionEvent = new(1301, "ResponseInspection");
    private static readonly EventId AuditSinkFailureEvent = new(1302, "AuditSinkFailure");
    private static readonly EventId SchemaLockWriteFailureEvent = new(1303, "SchemaLockWriteFailure");
    private static readonly EventId SchemaLockUpstreamChangedEvent = new(1304, "SchemaLockUpstreamChanged");
    private static readonly EventId SchemaDriftEvent = new(1305, "SchemaDrift");
    private static readonly EventId SchemaDriftReviewWriteFailureEvent = new(1306, "SchemaDriftReviewWriteFailure");
    private static readonly EventId SchemaDiffMetadataWriteFailureEvent = new(1307, "SchemaDiffMetadataWriteFailure");
    private static readonly EventId SchemaDriftReviewReadFailureEvent = new(1308, "SchemaDriftReviewReadFailure");

    private readonly ConcurrentDictionary<string, DriftReviewCacheEntry> _currentUnapprovedReviewCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _currentUnapprovedReviewCacheRefreshGates = new(StringComparer.Ordinal);
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
        var target = ResolveResponseInspectionTarget(context, server);
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

        stopwatch.Stop();

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
                stopwatch.ElapsedMilliseconds,
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
            result = ExtractToolsListResponse(body, context.Response.ContentType);
            if (!result.Success)
            {
                await EmitAuditAsync(
                    context,
                    policy,
                    server,
                    ToolsListResponseInspectionEventType,
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
                ToolsListResponseInspectionEventType,
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
            var driftReviewResults = await RecordDriftReviewsAsync(context, policy, server, driftResult);
            await RecordDiffMetadataAsync(context, server, driftResult, driftReviewResults);
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
                ToolsListResponseInspectionEventType,
                "tools/list",
                null,
                decision,
                driftResult.Reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult, driftReviewResults));

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

        var currentUnapprovedReviews = await GetCurrentUnapprovedDriftReviewsAsync(
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

            LogSchemaDriftReviewState(server, decision, driftResult.Version, currentUnapprovedReviews);
            ProxyWardTelemetry.RecordSchemaDrift(telemetry);

            await EmitAuditAsync(
                context,
                policy,
                server,
                ToolsListResponseInspectionEventType,
                "tools/list",
                null,
                decision,
                reasons,
                body.Length,
                stopwatch.ElapsedMilliseconds,
                CreateToolSummary(result.Tools, driftResult, currentUnapprovedReviews));

            if (decision == AuditDecision.Block)
            {
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "MCP tool surface drift review is not approved.",
                    decision = "block",
                    reasons,
                    tools = currentUnapprovedReviews
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
                        .ToArray()
                }, context.RequestAborted);
                return;
            }

            await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
            return;
        }

        await EmitAuditAsync(
            context,
            policy,
            server,
            ToolsListResponseInspectionEventType,
            "tools/list",
            null,
            AuditDecision.Allow,
            [],
            body.Length,
            stopwatch.ElapsedMilliseconds,
            CreateToolSummary(result.Tools, driftResult));

        await capture.CopyBufferedBodyToAsync(originalBody, context.RequestAborted);
    }

    private ResponseInspectionTarget ResolveResponseInspectionTarget(HttpContext context, ServerPolicy server)
    {
        if (!context.Items.TryGetValue(RequestInspectionItems.JsonRpcParseResult, out var parseItem)
            || parseItem is not JsonRpcParseResult parseResult
            || parseResult.Status != JsonRpcParseStatus.Parsed)
        {
            return ResponseInspectionTarget.None;
        }

        foreach (var message in parseResult.Messages)
        {
            var classification = classifier.Classify(message);
            if (classification.Kind == McpMessageKind.ToolsList)
            {
                return ResponseInspectionTarget.ToolsList;
            }
        }

        if (!server.Secrets.BlockReturn || server.Secrets.Patterns.Count == 0)
        {
            return ResponseInspectionTarget.None;
        }

        foreach (var message in parseResult.Messages)
        {
            var classification = classifier.Classify(message);
            if (classification.Kind == McpMessageKind.ToolCall)
            {
                return new ResponseInspectionTarget(
                    ResponseInspectionKind.ToolCallSecretReturn,
                    "tools/call",
                    ToolResponseSecretInspectionEventType,
                    classification.ToolName,
                    parseResult,
                    message);
            }
        }

        return ResponseInspectionTarget.None;
    }

    private ToolListExtractionResult ExtractToolsListResponse(byte[] body, string? contentType)
    {
        if (!IsStreamingContentType(contentType))
        {
            return extractor.Extract(body);
        }

        var payloads = ExtractServerSentEventDataPayloads(body);
        if (payloads.Count == 0)
        {
            return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
        }

        var jsonMessages = new JsonArray();
        foreach (var payload in payloads)
        {
            var trimmed = payload.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
            }

            if (message is not null)
            {
                jsonMessages.Add(message);
            }
        }

        if (jsonMessages.Count == 0)
        {
            return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
        }

        var extractableBody = Encoding.UTF8.GetBytes(
            jsonMessages.Count == 1
                ? jsonMessages[0]!.ToJsonString()
                : jsonMessages.ToJsonString());

        return extractor.Extract(extractableBody);
    }

    private static IReadOnlyList<string> ExtractServerSentEventDataPayloads(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var payloads = new List<string>();
        var currentDataLines = new List<string>();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                FlushEventData(currentDataLines, payloads);
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..];
            currentDataLines.Add(data.StartsWith(' ') ? data[1..] : data);
        }

        FlushEventData(currentDataLines, payloads);
        return payloads;
    }

    private static void FlushEventData(List<string> currentDataLines, List<string> payloads)
    {
        if (currentDataLines.Count == 0)
        {
            return;
        }

        payloads.Add(string.Join('\n', currentDataLines));
        currentDataLines.Clear();
    }

    private async Task HandleToolCallSecretResponseAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        ResponseInspectionTarget target,
        byte[] body,
        long durationMs,
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
            durationMs,
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

    private async Task<IReadOnlyList<DriftReviewRecordResult>> RecordDriftReviewsAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        ServerPolicy server,
        ToolSurfaceDriftResult driftResult)
    {
        if (driftResult.PreviousVersion is not { } previousVersion
            || previousVersion < 0
            || driftResult.Version <= previousVersion
            || !driftResult.WasNewVersion)
        {
            return [];
        }

        var detectedAtUtc = DateTimeOffset.UtcNow;
        var results = new List<DriftReviewRecordResult>();

        try
        {
            foreach (var drift in driftResult.Drifts)
            {
                foreach (var reviewField in CreateDriftReviewFields(drift))
                {
                    var observation = new DriftReviewObservation(
                        ServerId: server.Id,
                        ToolName: drift.ToolName,
                        FieldName: reviewField.FieldName,
                        FromVersion: previousVersion,
                        ToVersion: driftResult.Version,
                        Reasons: reviewField.Reasons,
                        PolicyVersion: policy.VersionHash,
                        DetectedAtUtc: detectedAtUtc);

                    results.Add(await driftReviewStore
                        .RecordObservationAsync(observation, context.RequestAborted)
                        .ConfigureAwait(false));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSchemaDriftReviewWriteFailure(server, ex);
            return [];
        }

        if (results.Count > 0)
        {
            _currentUnapprovedReviewCache.TryRemove(CreateDriftReviewCacheKey(server.Id, driftResult.Version), out _);
        }

        return results;
    }

    private async Task<IReadOnlyList<DriftReviewRecordResult>> GetCurrentUnapprovedDriftReviewsAsync(
        HttpContext context,
        ServerPolicy server,
        ToolSurfaceDriftResult driftResult)
    {
        if (driftResult.Version <= 0)
        {
            return [];
        }

        var cacheKey = CreateDriftReviewCacheKey(server.Id, driftResult.Version);
        var now = DateTimeOffset.UtcNow;
        if (_currentUnapprovedReviewCache.TryGetValue(cacheKey, out var cached))
        {
            if (now - cached.CachedAtUtc <= CurrentUnapprovedReviewCacheTtl)
            {
                return cached.Results;
            }

            var refreshGate = _currentUnapprovedReviewCacheRefreshGates.GetOrAdd(
                cacheKey,
                _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
            if (!refreshGate.Wait(0))
            {
                return cached.Results;
            }

            try
            {
                return await RefreshCurrentUnapprovedReviewsAsync(
                        context,
                        server,
                        driftResult.Version,
                        cacheKey)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSchemaDriftReviewReadFailure(server, ex);
                return cached.Results;
            }
            finally
            {
                refreshGate.Release();
            }
        }

        var initialRefreshGate = _currentUnapprovedReviewCacheRefreshGates.GetOrAdd(
            cacheKey,
            _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await initialRefreshGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            if (_currentUnapprovedReviewCache.TryGetValue(cacheKey, out cached)
                && DateTimeOffset.UtcNow - cached.CachedAtUtc <= CurrentUnapprovedReviewCacheTtl)
            {
                return cached.Results;
            }

            return await RefreshCurrentUnapprovedReviewsAsync(
                    context,
                    server,
                    driftResult.Version,
                    cacheKey)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSchemaDriftReviewReadFailure(server, ex);
            return [];
        }
        finally
        {
            initialRefreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<DriftReviewRecordResult>> RefreshCurrentUnapprovedReviewsAsync(
        HttpContext context,
        ServerPolicy server,
        int schemaVersion,
        string cacheKey)
    {
        var rows = await driftReviewStore
            .GetByServerAsync(server.Id, context.RequestAborted)
            .ConfigureAwait(false);

        var results = rows
            .Where(row => row.ToVersion == schemaVersion
                && row.Status != DriftReviewStatus.Approved)
            .OrderBy(row => row.Id)
            .Select(row => new DriftReviewRecordResult(row, WasNewlyCreated: false))
            .ToArray();

        _currentUnapprovedReviewCache[cacheKey] = new DriftReviewCacheEntry(DateTimeOffset.UtcNow, results);
        return results;
    }

    private static string CreateDriftReviewCacheKey(string serverId, int schemaVersion) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{serverId}\u001f{schemaVersion}");

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
            $"{server.Id}\u001f{server.Upstream}\u001f{policy.VersionHash}\u001f{SanitizeMediaType(contentType)}\u001f{bodyHash}");
    }

    private static IEnumerable<(string FieldName, IReadOnlyCollection<string> Reasons)> CreateDriftReviewFields(
        ToolSurfaceDrift drift)
    {
        if (drift.Reasons.Contains(PolicyReasonCodes.ToolDescriptionChanged, StringComparer.Ordinal))
        {
            yield return ("description", [PolicyReasonCodes.ToolDescriptionChanged]);
        }

        if (drift.Reasons.Contains(PolicyReasonCodes.ToolSchemaChanged, StringComparer.Ordinal))
        {
            yield return ("schema", [PolicyReasonCodes.ToolSchemaChanged]);
        }

        if (drift.Reasons.Contains(PolicyReasonCodes.McpProtocolChanged, StringComparer.Ordinal))
        {
            yield return ("mcpProtocol", [PolicyReasonCodes.McpProtocolChanged]);
        }
    }

    private async Task RecordDiffMetadataAsync(
        HttpContext context,
        ServerPolicy server,
        ToolSurfaceDriftResult driftResult,
        IReadOnlyList<DriftReviewRecordResult> driftReviewResults)
    {
        if (driftReviewResults.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var reviewResult in driftReviewResults)
            {
                var matchingDrift = driftResult.Drifts.FirstOrDefault(drift =>
                    string.Equals(drift.ToolName, reviewResult.Row.ToolName, StringComparison.Ordinal));
                if (matchingDrift is null)
                {
                    continue;
                }

                var input = CreateDiffMetadataInput(reviewResult.Row, matchingDrift, driftResult);
                if (input is null)
                {
                    continue;
                }

                await diffMetadataStore
                    .RecordAsync(input, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSchemaDiffMetadataWriteFailure(server, ex);
        }
    }

    private ToolSchemaDiffMetadataInput? CreateDiffMetadataInput(
        DriftReviewRow review,
        ToolSurfaceDrift drift,
        ToolSurfaceDriftResult driftResult)
    {
        var createdAtUtc = DateTimeOffset.UtcNow;
        return review.FieldName switch
        {
            "description" => new ToolSchemaDiffMetadataInput(
                review.Id,
                SafeToolSchemaMetadata.CreateDescriptionJson(drift.PreviousEntry, diffMetadataOptions),
                SafeToolSchemaMetadata.CreateDescriptionJson(drift.CurrentEntry, diffMetadataOptions),
                SafeToolSchemaMetadata.HashDescription(drift.PreviousEntry),
                SafeToolSchemaMetadata.HashDescription(drift.CurrentEntry),
                createdAtUtc),
            "schema" => new ToolSchemaDiffMetadataInput(
                review.Id,
                SafeToolSchemaMetadata.CreateSchemaJson(drift.PreviousEntry, diffMetadataOptions),
                SafeToolSchemaMetadata.CreateSchemaJson(drift.CurrentEntry, diffMetadataOptions),
                SafeToolSchemaMetadata.HashSchema(drift.PreviousEntry),
                SafeToolSchemaMetadata.HashSchema(drift.CurrentEntry),
                createdAtUtc),
            "mcpProtocol" => new ToolSchemaDiffMetadataInput(
                review.Id,
                BeforeJson: null,
                AfterJson: null,
                SafeToolSchemaMetadata.HashText(driftResult.PreviousMcpProtocol),
                SafeToolSchemaMetadata.HashText(driftResult.CurrentMcpProtocol),
                createdAtUtc),
            _ => null
        };
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

    private void LogSchemaDriftReviewWriteFailure(ServerPolicy server, Exception exception)
    {
        logger.LogWarning(
            SchemaDriftReviewWriteFailureEvent,
            exception,
            "ProxyWard schema drift review write failed for server {ServerId}. Audit event will not include drift review ids.",
            server.Id);
    }

    private void LogSchemaDiffMetadataWriteFailure(ServerPolicy server, Exception exception)
    {
        logger.LogWarning(
            SchemaDiffMetadataWriteFailureEvent,
            exception,
            "ProxyWard schema diff metadata write failed for server {ServerId}. Drift review item remains available with hash fallback.",
            server.Id);
    }

    private void LogSchemaDriftReviewReadFailure(ServerPolicy server, Exception exception)
    {
        logger.LogWarning(
            SchemaDriftReviewReadFailureEvent,
            exception,
            "ProxyWard schema drift review read failed for server {ServerId}. Current review state will not affect this response.",
            server.Id);
    }

    private void LogSchemaDriftReviewState(
        ServerPolicy server,
        AuditDecision decision,
        int schemaVersion,
        IReadOnlyList<DriftReviewRecordResult> reviewResults)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        logger.LogWarning(
            SchemaDriftEvent,
            "ProxyWard schema drift review state requires action for server {ServerId} at schema version {SchemaVersion}; decision {Decision}; review ids {ReviewIds}.",
            server.Id,
            schemaVersion,
            FormatAuditDecision(decision),
            string.Join(',', reviewResults.Select(result => result.Row.Id)));
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
            SanitizeMediaType(context.Response.ContentType),
            responseBytes,
            FormatMode(policy.Mode),
            policy.VersionHash,
            ProxyWardTelemetry.ServiceName,
            ResolveCorrelationId(context));
    }

    private static JsonObject CreateToolSummary(
        IReadOnlyList<DiscoveredTool> tools,
        ToolSurfaceDriftResult? driftResult = null,
        IReadOnlyCollection<DriftReviewRecordResult>? driftReviewResults = null)
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
            // Fall back to raw response text matching without ever logging the response body.
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
            var response = CreateJsonRpcError(
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
        && IsValidJsonRpcRequestId(target.Message.Id);

    private static bool IsValidJsonRpcRequestId(JsonNode id)
    {
        var json = id.ToJsonString();
        return json.Length > 0 && (json[0] == '"' || json[0] == '-' || char.IsDigit(json[0]));
    }

    private static JsonObject CreateJsonRpcError(
        JsonNode id,
        IReadOnlyCollection<string> reasons,
        string message)
    {
        var reasonNodes = new JsonArray();
        foreach (var reason in reasons)
        {
            reasonNodes.Add(JsonValue.Create(reason));
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32001,
                ["message"] = message,
                ["data"] = new JsonObject
                {
                    ["reasons"] = reasonNodes
                }
            }
        };
    }

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

    private static bool IsStreamingContentType(string? contentType) =>
        SanitizeMediaType(contentType).Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);

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

    private enum ResponseInspectionKind
    {
        None,
        ToolsList,
        ToolCallSecretReturn
    }

    private sealed record ResponseInspectionTarget(
        ResponseInspectionKind Kind,
        string Method,
        string EventType,
        string? ToolName,
        JsonRpcParseResult? ParseResult,
        JsonRpcMessage? Message)
    {
        public static ResponseInspectionTarget None { get; } = new(
            ResponseInspectionKind.None,
            string.Empty,
            string.Empty,
            null,
            null,
            null);

        public static ResponseInspectionTarget ToolsList { get; } = new(
            ResponseInspectionKind.ToolsList,
            "tools/list",
            ToolsListResponseInspectionEventType,
            null,
            null,
            null);
    }

    private sealed record DriftReviewCacheEntry(
        DateTimeOffset CachedAtUtc,
        IReadOnlyList<DriftReviewRecordResult> Results);

    private sealed record ToolsListInspectionCacheEntry(
        DateTimeOffset CachedAtUtc,
        IReadOnlyList<DiscoveredTool> Tools,
        ToolSurfaceDriftResult DriftResult);

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

            if (!IsInspectableContentType(response.ContentType))
            {
                SetUnsupported("unsupported_content_type");
                return;
            }

            var contentLength = response.ContentLength;
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

        private static bool IsInspectableContentType(string? contentType)
        {
            var mediaType = SanitizeMediaType(contentType);
            return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)
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

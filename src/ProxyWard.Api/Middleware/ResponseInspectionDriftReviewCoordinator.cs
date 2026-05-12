using System.Collections.Concurrent;
using System.Globalization;
using ProxyWard.Audit.Events;
using ProxyWard.Core.Policies;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Middleware;

public sealed class ResponseInspectionDriftReviewCoordinator(
    ISchemaDriftReviewStore driftReviewStore,
    IToolSchemaDiffMetadataStore diffMetadataStore,
    ToolSchemaDiffMetadataOptions diffMetadataOptions,
    ILogger<ResponseInspectionMiddleware> logger)
{
    private static readonly TimeSpan CurrentUnapprovedReviewCacheTtl = TimeSpan.FromSeconds(1);

    private static readonly EventId SchemaDriftEvent = new(1305, "SchemaDrift");
    private static readonly EventId SchemaDriftReviewWriteFailureEvent = new(1306, "SchemaDriftReviewWriteFailure");
    private static readonly EventId SchemaDiffMetadataWriteFailureEvent = new(1307, "SchemaDiffMetadataWriteFailure");
    private static readonly EventId SchemaDriftReviewReadFailureEvent = new(1308, "SchemaDriftReviewReadFailure");

    private readonly ConcurrentDictionary<string, DriftReviewCacheEntry> _currentUnapprovedReviewCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _currentUnapprovedReviewCacheRefreshGates = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<DriftReviewRecordResult>> RecordDriftReviewsAsync(
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

    public async Task<IReadOnlyList<DriftReviewRecordResult>> GetCurrentUnapprovedDriftReviewsAsync(
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

    public async Task RecordDiffMetadataAsync(
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

    public void LogSchemaDriftReviewState(
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

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };

    private sealed record DriftReviewCacheEntry(
        DateTimeOffset CachedAtUtc,
        IReadOnlyList<DriftReviewRecordResult> Results);
}

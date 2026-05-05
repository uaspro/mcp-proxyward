using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ProxyWard.Api.Observability;

public static class ProxyWardTelemetry
{
    public const string ServiceName = "mcp-proxyward";
    public const string ActivitySourceName = "ProxyWard";
    public const string MeterName = "ProxyWard";

    public const string RequestInspectionActivity = "proxyward.request_inspection";
    public const string PolicyEvaluationActivity = "proxyward.policy_evaluation";
    public const string YarpProxyActivity = "proxyward.yarp_proxy";
    public const string SchemaLockCheckActivity = "proxyward.schema_lock_check";
    public const string AuditWriteActivity = "proxyward.audit_write";

    public const string RequestsMetric = "proxyward.requests";
    public const string PolicyDecisionsMetric = "proxyward.policy_decisions";
    public const string BlockedCallsMetric = "proxyward.blocked_calls";
    public const string WouldBlockCallsMetric = "proxyward.would_block_calls";
    public const string SchemaDriftEventsMetric = "proxyward.schema_drift_events";
    public const string SchemaWriteFailedMetric = "proxyward.schema.write_failed";
    public const string SchemaUpstreamChangedMetric = "proxyward.schema.upstream_changed";
    public const string InspectionSkipsMetric = "proxyward.inspection_skips";
    public const string AuditSinkFailuresMetric = "proxyward.audit_sink_failures";

    public const string ServiceNameTag = "service.name";
    public const string CorrelationIdTag = "correlation.id";
    public const string ServerIdTag = "server.id";
    public const string McpMethodTag = "mcp.method";
    public const string McpToolNameTag = "mcp.tool.name";
    public const string McpArgumentSummaryTag = "mcp.argument.summary";
    public const string PolicyModeTag = "policy.mode";
    public const string PolicyDecisionTag = "policy.decision";
    public const string PolicyReasonsTag = "policy.reasons";
    public const string PolicyVersionTag = "policy.version";
    public const string AuditEventTypeTag = "audit.event_type";
    public const string InspectionDirectionTag = "inspection.direction";
    public const string InspectionUnsupportedKindTag = "inspection.unsupported_kind";
    public const string HttpStatusCodeTag = "http.response.status_code";
    public const string HttpRequestQueryTag = "http.request.query";
    public const string HttpTargetTag = "http.target";
    public const string HttpUrlTag = "http.url";
    public const string UrlFullTag = "url.full";
    public const string UrlQueryTag = "url.query";
    public const string SchemaVersionTag = "schema.version";
    public const string SchemaServerIdTag = "server_id";
    public const string SchemaFailureReasonTag = "reason";
    public const string SchemaPreviousUrlTag = "previous_url";
    public const string SchemaCurrentUrlTag = "current_url";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(RequestsMetric);
    private static readonly Counter<long> PolicyDecisions = Meter.CreateCounter<long>(PolicyDecisionsMetric);
    private static readonly Counter<long> BlockedCalls = Meter.CreateCounter<long>(BlockedCallsMetric);
    private static readonly Counter<long> WouldBlockCalls = Meter.CreateCounter<long>(WouldBlockCallsMetric);
    private static readonly Counter<long> SchemaDriftEvents = Meter.CreateCounter<long>(SchemaDriftEventsMetric);
    private static readonly Counter<long> SchemaWriteFailures = Meter.CreateCounter<long>(SchemaWriteFailedMetric);
    private static readonly Counter<long> SchemaUpstreamChanges = Meter.CreateCounter<long>(SchemaUpstreamChangedMetric);
    private static readonly Counter<long> InspectionSkips = Meter.CreateCounter<long>(InspectionSkipsMetric);
    private static readonly Counter<long> AuditSinkFailures = Meter.CreateCounter<long>(AuditSinkFailuresMetric);

    public static Activity? StartActivity(string name, TelemetryMetadata metadata)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        Enrich(activity, metadata);
        return activity;
    }

    public static void Enrich(Activity? activity, TelemetryMetadata metadata)
    {
        if (activity is null)
        {
            return;
        }

        foreach (var tag in CreateTags(metadata, includeCorrelationId: true))
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }

    public static void SetTag(Activity? activity, string key, object? value)
    {
        if (activity is not null && value is not null)
        {
            activity.SetTag(key, value);
        }
    }

    public static void RedactHttpRequestQueryTags(
        Activity? activity,
        string? path,
        string? queryString,
        string? scheme,
        string? host)
    {
        if (activity is null || string.IsNullOrWhiteSpace(queryString))
        {
            return;
        }

        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path;
        activity.SetTag(UrlQueryTag, "[redacted-query]");
        activity.SetTag(HttpRequestQueryTag, "[redacted-query]");
        activity.SetTag(HttpTargetTag, $"{normalizedPath}?[redacted-query]");

        if (!string.IsNullOrWhiteSpace(scheme))
        {
            activity.SetTag(HttpUrlTag, $"{scheme}://[redacted-host]{normalizedPath}?[redacted-query]");
            activity.SetTag(UrlFullTag, $"{scheme}://[redacted-host]{normalizedPath}?[redacted-query]");
        }
        else if (!string.IsNullOrWhiteSpace(host))
        {
            activity.SetTag(HttpUrlTag, $"[redacted-host]{normalizedPath}?[redacted-query]");
            activity.SetTag(UrlFullTag, $"[redacted-host]{normalizedPath}?[redacted-query]");
        }
    }

    public static void RecordProxiedRequest(
        TelemetryMetadata metadata,
        int? statusCode)
    {
        var tags = CreateMetricTags(metadata);
        if (statusCode is not null)
        {
            tags.Add(HttpStatusCodeTag, statusCode.Value);
        }

        Requests.Add(1, in tags);
    }

    public static void RecordPolicyDecision(TelemetryMetadata metadata)
    {
        var tags = CreateMetricTags(metadata);
        PolicyDecisions.Add(1, in tags);

        if (string.Equals(metadata.Decision, "block", StringComparison.Ordinal))
        {
            BlockedCalls.Add(1, in tags);
        }
        else if (string.Equals(metadata.Decision, "would_block", StringComparison.Ordinal))
        {
            WouldBlockCalls.Add(1, in tags);
        }
    }

    public static void RecordSchemaDrift(TelemetryMetadata metadata)
    {
        var tags = CreateMetricTags(metadata);
        SchemaDriftEvents.Add(1, in tags);
    }

    public static void RecordSchemaWriteFailed(string serverId, string reason)
    {
        var tags = new TagList
        {
            { SchemaServerIdTag, NormalizeTagValue(serverId) },
            { SchemaFailureReasonTag, NormalizeTagValue(reason) }
        };

        SchemaWriteFailures.Add(1, in tags);
    }

    public static void RecordSchemaUpstreamChanged(
        string serverId,
        string previousUrl,
        string currentUrl)
    {
        var tags = new TagList
        {
            { SchemaServerIdTag, NormalizeTagValue(serverId) },
            { SchemaPreviousUrlTag, NormalizeTagValue(previousUrl) },
            { SchemaCurrentUrlTag, NormalizeTagValue(currentUrl) }
        };

        SchemaUpstreamChanges.Add(1, in tags);
    }

    public static void RecordInspectionSkip(
        TelemetryMetadata metadata,
        string direction,
        string unsupportedKind)
    {
        var tags = CreateMetricTags(metadata);
        tags.Add(InspectionDirectionTag, direction);
        tags.Add(InspectionUnsupportedKindTag, NormalizeTagValue(unsupportedKind));
        InspectionSkips.Add(1, in tags);
    }

    public static void RecordAuditSinkFailure(TelemetryMetadata metadata)
    {
        var tags = CreateMetricTags(metadata);
        AuditSinkFailures.Add(1, in tags);
    }

    public static KeyValuePair<string, object?>[] CreateTags(
        TelemetryMetadata metadata,
        bool includeCorrelationId,
        bool includePolicyVersion = true,
        bool includeReasons = true,
        bool includeArgumentSummary = true)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new(ServiceNameTag, ServiceName)
        };

        AddIfPresent(tags, ServerIdTag, metadata.ServerId);
        AddIfPresent(tags, McpMethodTag, metadata.Method);
        AddIfPresent(tags, McpToolNameTag, metadata.ToolName);
        if (includeArgumentSummary)
        {
            AddIfPresent(tags, McpArgumentSummaryTag, metadata.ArgumentSummary);
        }
        AddIfPresent(tags, PolicyModeTag, metadata.Mode);
        AddIfPresent(tags, PolicyDecisionTag, metadata.Decision);
        if (includeReasons)
        {
            AddIfPresent(tags, PolicyReasonsTag, FormatReasons(metadata.Reasons));
        }
        if (includePolicyVersion)
        {
            AddIfPresent(tags, PolicyVersionTag, metadata.PolicyVersion);
        }
        AddIfPresent(tags, SchemaVersionTag, metadata.SchemaVersion);
        AddIfPresent(tags, AuditEventTypeTag, metadata.AuditEventType);

        if (includeCorrelationId)
        {
            AddIfPresent(tags, CorrelationIdTag, metadata.CorrelationId);
        }

        return tags.ToArray();
    }

    public static string? FormatReasons(IReadOnlyCollection<string>? reasons)
    {
        if (reasons is null || reasons.Count == 0)
        {
            return null;
        }

        return string.Join(',', reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)));
    }

    private static TagList CreateMetricTags(TelemetryMetadata metadata)
    {
        var tags = new TagList();
        foreach (var tag in CreateTags(
            metadata,
            includeCorrelationId: false,
            includePolicyVersion: false,
            includeReasons: false,
            includeArgumentSummary: false))
        {
            tags.Add(tag.Key, tag.Value);
        }

        return tags;
    }

    private static void AddIfPresent(List<KeyValuePair<string, object?>> tags, string key, string? value)
    {
        var normalized = NormalizeTagValue(value);
        if (normalized is not null)
        {
            tags.Add(new KeyValuePair<string, object?>(key, normalized));
        }
    }

    private static void AddIfPresent(List<KeyValuePair<string, object?>> tags, string key, int? value)
    {
        if (value is not null)
        {
            tags.Add(new KeyValuePair<string, object?>(key, value.Value));
        }
    }

    private static string? NormalizeTagValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record TelemetryMetadata(
    string? CorrelationId = null,
    string? ServerId = null,
    string? Method = null,
    string? ToolName = null,
    string? Mode = null,
    string? Decision = null,
    IReadOnlyCollection<string>? Reasons = null,
    string? PolicyVersion = null,
    int? SchemaVersion = null,
    string? AuditEventType = null,
    string? ArgumentSummary = null);

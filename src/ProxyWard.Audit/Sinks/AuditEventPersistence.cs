using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

internal static class AuditEventPersistence
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false
    };

    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    public static string FormatReasons(IReadOnlyCollection<string> reasons) =>
        string.Join(',', reasons);

    public static string FormatDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Allow => "allow",
            AuditDecision.Warn => "warn",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Block => "block",
            _ => "unknown"
        };

    public static string SerializePayload(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var payload = new JsonObject
        {
            ["timestamp"] = FormatTimestamp(auditEvent.Timestamp),
            ["eventType"] = auditEvent.EventType,
            ["mode"] = auditEvent.Mode,
            ["decision"] = FormatDecision(auditEvent.Decision),
            ["serverId"] = auditEvent.ServerId,
            ["method"] = auditEvent.Method,
            ["toolName"] = auditEvent.ToolName,
            ["reasons"] = new JsonArray(auditEvent.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["policyVersion"] = auditEvent.PolicyVersion,
            ["correlationId"] = auditEvent.CorrelationId,
            ["requestBytes"] = auditEvent.RequestBytes,
            ["durationMs"] = auditEvent.DurationMs,
            ["batchSize"] = auditEvent.BatchSize,
            ["batchIndex"] = auditEvent.BatchIndex,
            ["argumentOverrideApplied"] = auditEvent.ArgumentOverrideApplied,
            ["argumentSummary"] = auditEvent.ArgumentSummary?.DeepClone()
        };

        return payload.ToJsonString(PayloadJsonOptions);
    }
}

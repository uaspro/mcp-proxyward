using System.Text.Json.Nodes;

namespace ProxyWard.Audit.Events;

public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string EventType,
    string Mode,
    AuditDecision Decision,
    string ServerId,
    string? Method,
    string? ToolName,
    IReadOnlyCollection<string> Reasons,
    string PolicyVersion,
    string CorrelationId,
    long RequestBytes,
    long DurationMs,
    JsonNode? ArgumentSummary,
    int BatchSize,
    int? BatchIndex = null,
    bool ArgumentOverrideApplied = false);

namespace ProxyWard.Locking.Persistence;

public sealed record DriftReviewRow(
    long Id,
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    DriftReviewStatus Status,
    IReadOnlyList<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote);

namespace ProxyWard.Locking.Persistence;

public sealed record DriftReviewObservation(
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    IReadOnlyCollection<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc);

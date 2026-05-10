namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaDiffMetadataRow(
    long Id,
    long DriftReviewId,
    string? BeforeJson,
    string? AfterJson,
    string BeforeHash,
    string AfterHash,
    DateTimeOffset CreatedAtUtc);

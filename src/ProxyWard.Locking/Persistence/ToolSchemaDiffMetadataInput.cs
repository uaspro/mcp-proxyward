namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaDiffMetadataInput(
    long DriftReviewId,
    string? BeforeJson,
    string? AfterJson,
    string BeforeHash,
    string AfterHash,
    DateTimeOffset CreatedAtUtc);

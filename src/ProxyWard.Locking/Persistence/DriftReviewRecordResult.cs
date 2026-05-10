namespace ProxyWard.Locking.Persistence;

public sealed record DriftReviewRecordResult(
    DriftReviewRow Row,
    bool WasNewlyCreated);

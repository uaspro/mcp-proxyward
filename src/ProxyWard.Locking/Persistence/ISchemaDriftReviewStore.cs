namespace ProxyWard.Locking.Persistence;

public interface ISchemaDriftReviewStore
{
    ValueTask<DriftReviewRecordResult> RecordObservationAsync(
        DriftReviewObservation observation,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DriftReviewRow>> GetByServerAsync(
        string serverId,
        CancellationToken cancellationToken);
}

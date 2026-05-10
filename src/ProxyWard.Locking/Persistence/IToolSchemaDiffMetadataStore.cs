namespace ProxyWard.Locking.Persistence;

public interface IToolSchemaDiffMetadataStore
{
    ValueTask<ToolSchemaDiffMetadataRow> RecordAsync(
        ToolSchemaDiffMetadataInput input,
        CancellationToken cancellationToken);

    ValueTask<ToolSchemaDiffMetadataRow?> GetByDriftReviewIdAsync(
        long driftReviewId,
        CancellationToken cancellationToken);
}

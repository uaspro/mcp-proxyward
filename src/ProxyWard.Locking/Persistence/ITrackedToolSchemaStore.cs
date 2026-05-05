namespace ProxyWard.Locking.Persistence;

public interface ITrackedToolSchemaStore
{
    ValueTask<ToolSchemaVersionRow?> GetLatestAsync(
        string serverId,
        CancellationToken cancellationToken);

    ValueTask<RecordedVersion> RecordAsync(
        ToolSchemaSnapshotInput snapshot,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken);
}

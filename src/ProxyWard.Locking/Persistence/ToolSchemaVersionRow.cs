namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaVersionRow(
    long Id,
    string ServerId,
    string UpstreamUrl,
    int Version,
    string SnapshotHash,
    string McpProtocol,
    IReadOnlyList<ToolSchemaSnapshotEntry> Fingerprints,
    int ToolCount,
    string? PolicyVersion,
    string? SourceCorrelationId,
    DateTimeOffset CreatedAtUtc);

namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaSnapshotInput(
    string ServerId,
    string UpstreamUrl,
    string McpProtocol,
    IReadOnlyCollection<ToolSchemaSnapshotEntry> Tools,
    string? PolicyVersion = null,
    string? SourceCorrelationId = null);

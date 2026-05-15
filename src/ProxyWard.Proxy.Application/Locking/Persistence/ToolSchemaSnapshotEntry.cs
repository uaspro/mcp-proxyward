using ProxyWard.Locking.Tools;

namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaSnapshotEntry(
    string ToolName,
    ToolFingerprint Fingerprint,
    string? Title = null,
    string? Description = null,
    string? InputSchemaJson = null,
    string? OutputSchemaJson = null);

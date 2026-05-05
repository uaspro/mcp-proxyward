using ProxyWard.Locking.Tools;

namespace ProxyWard.Locking.Persistence;

public sealed record ToolSchemaSnapshotEntry(string ToolName, ToolFingerprint Fingerprint);

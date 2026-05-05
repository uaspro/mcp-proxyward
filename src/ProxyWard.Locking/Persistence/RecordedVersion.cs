namespace ProxyWard.Locking.Persistence;

public sealed record RecordedVersion(
    int Version,
    string SnapshotHash,
    bool WasNewVersion,
    DateTimeOffset CreatedAtUtc,
    bool UpstreamChanged = false,
    string? PreviousUpstreamUrl = null,
    string? CurrentUpstreamUrl = null);

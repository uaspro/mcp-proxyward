namespace ProxyWard.Locking.Lockfiles;

public sealed record ToolSurfaceDrift(
    string ToolName,
    IReadOnlyCollection<string> Reasons);

public sealed record ToolSurfaceDriftResult(
    IReadOnlyList<ToolSurfaceDrift> Drifts,
    IReadOnlyCollection<string> Reasons,
    int Version,
    bool WasNewVersion,
    SchemaLockWriteFailure? WriteFailure = null,
    bool UpstreamChanged = false,
    string? PreviousUpstreamUrl = null,
    string? CurrentUpstreamUrl = null)
{
    public bool HasDrift => Drifts.Count > 0;
    public bool Skipped => WriteFailure is not null;

    public static ToolSurfaceDriftResult NoDrift(int version, bool wasNewVersion) =>
        new([], [], version, wasNewVersion);

    public static ToolSurfaceDriftResult SkippedForWriteFailure(string reason) =>
        new([], [], 0, WasNewVersion: false, new SchemaLockWriteFailure(reason));
}

public sealed record SchemaLockWriteFailure(string Reason);

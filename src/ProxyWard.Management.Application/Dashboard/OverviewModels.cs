namespace ProxyWard.Management.Application.Dashboard;

public sealed record OverviewQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketSeconds,
    int TopReasonsLimit,
    int TopToolsLimit);

public sealed record OverviewWindow(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double DurationSeconds);

public sealed record OverviewBucket(
    int SizeSeconds,
    int Count);

public sealed record OverviewMetadata(
    string Source,
    DateTimeOffset? AsOfUtc,
    bool Partial,
    string? Notes);

public sealed record OverviewTopRow(
    string Key,
    long Count);

public sealed record OverviewSeriesPoint(
    DateTimeOffset BucketStartUtc,
    int Allow,
    int Block,
    int WouldBlock,
    int Warn,
    int Total);

public sealed record OverviewResponse(
    OverviewWindow Window,
    OverviewBucket Bucket,
    OverviewMetadata Metadata,
    double RequestRate,
    double BlockRate,
    double WouldBlockRate,
    double ErrorRate,
    long? LatencyP95Ms,
    IReadOnlyList<OverviewTopRow> TopReasons,
    IReadOnlyList<OverviewTopRow> TopTools,
    IReadOnlyList<OverviewSeriesPoint> Series);

public interface IProxyTelemetryReader
{
    Task<OverviewResponse> GetOverviewAsync(OverviewQuery query, CancellationToken cancellationToken);
}

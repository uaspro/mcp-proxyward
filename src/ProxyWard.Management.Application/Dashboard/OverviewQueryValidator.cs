namespace ProxyWard.Management.Application.Dashboard;

public static class OverviewQueryValidator
{
    private const int MinBucketSeconds = 10;
    private const int DefaultBucketSeconds = 60;
    private const int DefaultTopN = 5;
    private const int MaxTopN = 50;

    private static readonly TimeSpan MinWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    public static OverviewValidation Validate(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? bucketSeconds,
        int? topReasons,
        int? topTools,
        DateTimeOffset now)
    {
        DateTimeOffset effectiveFrom;
        DateTimeOffset effectiveTo;

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            effectiveFrom = fromUtc.Value;
            effectiveTo = toUtc.Value;
        }
        else if (!fromUtc.HasValue && !toUtc.HasValue)
        {
            effectiveTo = now;
            effectiveFrom = now - DefaultWindow;
        }
        else
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "fromUtc and toUtc must be provided together.");
        }

        if (effectiveTo < effectiveFrom)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "toUtc must be greater than or equal to fromUtc.");
        }

        if (effectiveTo > now + ClockSkewTolerance)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "toUtc is too far in the future.");
        }

        var windowDuration = effectiveTo - effectiveFrom;
        if (windowDuration < MinWindow)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "window duration is below the supported minimum.");
        }

        if (windowDuration > MaxWindow)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "window duration is above the supported maximum.");
        }

        var effectiveBucket = bucketSeconds ?? DefaultBucketSeconds;
        if (effectiveBucket < MinBucketSeconds)
        {
            return OverviewValidation.Failure(
                "bucket_invalid",
                "bucketSeconds is below the supported minimum.");
        }

        if (effectiveBucket * 2.0 > windowDuration.TotalSeconds)
        {
            return OverviewValidation.Failure(
                "bucket_invalid",
                "bucketSeconds must not exceed half the window duration.");
        }

        var effectiveTopReasons = topReasons ?? DefaultTopN;
        var effectiveTopTools = topTools ?? DefaultTopN;
        if (effectiveTopReasons < 1 || effectiveTopReasons > MaxTopN
            || effectiveTopTools < 1 || effectiveTopTools > MaxTopN)
        {
            return OverviewValidation.Failure(
                "topn_invalid",
                "topReasons and topTools must be in the range [1, 50].");
        }

        return OverviewValidation.Success(new OverviewQuery(
            FromUtc: effectiveFrom,
            ToUtc: effectiveTo,
            BucketSeconds: effectiveBucket,
            TopReasonsLimit: effectiveTopReasons,
            TopToolsLimit: effectiveTopTools));
    }
}

public readonly record struct OverviewValidation(string? Error, string? Message, OverviewQuery? Query)
{
    public static OverviewValidation Success(OverviewQuery query) => new(null, null, query);
    public static OverviewValidation Failure(string error, string message) => new(error, message, null);
}

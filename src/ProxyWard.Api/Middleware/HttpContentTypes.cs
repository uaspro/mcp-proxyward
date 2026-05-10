namespace ProxyWard.Api.Middleware;

internal static class HttpContentTypes
{
    public static bool IsJson(string? contentType)
    {
        var mediaType = Sanitize(contentType);
        return IsJsonMediaType(mediaType);
    }

    public static bool IsInspectableResponse(string? contentType)
    {
        var mediaType = Sanitize(contentType);
        return IsJsonMediaType(mediaType)
            || mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStreaming(string? contentType) =>
        Sanitize(contentType).Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);

    public static string Sanitize(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = separatorIndex >= 0 ? contentType[..separatorIndex] : contentType;
        return mediaType.Trim();
    }

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
}

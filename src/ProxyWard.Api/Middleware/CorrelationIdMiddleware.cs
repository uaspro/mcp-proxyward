using ProxyWard.Api.Observability;

namespace ProxyWard.Api.Middleware;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    private const string HeaderName = "X-Correlation-Id";
    private const int MaxCorrelationIdLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[AuditItems.CorrelationId] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            [ProxyWardTelemetry.ServiceNameTag] = ProxyWardTelemetry.ServiceName,
            [ProxyWardTelemetry.CorrelationIdTag] = correlationId
        });

        await next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValue)
            && IsAcceptable(headerValue.ToString(), out var fromHeader))
        {
            return fromHeader;
        }

        if (IsAcceptable(context.TraceIdentifier, out var fromTrace))
        {
            return fromTrace;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsAcceptable(string? value, out string sanitized)
    {
        sanitized = string.Empty;

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxCorrelationIdLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!IsAllowedCharacter(ch))
            {
                return false;
            }
        }

        sanitized = value;
        return true;
    }

    private static bool IsAllowedCharacter(char ch) =>
        (ch >= 'A' && ch <= 'Z')
        || (ch >= 'a' && ch <= 'z')
        || (ch >= '0' && ch <= '9')
        || ch == '.'
        || ch == '_'
        || ch == '-'
        || ch == ':';
}

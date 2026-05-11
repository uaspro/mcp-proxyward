using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Security;

namespace ProxyWard.Management.Api.Security;

public sealed class ManagementWriteAuthorization
{
    private readonly IManagementSecurityAuditWriter _auditWriter;
    private readonly ILogger<ManagementWriteAuthorization> _logger;
    private readonly ManagementApiOptions _options;

    public ManagementWriteAuthorization(
        ManagementApiOptions options,
        IManagementSecurityAuditWriter auditWriter,
        ILogger<ManagementWriteAuthorization> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (TryAuthorize(context, out var failureReason))
        {
            return true;
        }

        stopwatch.Stop();

        _logger.LogWarning(
            "Management write authorization failed for {Method} {Path} from {RemoteIp}. Reason: {Reason}.",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            failureReason);

        await _auditWriter
            .WriteAuthorizationFailureAsync(CreateFailure(context, failureReason, stopwatch.ElapsedMilliseconds), cancellationToken)
            .ConfigureAwait(false);

        return false;
    }

    private bool TryAuthorize(
        HttpContext context,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (_options.LocalDevelopmentMode)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_options.AdminToken))
        {
            failureReason = "admin_token_not_configured";
            return false;
        }

        var headerValue = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "authorization_header_missing";
            return false;
        }

        var suppliedToken = headerValue[bearerPrefix.Length..].Trim();
        if (suppliedToken.Length == 0)
        {
            failureReason = "bearer_token_missing";
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(_options.AdminToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        if (expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            return true;
        }

        failureReason = "bearer_token_invalid";
        return false;
    }

    private static ManagementAuthorizationFailure CreateFailure(HttpContext context, string reason, long durationMs) =>
        new(
            TimestampUtc: DateTimeOffset.UtcNow,
            Method: context.Request.Method,
            Path: context.Request.Path.Value ?? string.Empty,
            RemoteIp: context.Connection.RemoteIpAddress?.ToString(),
            Reason: reason,
            CorrelationId: context.TraceIdentifier,
            RequestBytes: context.Request.ContentLength ?? 0,
            DurationMs: durationMs);
}

using System.Security.Cryptography;
using System.Text;
using ProxyWard.Management.Application;

namespace ProxyWard.Management.Api.Security;

internal static class ManagementWriteAuthorization
{
    public static async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        ManagementApiOptions options,
        ManagementSecurityAuditService securityAuditService,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (TryAuthorize(context, options, out var failureReason))
        {
            return true;
        }

        logger.LogWarning(
            "Management write authorization failed for {Method} {Path} from {RemoteIp}. Reason: {Reason}.",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            failureReason);

        await securityAuditService
            .RecordAuthorizationFailureAsync(context, failureReason, cancellationToken)
            .ConfigureAwait(false);

        return false;
    }

    private static bool TryAuthorize(
        HttpContext context,
        ManagementApiOptions options,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (options.LocalDevelopmentMode)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.AdminToken))
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

        var expectedBytes = Encoding.UTF8.GetBytes(options.AdminToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        if (expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            return true;
        }

        failureReason = "bearer_token_invalid";
        return false;
    }
}

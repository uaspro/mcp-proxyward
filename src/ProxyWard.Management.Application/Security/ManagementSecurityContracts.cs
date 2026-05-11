namespace ProxyWard.Management.Application.Security;

public sealed record ManagementAuthorizationFailure(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    string? RemoteIp,
    string Reason,
    string CorrelationId,
    long RequestBytes,
    long DurationMs);

public interface IManagementSecurityAuditWriter
{
    Task WriteAuthorizationFailureAsync(
        ManagementAuthorizationFailure failure,
        CancellationToken cancellationToken);
}

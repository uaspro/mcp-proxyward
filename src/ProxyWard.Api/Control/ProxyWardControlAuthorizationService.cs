using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ProxyWard.Api.Runtime;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Control;

public sealed class ProxyWardControlAuthorizationService
{
    private readonly IAuditSink _auditSink;
    private readonly ILogger<ProxyWardControlAuthorizationService> _logger;
    private readonly IProxyWardPolicyProvider _policyProvider;

    public ProxyWardControlAuthorizationService(
        IAuditSink auditSink,
        ILogger<ProxyWardControlAuthorizationService> logger,
        IProxyWardPolicyProvider policyProvider)
    {
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
    }

    public async Task<bool> AuthorizeAsync(
        HttpContext context,
        ProxyWardControlOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (IsAuthorized(context, options, out var failureReason))
        {
            return true;
        }

        stopwatch.Stop();

        await RecordAuthorizationFailureAsync(
            context,
            _policyProvider.Current,
            failureReason,
            stopwatch.ElapsedMilliseconds,
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task RecordAuthorizationFailureAsync(
        HttpContext context,
        ProxyWardPolicy policy,
        string reason,
        long durationMs,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Proxy control authorization failed for {Method} {Path} from {RemoteIp}. Reason: {Reason}.",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            reason);

        try
        {
            await _auditSink.WriteAsync(
                new AuditEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: "proxy_control_auth_failure",
                    Mode: FormatMode(policy.Mode),
                    Decision: AuditDecision.Block,
                    ServerId: "proxy-control",
                    Method: $"{context.Request.Method} {context.Request.Path}",
                    ToolName: null,
                    Reasons: [reason],
                    PolicyVersion: policy.VersionHash,
                    CorrelationId: context.TraceIdentifier,
                    RequestBytes: context.Request.ContentLength ?? 0,
                    DurationMs: durationMs,
                    ArgumentSummary: null,
                    BatchSize: 1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "ProxyWard audit sink failed to record proxy control authorization failure.");
        }
    }

    private static bool IsAuthorized(
        HttpContext context,
        ProxyWardControlOptions options,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            failureReason = "control_token_not_configured";
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

        var expectedBytes = Encoding.UTF8.GetBytes(options.Token);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);

        if (expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            return true;
        }

        failureReason = "bearer_token_invalid";
        return false;
    }

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";
}

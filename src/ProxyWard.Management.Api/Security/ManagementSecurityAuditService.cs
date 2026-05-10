using System.Text.Json.Nodes;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;

namespace ProxyWard.Management.Api.Security;

public sealed class ManagementSecurityAuditService
{
    private readonly ManagementApiOptions _options;
    private readonly ILogger<ManagementSecurityAuditService> _logger;

    public ManagementSecurityAuditService(
        ManagementApiOptions options,
        ILogger<ManagementSecurityAuditService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAuthorizationFailureAsync(
        HttpContext context,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            using var sink = new SqliteAuditSink(_options.AuditDatabasePath);
            await sink.WriteAsync(
                new AuditEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    EventType: "management_auth_failure",
                    Mode: "management",
                    Decision: AuditDecision.Block,
                    ServerId: "management",
                    Method: $"{context.Request.Method} {context.Request.Path}",
                    ToolName: null,
                    Reasons: [reason],
                    PolicyVersion: "management",
                    CorrelationId: context.TraceIdentifier,
                    RequestBytes: context.Request.ContentLength ?? 0,
                    DurationMs: 0,
                    ArgumentSummary: CreatePayload(context, reason),
                    BatchSize: 1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Management auth failure audit write failed for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
        }
    }

    private static JsonNode CreatePayload(HttpContext context, string reason) =>
        new JsonObject
        {
            ["reason"] = reason,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value,
            ["remoteIp"] = context.Connection.RemoteIpAddress?.ToString()
        };
}

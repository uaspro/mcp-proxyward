using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Security;

namespace ProxyWard.Management.Infrastructure.Security;

public sealed class SqliteManagementSecurityAuditWriter : IManagementSecurityAuditWriter
{
    private readonly ManagementApiOptions _options;
    private readonly ILogger<SqliteManagementSecurityAuditWriter> _logger;

    public SqliteManagementSecurityAuditWriter(
        ManagementApiOptions options,
        ILogger<SqliteManagementSecurityAuditWriter> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAuthorizationFailureAsync(
        ManagementAuthorizationFailure failure,
        CancellationToken cancellationToken)
    {
        try
        {
            using var sink = new SqliteAuditSink(_options.AuditDatabasePath);
            await sink.WriteAsync(
                new AuditEvent(
                    Timestamp: failure.TimestampUtc,
                    EventType: "management_auth_failure",
                    Mode: "management",
                    Decision: AuditDecision.Block,
                    ServerId: "management",
                    Method: $"{failure.Method} {failure.Path}",
                    ToolName: null,
                    Reasons: [failure.Reason],
                    PolicyVersion: "management",
                    CorrelationId: failure.CorrelationId,
                    RequestBytes: failure.RequestBytes,
                    DurationMs: failure.DurationMs,
                    ArgumentSummary: CreatePayload(failure),
                    BatchSize: 1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Management auth failure audit write failed for {Method} {Path}.",
                failure.Method,
                failure.Path);
        }
    }

    private static JsonNode CreatePayload(ManagementAuthorizationFailure failure) =>
        new JsonObject
        {
            ["reason"] = failure.Reason,
            ["method"] = failure.Method,
            ["path"] = failure.Path,
            ["remoteIp"] = failure.RemoteIp
        };
}

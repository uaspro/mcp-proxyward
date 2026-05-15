using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application.Security;

namespace ProxyWard.Management.Infrastructure.Security;

public sealed class PostgresManagementSecurityAuditWriter : IManagementSecurityAuditWriter, IAsyncDisposable, IDisposable
{
    private readonly PostgresAuditSink _sink;
    private readonly ILogger<PostgresManagementSecurityAuditWriter> _logger;

    public PostgresManagementSecurityAuditWriter(
        string connectionString,
        ILogger<PostgresManagementSecurityAuditWriter> logger)
        : this(new PostgresAuditSink(connectionString), logger)
    {
    }

    public PostgresManagementSecurityAuditWriter(
        NpgsqlDataSource dataSource,
        ILogger<PostgresManagementSecurityAuditWriter> logger)
        : this(new PostgresAuditSink(dataSource), logger)
    {
    }

    private PostgresManagementSecurityAuditWriter(
        PostgresAuditSink sink,
        ILogger<PostgresManagementSecurityAuditWriter> logger)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAuthorizationFailureAsync(
        ManagementAuthorizationFailure failure,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sink.WriteAsync(
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

    public ValueTask DisposeAsync() => _sink.DisposeAsync();

    public void Dispose() => _sink.Dispose();
}

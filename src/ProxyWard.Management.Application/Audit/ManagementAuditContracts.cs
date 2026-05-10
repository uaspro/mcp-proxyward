using System.Text.Json.Nodes;

namespace ProxyWard.Management.Application.Audit;

public sealed record ManagementAuditReadOptions(
    int MaxPageSize = 200,
    int MaxExportRowCount = 50_000,
    int MaxOverviewSampleSize = 100_000);

public sealed record ManagementAuditEventQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Decision = null,
    string? ServerId = null,
    string? Method = null,
    string? ToolName = null,
    string? CorrelationId = null,
    string? SearchText = null,
    int Offset = 0,
    int PageSize = 50);

public sealed record ManagementAuditEventPage(
    int Offset,
    int PageSize,
    long TotalCount,
    IReadOnlyList<ManagementAuditEventItem> Items);

public sealed record ManagementAuditEventItem(
    long Id,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Mode,
    string Decision,
    string ServerId,
    string? Method,
    string? ToolName,
    IReadOnlyList<string> Reasons,
    string PolicyVersion,
    string CorrelationId,
    long RequestBytes,
    long DurationMs,
    JsonNode? ArgumentSummary);

public interface IManagementAuditEventRepository
{
    Task<ManagementAuditEventPage> QueryAsync(
        ManagementAuditEventQuery query,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ManagementAuditEventItem> StreamAsync(
        ManagementAuditEventQuery query,
        CancellationToken cancellationToken);

    Task<ManagementAuditEventItem?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken);
}

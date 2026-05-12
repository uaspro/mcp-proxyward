namespace ProxyWard.Management.Application.Drift;

public sealed record ManagementSchemaDriftQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Status = null,
    string? ServerId = null,
    string? ToolName = null,
    int Offset = 0,
    int PageSize = 50);

public sealed record ManagementSchemaDriftWindow(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public sealed record ManagementSchemaDriftPage(
    int Offset,
    int PageSize,
    long TotalCount,
    ManagementSchemaDriftWindow Window,
    IReadOnlyList<ManagementSchemaDriftItem> Items);

public sealed record ManagementSchemaDriftFilterOption(
    string Value,
    long Count);

public sealed record ManagementSchemaDriftFilterOptions(
    IReadOnlyList<ManagementSchemaDriftFilterOption> Servers,
    IReadOnlyList<ManagementSchemaDriftFilterOption> Tools);

public sealed record ManagementSchemaDriftItem(
    long Id,
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    string Status,
    IReadOnlyList<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote,
    long ImpactCount,
    bool HasDiffMetadata,
    string DiffMode);

public sealed record ManagementSchemaDriftDetail(
    long Id,
    string ServerId,
    string ToolName,
    string FieldName,
    int FromVersion,
    int ToVersion,
    string Status,
    IReadOnlyList<string> Reasons,
    string? PolicyVersion,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote,
    long ImpactCount,
    bool HasDiffMetadata,
    string DiffMode,
    ManagementSchemaDriftDiff Diff);

public sealed record ManagementSchemaDriftDiff(
    string? BeforeJson,
    string? AfterJson,
    string BeforeHash,
    string AfterHash,
    DateTimeOffset? CreatedAtUtc,
    string Mode);

public sealed record ManagementSchemaDriftActionRequest(
    string? ReviewedBy = null,
    string? ReviewNote = null);

public interface IManagementSchemaDriftRepository
{
    Task<ManagementSchemaDriftPage> QueryAsync(
        ManagementSchemaDriftQuery query,
        CancellationToken cancellationToken);

    Task<ManagementSchemaDriftFilterOptions> GetFilterOptionsAsync(
        CancellationToken cancellationToken);

    Task<ManagementSchemaDriftDetail?> GetByIdAsync(
        long id,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken);
}

public interface IManagementSchemaDriftActionService
{
    Task<ManagementSchemaDriftDetail?> ApplyAsync(
        long id,
        string action,
        ManagementSchemaDriftActionRequest? request,
        CancellationToken cancellationToken);
}

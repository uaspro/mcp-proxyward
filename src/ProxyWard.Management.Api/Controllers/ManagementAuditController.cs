using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProxyWard.Management.Application.Audit;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/audit")]
public sealed class ManagementAuditController : ControllerBase
{
    private const string AuditExportFileName = "proxyward-audit-events.ndjson";
    private const string AuditExportContentType = "application/x-ndjson";
    private const int AuditExportFlushEveryRows = 100;
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    private readonly IManagementAuditEventRepository _repository;
    private readonly IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> _jsonOptions;

    public ManagementAuditController(
        IManagementAuditEventRepository repository,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
    }

    [HttpGet("events")]
    public async Task<IActionResult> QueryEvents(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? decision,
        [FromQuery] string? serverId,
        [FromQuery] string? method,
        [FromQuery] string? toolName,
        [FromQuery] string? correlationId,
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = new ManagementAuditEventQuery(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Decision: decision,
            ServerId: serverId,
            Method: method,
            ToolName: toolName,
            CorrelationId: correlationId,
            SearchText: search,
            Offset: offset ?? 0,
            PageSize: pageSize ?? 50);

        return Ok(await _repository.QueryAsync(query, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("events/{id:long}")]
    public async Task<IActionResult> GetEvent(long id, CancellationToken cancellationToken)
    {
        var auditEvent = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return auditEvent is null
            ? NotFound(new
            {
                error = "audit_event_not_found",
                id
            })
            : Ok(auditEvent);
    }

    [HttpGet("export.ndjson")]
    public async Task ExportNdjson(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? decision,
        [FromQuery] string? serverId,
        [FromQuery] string? method,
        [FromQuery] string? toolName,
        [FromQuery] string? correlationId,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = new ManagementAuditEventQuery(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Decision: decision,
            ServerId: serverId,
            Method: method,
            ToolName: toolName,
            CorrelationId: correlationId,
            SearchText: search);

        await using var enumerator = _repository
            .StreamAsync(query, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = AuditExportContentType;
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{AuditExportFileName}\"";

        var serializerOptions = new JsonSerializerOptions(_jsonOptions.Value.SerializerOptions)
        {
            WriteIndented = false
        };
        var responseBody = Response.Body;

        var rowsSinceFlush = 0;
        while (hasNext)
        {
            await JsonSerializer
                .SerializeAsync(responseBody, enumerator.Current, serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            await responseBody.WriteAsync(NewLineBytes, cancellationToken).ConfigureAwait(false);

            rowsSinceFlush++;
            if (rowsSinceFlush >= AuditExportFlushEveryRows)
            {
                await responseBody.FlushAsync(cancellationToken).ConfigureAwait(false);
                rowsSinceFlush = 0;
            }

            hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }

        if (rowsSinceFlush > 0)
        {
            await responseBody.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

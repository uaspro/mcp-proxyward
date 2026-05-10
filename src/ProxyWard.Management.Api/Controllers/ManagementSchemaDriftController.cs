using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Drift;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/schema/drifts")]
public sealed class ManagementSchemaDriftController : ControllerBase
{
    private readonly IManagementSchemaDriftActionService _actionService;
    private readonly ManagementApiOptions _managementOptions;
    private readonly IManagementSchemaDriftRepository _repository;
    private readonly ManagementSecurityAuditService _securityAuditService;
    private readonly ILogger<ManagementSchemaDriftController> _logger;

    public ManagementSchemaDriftController(
        IManagementSchemaDriftActionService actionService,
        ManagementApiOptions managementOptions,
        IManagementSchemaDriftRepository repository,
        ManagementSecurityAuditService securityAuditService,
        ILogger<ManagementSchemaDriftController> logger)
    {
        _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
        _managementOptions = managementOptions ?? throw new ArgumentNullException(nameof(managementOptions));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _securityAuditService = securityAuditService ?? throw new ArgumentNullException(nameof(securityAuditService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? status,
        [FromQuery] string? serverId,
        [FromQuery] string? toolName,
        [FromQuery] int? offset,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (!IsValidDriftStatus(status))
        {
            return BadRequest(new
            {
                error = "schema_drift_status_invalid",
                message = "status must be one of pending, approved, rejected, or blocked."
            });
        }

        var query = new ManagementSchemaDriftQuery(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Status: status,
            ServerId: serverId,
            ToolName: toolName,
            Offset: offset ?? 0,
            PageSize: pageSize ?? 50);

        return Ok(await _repository.QueryAsync(query, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetDetail(
        long id,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        var detail = await _repository.GetByIdAsync(id, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? NotFound(new
            {
                error = "schema_drift_not_found",
                id
            })
            : Ok(detail);
    }

    [HttpPost("{id:long}/approve")]
    public Task<IActionResult> Approve(long id, CancellationToken cancellationToken) =>
        ApplySchemaDriftActionAsync(id, "approve", cancellationToken);

    [HttpPost("{id:long}/reject")]
    public Task<IActionResult> Reject(long id, CancellationToken cancellationToken) =>
        ApplySchemaDriftActionAsync(id, "reject", cancellationToken);

    [HttpPost("{id:long}/block")]
    public Task<IActionResult> Block(long id, CancellationToken cancellationToken) =>
        ApplySchemaDriftActionAsync(id, "block", cancellationToken);

    private async Task<IActionResult> ApplySchemaDriftActionAsync(
        long id,
        string action,
        CancellationToken cancellationToken)
    {
        if (!await ManagementWriteAuthorization.IsAuthorizedAsync(
                HttpContext,
                _managementOptions,
                _securityAuditService,
                _logger,
                cancellationToken).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        ManagementSchemaDriftActionRequest? request;
        try
        {
            request = await ReadOptionalActionRequestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest(new
            {
                error = "request_json_invalid",
                message = ex.Message
            });
        }

        var detail = await _actionService
            .ApplyAsync(id, action, request, cancellationToken)
            .ConfigureAwait(false);
        return detail is null
            ? NotFound(new
            {
                error = "schema_drift_not_found",
                id
            })
            : Ok(detail);
    }

    private async Task<ManagementSchemaDriftActionRequest?> ReadOptionalActionRequestAsync(
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is null or 0)
        {
            return null;
        }

        return await Request
            .ReadFromJsonAsync<ManagementSchemaDriftActionRequest>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsValidDriftStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        return status.Trim() switch
        {
            "pending" or "approved" or "rejected" or "blocked" => true,
            _ => false
        };
    }
}

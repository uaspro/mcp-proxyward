using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Policy;
using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/policy")]
public sealed class ManagementPolicyController : ControllerBase
{
    private readonly ManagementApiOptions _managementOptions;
    private readonly ManagementPolicyApplyService _applyService;
    private readonly ManagementPolicyModeService _modeService;
    private readonly ManagementPolicyReader _policyReader;
    private readonly ManagementPolicyValidationService _validationService;
    private readonly ManagementSecurityAuditService _securityAuditService;
    private readonly ILogger<ManagementPolicyController> _logger;

    public ManagementPolicyController(
        ManagementApiOptions managementOptions,
        ManagementPolicyApplyService applyService,
        ManagementPolicyModeService modeService,
        ManagementPolicyReader policyReader,
        ManagementPolicyValidationService validationService,
        ManagementSecurityAuditService securityAuditService,
        ILogger<ManagementPolicyController> logger)
    {
        _managementOptions = managementOptions ?? throw new ArgumentNullException(nameof(managementOptions));
        _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
        _modeService = modeService ?? throw new ArgumentNullException(nameof(modeService));
        _policyReader = policyReader ?? throw new ArgumentNullException(nameof(policyReader));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _securityAuditService = securityAuditService ?? throw new ArgumentNullException(nameof(securityAuditService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _policyReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return Ok(response);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = "policy_not_found", path = ex.FileName });
        }
        catch (PolicyValidationException ex)
        {
            return Problem(
                title: "Invalid ProxyWard policy",
                detail: string.Join("; ", ex.Errors),
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = "policy_invalid",
                    ["errors"] = ex.Errors
                });
        }
        catch (IOException ex)
        {
            return Problem(
                title: "Policy database could not be read",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = "policy_read_failed"
                });
        }
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _validationService
                .ValidateAsync(CreatePolicyValidationRequest(), cancellationToken)
                .ConfigureAwait(false);
            return Ok(result);
        }
        catch (ManagementPolicyValidationRequestException ex)
        {
            return BadRequest(new
            {
                error = "policy_validation_request_invalid",
                message = ex.Message
            });
        }
    }

    [HttpPut]
    public async Task<IActionResult> Apply(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(cancellationToken).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        try
        {
            var outcome = await _applyService
                .ApplyAsync(CreatePolicyValidationRequest(), cancellationToken)
                .ConfigureAwait(false);

            return outcome.IsApplied
                ? Ok(outcome.Response)
                : BadRequest(new
                {
                    error = "policy_validation_failed",
                    validation = outcome.ValidationFailure
                });
        }
        catch (ManagementPolicyValidationRequestException ex)
        {
            return BadRequest(new
            {
                error = "policy_apply_request_invalid",
                message = ex.Message
            });
        }
        catch (ManagementPolicyApplyException ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    error = "policy_apply_failed",
                    phase = ex.Phase,
                    message = ex.Message,
                    rollbackAttempted = ex.RollbackAttempted,
                    rollbackApplied = ex.RollbackApplied
                });
        }
    }

    [HttpGet("impact")]
    public async Task<IActionResult> GetImpact(
        [FromQuery] string? mode,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _modeService
                .GetImpactAsync(mode, fromUtc, toUtc, cancellationToken)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (ManagementPolicyModeRequestException ex)
        {
            return BadRequest(new
            {
                error = ex.Error,
                message = ex.Message
            });
        }
        catch (ProxyControlClientException ex)
        {
            return Problem(
                title: "Proxy control request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = ex.Error,
                    ["proxyStatusCode"] = ex.StatusCode
                });
        }
    }

    [HttpPatch("mode")]
    public async Task<IActionResult> SwitchMode(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(cancellationToken).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        ManagementPolicyModeSwitchRequest? request;
        try
        {
            request = await Request
                .ReadFromJsonAsync<ManagementPolicyModeSwitchRequest>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest(new
            {
                error = "mode_switch_request_invalid",
                message = ex.Message
            });
        }

        try
        {
            var response = await _modeService
                .SwitchModeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (ManagementPolicyModeRequestException ex)
        {
            return BadRequest(new
            {
                error = ex.Error,
                message = ex.Message
            });
        }
        catch (ProxyControlClientException ex)
        {
            return Problem(
                title: "Proxy control request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = ex.Error,
                    ["proxyStatusCode"] = ex.StatusCode
                });
        }
    }

    private Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken) =>
        ManagementWriteAuthorization.IsAuthorizedAsync(
            HttpContext,
            _managementOptions,
            _securityAuditService,
            _logger,
            cancellationToken);

    private ManagementPolicyValidationRequest CreatePolicyValidationRequest() =>
        new(Request.Body, Request.ContentType);
}

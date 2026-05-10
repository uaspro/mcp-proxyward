using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Application.Settings;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class ManagementSettingsController : ControllerBase
{
    private readonly ManagementSettingsService _settingsService;

    public ManagementSettingsController(ManagementSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
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
}

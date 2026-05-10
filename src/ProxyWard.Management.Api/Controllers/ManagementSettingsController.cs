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
            return this.PolicyNotFound(ex);
        }
        catch (PolicyValidationException ex)
        {
            return this.InvalidPolicy(ex);
        }
        catch (IOException ex)
        {
            return this.PolicyReadFailed(ex);
        }
    }
}

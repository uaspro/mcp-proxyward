using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Application.Status;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/status")]
public sealed class ManagementStatusController : ControllerBase
{
    private readonly ManagementStatusService _statusService;

    public ManagementStatusController(ManagementStatusService statusService)
    {
        _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var response = await _statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }
}

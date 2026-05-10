using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Api.Dashboard;
using ProxyWard.Management.Application.Dashboard;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/overview")]
public sealed class ManagementOverviewController : ControllerBase
{
    private readonly IProxyTelemetryReader _reader;

    public ManagementOverviewController(IProxyTelemetryReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int? bucketSeconds,
        [FromQuery] int? topReasons,
        [FromQuery] int? topTools,
        CancellationToken cancellationToken)
    {
        var validation = OverviewQueryValidator.Validate(
            fromUtc,
            toUtc,
            bucketSeconds,
            topReasons,
            topTools,
            DateTimeOffset.UtcNow);

        if (validation.Error is not null)
        {
            return BadRequest(new { error = validation.Error, message = validation.Message });
        }

        var response = await _reader.GetOverviewAsync(validation.Query!, cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }
}

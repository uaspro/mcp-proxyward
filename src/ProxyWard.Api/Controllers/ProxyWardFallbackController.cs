using Microsoft.AspNetCore.Mvc;

namespace ProxyWard.Api.Controllers;

[ApiController]
public sealed class ProxyWardFallbackController : ControllerBase
{
    [HttpGet("{**path}", Order = int.MaxValue)]
    [HttpPost("{**path}", Order = int.MaxValue)]
    [HttpPut("{**path}", Order = int.MaxValue)]
    [HttpPatch("{**path}", Order = int.MaxValue)]
    [HttpDelete("{**path}", Order = int.MaxValue)]
    [HttpOptions("{**path}", Order = int.MaxValue)]
    public IActionResult NotFoundFallback() =>
        NotFound(new
        {
            error = "No MCP proxy route configured for this request."
        });
}

using Microsoft.AspNetCore.Mvc;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
public sealed class ManagementRootController : ControllerBase
{
    [HttpGet("/")]
    public IActionResult Root() => Redirect("/api/status");
}

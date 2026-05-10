using Microsoft.AspNetCore.Mvc;
using ProxyWard.Api.Runtime;

namespace ProxyWard.Api.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/")]
    public IActionResult Root() => Redirect("/health");

    [HttpGet("/health")]
    public IActionResult GetHealth([FromServices] IProxyWardPolicyProvider policyProvider)
    {
        var loadedPolicy = policyProvider.Current;
        return Ok(new
        {
            status = "healthy",
            service = "MCP ProxyWard",
            mode = loadedPolicy.Mode.ToString().ToLowerInvariant(),
            policyVersion = loadedPolicy.VersionHash,
            serverCount = loadedPolicy.Servers.Count
        });
    }
}

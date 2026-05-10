using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application.Tools;

namespace ProxyWard.Management.Api.Controllers;

[ApiController]
[Route("api/tools")]
public sealed class ManagementToolsController : ControllerBase
{
    private readonly IManagementToolDiscoveryService _discoveryService;
    private readonly IManagementToolInventoryRepository _inventoryRepository;
    private readonly ManagementWriteAuthorization _writeAuthorization;

    public ManagementToolsController(
        IManagementToolDiscoveryService discoveryService,
        IManagementToolInventoryRepository inventoryRepository,
        ManagementWriteAuthorization writeAuthorization)
    {
        _discoveryService = discoveryService ?? throw new ArgumentNullException(nameof(discoveryService));
        _inventoryRepository = inventoryRepository ?? throw new ArgumentNullException(nameof(inventoryRepository));
        _writeAuthorization = writeAuthorization ?? throw new ArgumentNullException(nameof(writeAuthorization));
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var response = await _inventoryRepository.GetAsync(cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }

    [HttpPost("discover")]
    public async Task<IActionResult> Discover(CancellationToken cancellationToken)
    {
        if (!await _writeAuthorization.IsAuthorizedAsync(HttpContext, cancellationToken).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        ManagementToolDiscoveryRequest? request;
        try
        {
            request = await Request
                .ReadFromJsonAsync<ManagementToolDiscoveryRequest>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest(new
            {
                error = "tool_discovery_request_invalid",
                message = ex.Message
            });
        }

        try
        {
            var response = await _discoveryService
                .DiscoverAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return Ok(response);
        }
        catch (ManagementToolDiscoveryRequestException ex)
        {
            return BadRequest(new
            {
                error = ex.Error,
                message = ex.Message
            });
        }
        catch (ManagementToolDiscoveryException ex)
        {
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new
                {
                    error = ex.Error,
                    message = ex.Message,
                    upstreamStatusCode = ex.UpstreamStatusCode
                });
        }
    }
}

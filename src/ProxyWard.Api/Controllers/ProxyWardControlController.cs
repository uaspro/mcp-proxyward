using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ProxyWard.Api.Control;
using ProxyWard.Policy.Configuration;
using ProxyWard.Proxy.Application.Runtime;
using ProxyWard.Proxy.Infrastructure.Yarp;

namespace ProxyWard.Api.Controllers;

[ApiController]
[Route("control")]
public sealed class ProxyWardControlController : ControllerBase
{
    private readonly ProxyWardControlAuthorizationService _authorization;
    private readonly ProxyWardControlOptions _options;
    private readonly IProxyWardPolicyProvider _policyProvider;
    private readonly ProxyWardYarpConfigFactory _yarpConfigFactory;
    private readonly IProxyWardYarpConfigProvider _yarpConfigProvider;

    public ProxyWardControlController(
        ProxyWardControlAuthorizationService authorization,
        ProxyWardControlOptions options,
        IProxyWardPolicyProvider policyProvider,
        ProxyWardYarpConfigFactory yarpConfigFactory,
        IProxyWardYarpConfigProvider yarpConfigProvider)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _yarpConfigFactory = yarpConfigFactory ?? throw new ArgumentNullException(nameof(yarpConfigFactory));
        _yarpConfigProvider = yarpConfigProvider ?? throw new ArgumentNullException(nameof(yarpConfigProvider));
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        return Ok(CreateStatusResponse(_policyProvider.Current, _yarpConfigProvider.Version));
    }

    [HttpPut("policy-snapshot")]
    public async Task<IActionResult> ApplyPolicySnapshot(CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var yaml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return BadRequest(new
            {
                error = "policy_validation_failed",
                errors = new[] { "policy snapshot body is required" }
            });
        }

        try
        {
            var replacement = ProxyWardPolicyLoader.Load(yaml);
            _policyProvider.Replace(replacement);
            return Ok(CreateStatusResponse(replacement, _yarpConfigProvider.Version));
        }
        catch (PolicyValidationException ex)
        {
            return BadRequest(new
            {
                error = "policy_validation_failed",
                errors = ex.Errors
            });
        }
    }

    [HttpPatch("mode")]
    public async Task<IActionResult> ApplyMode(CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        ModeApplyRequest? request;
        try
        {
            request = await Request.ReadFromJsonAsync<ModeApplyRequest>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return CreateModeValidationError([$"JSON could not be parsed: {ex.Message}"]);
        }

        if (!TryParseMode(request?.Mode, out var mode))
        {
            return CreateModeValidationError(["mode must be 'audit' or 'enforce'"]);
        }

        var replacement = ProxyWardPolicyLoader.WithMode(_policyProvider.Current, mode);
        _policyProvider.Replace(replacement);
        return Ok(CreateStatusResponse(replacement, _yarpConfigProvider.Version));
    }

    [HttpPut("yarp-config")]
    public async Task<IActionResult> ApplyYarpConfig(CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeAsync(cancellationToken).ConfigureAwait(false);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        YarpConfigApplyRequest? request;
        try
        {
            request = await Request.ReadFromJsonAsync<YarpConfigApplyRequest>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return CreateYarpValidationError([$"JSON could not be parsed: {ex.Message}"]);
        }

        var yarpConfig = _yarpConfigFactory.Build(request);
        if (!yarpConfig.IsValid)
        {
            return CreateYarpValidationError(yarpConfig.Errors);
        }

        _yarpConfigProvider.Replace(yarpConfig.Routes, yarpConfig.Clusters);
        return Ok(CreateYarpStatusResponse(_yarpConfigProvider));
    }

    private async Task<IActionResult?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        return await _authorization.AuthorizeAsync(HttpContext, _options, cancellationToken).ConfigureAwait(false)
            ? null
            : Unauthorized();
    }

    private static object CreateStatusResponse(ProxyWardPolicy policy, int routeVersion) => new
    {
        status = "healthy",
        service = "MCP ProxyWard",
        mode = policy.Mode.ToString().ToLowerInvariant(),
        policyVersion = policy.VersionHash,
        serverCount = policy.Servers.Count,
        routeVersion
    };

    private static object CreateYarpStatusResponse(IProxyWardYarpConfigProvider provider) => new
    {
        status = "accepted",
        routeVersion = provider.Version,
        routeCount = provider.RouteCount,
        clusterCount = provider.ClusterCount
    };

    private BadRequestObjectResult CreateYarpValidationError(IReadOnlyCollection<string> errors) =>
        BadRequest(new
        {
            error = "yarp_config_validation_failed",
            errors
        });

    private BadRequestObjectResult CreateModeValidationError(IReadOnlyCollection<string> errors) =>
        BadRequest(new
        {
            error = "mode_validation_failed",
            errors
        });

    private static bool TryParseMode(string? value, out ProxyWardMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "audit":
                mode = ProxyWardMode.Audit;
                return true;
            case "enforce":
                mode = ProxyWardMode.Enforce;
                return true;
            default:
                mode = ProxyWardMode.Audit;
                return false;
        }
    }
}

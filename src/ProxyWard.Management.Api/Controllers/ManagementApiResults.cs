using Microsoft.AspNetCore.Mvc;
using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Controllers;

internal static class ManagementApiResults
{
    public static IActionResult PolicyNotFound(this ControllerBase controller, FileNotFoundException exception) =>
        controller.NotFound(new
        {
            error = "policy_not_found",
            path = exception.FileName
        });

    public static IActionResult InvalidPolicy(this ControllerBase controller, PolicyValidationException exception) =>
        controller.Problem(
            title: "Invalid ProxyWard policy",
            detail: string.Join("; ", exception.Errors),
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["error"] = "policy_invalid",
                ["errors"] = exception.Errors
            });

    public static IActionResult PolicyReadFailed(this ControllerBase controller, IOException exception) =>
        controller.Problem(
            title: "Policy database could not be read",
            detail: exception.Message,
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?>
            {
                ["error"] = "policy_read_failed"
            });

    public static IActionResult ProxyControlFailed(
        this ControllerBase controller,
        ProxyControlClientException exception) =>
        controller.Problem(
            title: "Proxy control request failed",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway,
            extensions: new Dictionary<string, object?>
            {
                ["error"] = exception.Error,
                ["proxyStatusCode"] = exception.StatusCode
            });
}

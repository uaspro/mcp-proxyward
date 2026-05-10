using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyWard.Management.Api.Dashboard;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Audit;
using ProxyWard.Management.Application.Dashboard;
using ProxyWard.Management.Application.Drift;
using ProxyWard.Management.Application.Policy;
using ProxyWard.Management.Application.Settings;
using ProxyWard.Management.Application.Status;
using ProxyWard.Management.Application.Tools;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Api.Endpoints;

internal static class ManagementApiEndpointExtensions
{
    private const string AuditExportFileName = "proxyward-audit-events.ndjson";
    private const string AuditExportContentType = "application/x-ndjson";
    private const int AuditExportFlushEveryRows = 100;
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    public static IEndpointRouteBuilder MapProxyWardManagementApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/api/status"));
        app.MapStatusEndpoints();
        app.MapSettingsEndpoints();
        app.MapPolicyEndpoints();
        app.MapAuditEndpoints();
        app.MapSchemaDriftEndpoints();
        app.MapToolEndpoints();
        app.MapOverviewEndpoints();

        return app;
    }

    private static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", async (
            ManagementStatusService statusService,
            CancellationToken cancellationToken) =>
        {
            var response = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });
    }

    private static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", async Task<IResult> (
            ManagementSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = "policy_not_found", path = ex.FileName });
            }
            catch (PolicyValidationException ex)
            {
                return Results.Problem(
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
                return Results.Problem(
                    title: "Policy database could not be read",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    extensions: new Dictionary<string, object?>
                    {
                        ["error"] = "policy_read_failed"
                    });
            }
        });
    }

    private static void MapPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/policy", async Task<IResult> (
            ManagementPolicyReader policyReader,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await policyReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = "policy_not_found", path = ex.FileName });
            }
            catch (PolicyValidationException ex)
            {
                return Results.Problem(
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
                return Results.Problem(
                    title: "Policy database could not be read",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError,
                    extensions: new Dictionary<string, object?>
                    {
                        ["error"] = "policy_read_failed"
                    });
            }
        });

        app.MapPost("/api/policy/validate", async Task<IResult> (
            HttpContext context,
            ManagementPolicyValidationService validationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await validationService
                    .ValidateAsync(CreatePolicyValidationRequest(context), cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (ManagementPolicyValidationRequestException ex)
            {
                return Results.BadRequest(new
                {
                    error = "policy_validation_request_invalid",
                    message = ex.Message
                });
            }
        });

        app.MapPut("/api/policy", async Task<IResult> (
            HttpContext context,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            ManagementPolicyApplyService applyService,
            CancellationToken cancellationToken) =>
        {
            if (!await ManagementWriteAuthorization.IsAuthorizedAsync(
                    context,
                    managementOptions,
                    securityAuditService,
                    logger,
                    cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try
            {
                var outcome = await applyService
                    .ApplyAsync(CreatePolicyValidationRequest(context), cancellationToken)
                    .ConfigureAwait(false);

                return outcome.IsApplied
                    ? Results.Ok(outcome.Response)
                    : Results.BadRequest(new
                    {
                        error = "policy_validation_failed",
                        validation = outcome.ValidationFailure
                    });
            }
            catch (ManagementPolicyValidationRequestException ex)
            {
                return Results.BadRequest(new
                {
                    error = "policy_apply_request_invalid",
                    message = ex.Message
                });
            }
            catch (ManagementPolicyApplyException ex)
            {
                return Results.Json(
                    new
                    {
                        error = "policy_apply_failed",
                        phase = ex.Phase,
                        message = ex.Message,
                        rollbackAttempted = ex.RollbackAttempted,
                        rollbackApplied = ex.RollbackApplied
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/policy/impact", async Task<IResult> (
            ManagementPolicyModeService modeService,
            string? mode,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await modeService
                    .GetImpactAsync(mode, fromUtc, toUtc, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ManagementPolicyModeRequestException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Error,
                    message = ex.Message
                });
            }
            catch (ProxyControlClientException ex)
            {
                return Results.Problem(
                    title: "Proxy control request failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?>
                    {
                        ["error"] = ex.Error,
                        ["proxyStatusCode"] = ex.StatusCode
                    });
            }
        });

        app.MapPatch("/api/policy/mode", async Task<IResult> (
            HttpContext context,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            ManagementPolicyModeService modeService,
            CancellationToken cancellationToken) =>
        {
            if (!await ManagementWriteAuthorization.IsAuthorizedAsync(
                    context,
                    managementOptions,
                    securityAuditService,
                    logger,
                    cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            ManagementPolicyModeSwitchRequest? request;
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<ManagementPolicyModeSwitchRequest>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new
                {
                    error = "mode_switch_request_invalid",
                    message = ex.Message
                });
            }

            try
            {
                var response = await modeService
                    .SwitchModeAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ManagementPolicyModeRequestException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Error,
                    message = ex.Message
                });
            }
            catch (ProxyControlClientException ex)
            {
                return Results.Problem(
                    title: "Proxy control request failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?>
                    {
                        ["error"] = ex.Error,
                        ["proxyStatusCode"] = ex.StatusCode
                    });
            }
        });
    }

    private static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit/events", async (
            IManagementAuditEventRepository repository,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? decision,
            string? serverId,
            string? method,
            string? toolName,
            string? correlationId,
            string? search,
            int? offset,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var query = new ManagementAuditEventQuery(
                FromUtc: fromUtc,
                ToUtc: toUtc,
                Decision: decision,
                ServerId: serverId,
                Method: method,
                ToolName: toolName,
                CorrelationId: correlationId,
                SearchText: search,
                Offset: offset ?? 0,
                PageSize: pageSize ?? 50);

            return Results.Ok(await repository.QueryAsync(query, cancellationToken));
        });

        app.MapGet("/api/audit/events/{id:long}", async (
            long id,
            IManagementAuditEventRepository repository,
            CancellationToken cancellationToken) =>
        {
            var auditEvent = await repository.GetByIdAsync(id, cancellationToken);
            return auditEvent is null
                ? Results.NotFound(new
                {
                    error = "audit_event_not_found",
                    id
                })
                : Results.Ok(auditEvent);
        });

        app.MapGet("/api/audit/export.ndjson", async (
            HttpContext httpContext,
            IManagementAuditEventRepository repository,
            IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? decision,
            string? serverId,
            string? method,
            string? toolName,
            string? correlationId,
            string? search,
            CancellationToken cancellationToken) =>
        {
            var query = new ManagementAuditEventQuery(
                FromUtc: fromUtc,
                ToUtc: toUtc,
                Decision: decision,
                ServerId: serverId,
                Method: method,
                ToolName: toolName,
                CorrelationId: correlationId,
                SearchText: search);

            await using var enumerator = repository
                .StreamAsync(query, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            var hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = AuditExportContentType;
            httpContext.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{AuditExportFileName}\"";

            var serializerOptions = new JsonSerializerOptions(jsonOptions.Value.SerializerOptions)
            {
                WriteIndented = false
            };
            var responseBody = httpContext.Response.Body;

            var rowsSinceFlush = 0;
            while (hasNext)
            {
                await JsonSerializer
                    .SerializeAsync(responseBody, enumerator.Current, serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await responseBody.WriteAsync(NewLineBytes, cancellationToken).ConfigureAwait(false);

                rowsSinceFlush++;
                if (rowsSinceFlush >= AuditExportFlushEveryRows)
                {
                    await responseBody.FlushAsync(cancellationToken).ConfigureAwait(false);
                    rowsSinceFlush = 0;
                }

                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }

            if (rowsSinceFlush > 0)
            {
                await responseBody.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        });
    }

    private static void MapSchemaDriftEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/schema/drifts", async (
            IManagementSchemaDriftRepository repository,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? status,
            string? serverId,
            string? toolName,
            int? offset,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidDriftStatus(status))
            {
                return Results.BadRequest(new
                {
                    error = "schema_drift_status_invalid",
                    message = "status must be one of pending, approved, rejected, or blocked."
                });
            }

            var query = new ManagementSchemaDriftQuery(
                FromUtc: fromUtc,
                ToUtc: toUtc,
                Status: status,
                ServerId: serverId,
                ToolName: toolName,
                Offset: offset ?? 0,
                PageSize: pageSize ?? 50);

            return Results.Ok(await repository.QueryAsync(query, cancellationToken).ConfigureAwait(false));
        });

        app.MapGet("/api/schema/drifts/{id:long}", async (
            long id,
            IManagementSchemaDriftRepository repository,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            CancellationToken cancellationToken) =>
        {
            var detail = await repository.GetByIdAsync(id, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
            return detail is null
                ? Results.NotFound(new
                {
                    error = "schema_drift_not_found",
                    id
                })
                : Results.Ok(detail);
        });

        app.MapPost("/api/schema/drifts/{id:long}/approve", async (
            HttpContext context,
            long id,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            IManagementSchemaDriftActionService actionService,
            CancellationToken cancellationToken) =>
            await ApplySchemaDriftActionAsync(
                context,
                id,
                "approve",
                managementOptions,
                securityAuditService,
                logger,
                actionService,
                cancellationToken).ConfigureAwait(false));

        app.MapPost("/api/schema/drifts/{id:long}/reject", async (
            HttpContext context,
            long id,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            IManagementSchemaDriftActionService actionService,
            CancellationToken cancellationToken) =>
            await ApplySchemaDriftActionAsync(
                context,
                id,
                "reject",
                managementOptions,
                securityAuditService,
                logger,
                actionService,
                cancellationToken).ConfigureAwait(false));

        app.MapPost("/api/schema/drifts/{id:long}/block", async (
            HttpContext context,
            long id,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            IManagementSchemaDriftActionService actionService,
            CancellationToken cancellationToken) =>
            await ApplySchemaDriftActionAsync(
                context,
                id,
                "block",
                managementOptions,
                securityAuditService,
                logger,
                actionService,
                cancellationToken).ConfigureAwait(false));
    }

    private static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tools", async (
            IManagementToolInventoryRepository repository,
            CancellationToken cancellationToken) =>
        {
            var response = await repository.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });

        app.MapPost("/api/tools/discover", async Task<IResult> (
            HttpContext context,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            IManagementToolDiscoveryService discoveryService,
            CancellationToken cancellationToken) =>
        {
            if (!await ManagementWriteAuthorization.IsAuthorizedAsync(
                    context,
                    managementOptions,
                    securityAuditService,
                    logger,
                    cancellationToken).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            ManagementToolDiscoveryRequest? request;
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<ManagementToolDiscoveryRequest>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new
                {
                    error = "tool_discovery_request_invalid",
                    message = ex.Message
                });
            }

            try
            {
                var response = await discoveryService
                    .DiscoverAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ManagementToolDiscoveryRequestException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Error,
                    message = ex.Message
                });
            }
            catch (ManagementToolDiscoveryException ex)
            {
                return Results.Json(
                    new
                    {
                        error = ex.Error,
                        message = ex.Message,
                        upstreamStatusCode = ex.UpstreamStatusCode
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    private static void MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/overview", async (
            IProxyTelemetryReader reader,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? bucketSeconds,
            int? topReasons,
            int? topTools,
            CancellationToken cancellationToken) =>
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
                return Results.BadRequest(new { error = validation.Error, message = validation.Message });
            }

            var response = await reader.GetOverviewAsync(validation.Query!, cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });
    }

    private static async Task<IResult> ApplySchemaDriftActionAsync(
        HttpContext context,
        long id,
        string action,
        ManagementApiOptions managementOptions,
        ManagementSecurityAuditService securityAuditService,
        ILogger<Program> logger,
        IManagementSchemaDriftActionService actionService,
        CancellationToken cancellationToken)
    {
        if (!await ManagementWriteAuthorization.IsAuthorizedAsync(
                context,
                managementOptions,
                securityAuditService,
                logger,
                cancellationToken).ConfigureAwait(false))
        {
            return Results.Unauthorized();
        }

        ManagementSchemaDriftActionRequest? request;
        try
        {
            request = await ReadOptionalActionRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new
            {
                error = "request_json_invalid",
                message = ex.Message
            });
        }

        var detail = await actionService
            .ApplyAsync(id, action, request, cancellationToken)
            .ConfigureAwait(false);
        return detail is null
            ? Results.NotFound(new
            {
                error = "schema_drift_not_found",
                id
            })
            : Results.Ok(detail);
    }

    private static async Task<ManagementSchemaDriftActionRequest?> ReadOptionalActionRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is null or 0)
        {
            return null;
        }

        return await context.Request
            .ReadFromJsonAsync<ManagementSchemaDriftActionRequest>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsValidDriftStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        return status.Trim() switch
        {
            "pending" or "approved" or "rejected" or "blocked" => true,
            _ => false
        };
    }

    private static ManagementPolicyValidationRequest CreatePolicyValidationRequest(HttpContext context) =>
        new(context.Request.Body, context.Request.ContentType);
}

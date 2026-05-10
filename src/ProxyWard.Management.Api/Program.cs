using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ProxyWard.Management.Api.Audit;
using ProxyWard.Management.Api.Dashboard;
using ProxyWard.Management.Api.Drift;
using ProxyWard.Management.Api.Policy;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Api.Settings;
using ProxyWard.Management.Api.Status;
using ProxyWard.Management.Api.Tools;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.Management.Api;

public partial class Program
{
    private const string CorsPolicyName = "ProxyWardManagementCors";
    private const string AuditExportFileName = "proxyward-audit-events.ndjson";
    private const string AuditExportContentType = "application/x-ndjson";
    private const int AuditExportFlushEveryRows = 100;
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    private const int OverviewMinBucketSeconds = 10;
    private static readonly TimeSpan OverviewMinWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OverviewMaxWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan OverviewClockSkewTolerance = TimeSpan.FromMinutes(5);
    private const int OverviewDefaultBucketSeconds = 60;
    private const int OverviewDefaultTopN = 5;
    private const int OverviewMaxTopN = 50;
    private static readonly TimeSpan OverviewDefaultWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan ProxyControlProbeTimeout = TimeSpan.FromSeconds(2);

    public static Task Main(string[] args) =>
        CreateApp(args).RunAsync();

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = LoadOptions(builder.Configuration);
        var auditReadOptions = LoadAuditReadOptions(builder.Configuration);

        builder.Services.AddCors(cors =>
        {
            cors.AddPolicy(CorsPolicyName, policy =>
            {
                if (options.CorsAllowedOrigins.Count == 0)
                {
                    policy.SetIsOriginAllowed(_ => false);
                }
                else
                {
                    policy.WithOrigins(options.CorsAllowedOrigins.ToArray());
                }

                policy
                    .WithMethods("GET", "POST", "PUT", "PATCH", "OPTIONS")
                    .WithHeaders(HeaderNames.Accept, HeaderNames.Authorization, HeaderNames.ContentType)
                    .SetPreflightMaxAge(TimeSpan.FromHours(1));
            });
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(auditReadOptions);
        builder.Services.AddScoped(services => new ManagementAuditEventRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        builder.Services.AddScoped(services => new ManagementSchemaDriftRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        builder.Services.AddScoped(services => new ManagementSchemaDriftActionService(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementSchemaDriftRepository>()));
        builder.Services.AddScoped<IProxyTelemetryReader>(services => new AuditDbProxyTelemetryReader(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        builder.Services.AddScoped<ManagementToolInventoryRepository>();
        builder.Services.AddSingleton(_ => new SqlitePolicyStore(options.AuditDatabasePath));
        builder.Services.AddScoped<ManagementPolicyReader>();
        builder.Services.AddScoped<ManagementPolicyValidationService>();
        builder.Services.AddScoped<ManagementPolicyApplyService>();
        builder.Services.AddScoped<ManagementPolicyModeService>();
        builder.Services.AddScoped<ManagementSecurityAuditService>();
        builder.Services.AddScoped<ManagementSettingsService>();
        builder.Services.AddHttpClient<IProxyControlClient, HttpProxyControlClient>(client =>
        {
            client.BaseAddress = options.ProxyControlBaseUrl;
            client.Timeout = ProxyControlProbeTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        builder.Services.AddScoped<ManagementStatusService>();

        var app = builder.Build();

        app.UseCors(CorsPolicyName);

        app.MapGet("/", () => Results.Redirect("/api/status"));

        app.MapGet("/api/status", async (
            ManagementStatusService statusService,
            CancellationToken cancellationToken) =>
        {
            var response = await statusService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });

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
                    .ValidateAsync(context.Request, cancellationToken)
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
            if (!await IsAuthorizedManagementWriteAsync(
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
                    .ApplyAsync(context.Request, cancellationToken)
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
            if (!await IsAuthorizedManagementWriteAsync(
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

        app.MapGet("/api/audit/events", async (
            ManagementAuditEventRepository repository,
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
            ManagementAuditEventRepository repository,
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
            ManagementAuditEventRepository repository,
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

        app.MapGet("/api/schema/drifts", async (
            ManagementSchemaDriftRepository repository,
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
            ManagementSchemaDriftRepository repository,
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

        app.MapGet("/api/tools", async (
            ManagementToolInventoryRepository repository,
            CancellationToken cancellationToken) =>
        {
            var response = await repository.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });

        app.MapPost("/api/schema/drifts/{id:long}/approve", async (
            HttpContext context,
            long id,
            ManagementApiOptions managementOptions,
            ManagementSecurityAuditService securityAuditService,
            ILogger<Program> logger,
            ManagementSchemaDriftActionService actionService,
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
            ManagementSchemaDriftActionService actionService,
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
            ManagementSchemaDriftActionService actionService,
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

        app.MapGet("/api/overview", async (
            IProxyTelemetryReader reader,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? bucketSeconds,
            int? topReasons,
            int? topTools,
            CancellationToken cancellationToken) =>
        {
            var validation = ValidateOverviewQuery(fromUtc, toUtc, bucketSeconds, topReasons, topTools, DateTimeOffset.UtcNow);
            if (validation.Error is not null)
            {
                return Results.BadRequest(new { error = validation.Error, message = validation.Message });
            }

            var response = await reader.GetOverviewAsync(validation.Query!, cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });

        return app;
    }

    private static async Task<IResult> ApplySchemaDriftActionAsync(
        HttpContext context,
        long id,
        string action,
        ManagementApiOptions managementOptions,
        ManagementSecurityAuditService securityAuditService,
        ILogger<Program> logger,
        ManagementSchemaDriftActionService actionService,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedManagementWriteAsync(
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

    private static async Task<bool> IsAuthorizedManagementWriteAsync(
        HttpContext context,
        ManagementApiOptions options,
        ManagementSecurityAuditService securityAuditService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (TryAuthorizeManagementWrite(context, options, out var failureReason))
        {
            return true;
        }

        logger.LogWarning(
            "Management write authorization failed for {Method} {Path} from {RemoteIp}. Reason: {Reason}.",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            failureReason);

        await securityAuditService
            .RecordAuthorizationFailureAsync(context, failureReason, cancellationToken)
            .ConfigureAwait(false);

        return false;
    }

    private static bool TryAuthorizeManagementWrite(
        HttpContext context,
        ManagementApiOptions options,
        out string failureReason)
    {
        failureReason = string.Empty;

        if (options.LocalDevelopmentMode)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(options.AdminToken))
        {
            failureReason = "admin_token_not_configured";
            return false;
        }

        var headerValue = context.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "authorization_header_missing";
            return false;
        }

        var suppliedToken = headerValue[bearerPrefix.Length..].Trim();
        if (suppliedToken.Length == 0)
        {
            failureReason = "bearer_token_missing";
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(options.AdminToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
        if (expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        {
            return true;
        }

        failureReason = "bearer_token_invalid";
        return false;
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

    private static OverviewValidation ValidateOverviewQuery(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? bucketSeconds,
        int? topReasons,
        int? topTools,
        DateTimeOffset now)
    {
        DateTimeOffset effectiveFrom;
        DateTimeOffset effectiveTo;

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            effectiveFrom = fromUtc.Value;
            effectiveTo = toUtc.Value;
        }
        else if (!fromUtc.HasValue && !toUtc.HasValue)
        {
            effectiveTo = now;
            effectiveFrom = now - OverviewDefaultWindow;
        }
        else
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "fromUtc and toUtc must be provided together.");
        }

        if (effectiveTo < effectiveFrom)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "toUtc must be greater than or equal to fromUtc.");
        }

        if (effectiveTo > now + OverviewClockSkewTolerance)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "toUtc is too far in the future.");
        }

        var windowDuration = effectiveTo - effectiveFrom;
        if (windowDuration < OverviewMinWindow)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "window duration is below the supported minimum.");
        }

        if (windowDuration > OverviewMaxWindow)
        {
            return OverviewValidation.Failure(
                "window_invalid",
                "window duration is above the supported maximum.");
        }

        var effectiveBucket = bucketSeconds ?? OverviewDefaultBucketSeconds;
        if (effectiveBucket < OverviewMinBucketSeconds)
        {
            return OverviewValidation.Failure(
                "bucket_invalid",
                "bucketSeconds is below the supported minimum.");
        }

        if (effectiveBucket * 2.0 > windowDuration.TotalSeconds)
        {
            return OverviewValidation.Failure(
                "bucket_invalid",
                "bucketSeconds must not exceed half the window duration.");
        }

        var effectiveTopReasons = topReasons ?? OverviewDefaultTopN;
        var effectiveTopTools = topTools ?? OverviewDefaultTopN;
        if (effectiveTopReasons < 1 || effectiveTopReasons > OverviewMaxTopN
            || effectiveTopTools < 1 || effectiveTopTools > OverviewMaxTopN)
        {
            return OverviewValidation.Failure(
                "topn_invalid",
                "topReasons and topTools must be in the range [1, 50].");
        }

        return OverviewValidation.Success(new OverviewQuery(
            FromUtc: effectiveFrom,
            ToUtc: effectiveTo,
            BucketSeconds: effectiveBucket,
            TopReasonsLimit: effectiveTopReasons,
            TopToolsLimit: effectiveTopTools));
    }

    private readonly record struct OverviewValidation(string? Error, string? Message, OverviewQuery? Query)
    {
        public static OverviewValidation Success(OverviewQuery query) => new(null, null, query);
        public static OverviewValidation Failure(string error, string message) => new(error, message, null);
    }

    private static ManagementApiOptions LoadOptions(IConfiguration configuration)
    {
        var auditDatabasePath = Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_AUDIT_DB_PATH")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_DB_PATH")
            ?? configuration["Management:Audit:SqlitePath"]
            ?? "./data/proxyward.db";

        var proxyControlBaseUrlValue = Environment.GetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL")
            ?? configuration["Management:ProxyControl:BaseUrl"]
            ?? "http://localhost:8080";

        if (!Uri.TryCreate(proxyControlBaseUrlValue, UriKind.Absolute, out var proxyControlBaseUrl)
            || (proxyControlBaseUrl.Scheme != Uri.UriSchemeHttp && proxyControlBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Management:ProxyControl:BaseUrl or PROXYWARD_PROXY_CONTROL_URL must be an absolute http or https URL.");
        }

        var proxyControlTokenValue = Environment.GetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_TOKEN")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN")
            ?? configuration["Management:ProxyControl:Token"];
        var proxyControlToken = string.IsNullOrWhiteSpace(proxyControlTokenValue) ? null : proxyControlTokenValue;

        var adminTokenValue = Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_ADMIN_TOKEN")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN")
            ?? configuration["Management:AdminToken"];
        var adminToken = string.IsNullOrWhiteSpace(adminTokenValue) ? null : adminTokenValue;

        var localDevelopmentMode = ReadBool(
            configuration,
            "PROXYWARD_MANAGEMENT_LOCAL_DEV",
            "Management:LocalDevMode",
            defaultValue: false);

        var corsAllowedOrigins = ReadStringList(
            Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_CORS_ALLOWED_ORIGINS")
                ?? configuration["Management:Cors:AllowedOrigins"]);

        return new ManagementApiOptions(
            auditDatabasePath,
            proxyControlBaseUrl,
            proxyControlToken,
            adminToken,
            localDevelopmentMode,
            corsAllowedOrigins);
    }

    private static ManagementAuditReadOptions LoadAuditReadOptions(IConfiguration configuration)
    {
        var defaults = new ManagementAuditReadOptions();

        var maxExportRowCount = ReadPositiveInt(
            configuration,
            "PROXYWARD_MANAGEMENT_AUDIT_MAX_EXPORT_ROWS",
            "Management:Audit:MaxExportRows",
            defaults.MaxExportRowCount);

        var maxOverviewSampleSize = ReadPositiveInt(
            configuration,
            "PROXYWARD_MANAGEMENT_OVERVIEW_MAX_SAMPLE_SIZE",
            "Management:Overview:MaxSampleSize",
            defaults.MaxOverviewSampleSize);

        return defaults with
        {
            MaxExportRowCount = maxExportRowCount,
            MaxOverviewSampleSize = maxOverviewSampleSize
        };
    }

    private static int ReadPositiveInt(
        IConfiguration configuration,
        string envVarName,
        string configKey,
        int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName) ?? configuration[configKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException(
                $"{configKey} or {envVarName} must be a positive integer.");
        }

        return parsed;
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string envVarName,
        string configKey,
        bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName) ?? configuration[configKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            throw new InvalidOperationException($"{configKey} or {envVarName} must be true or false.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> ReadStringList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(origin => origin.TrimEnd('/'))
                .Where(origin => origin.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

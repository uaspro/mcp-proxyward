using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProxyWard.Management.Api.Audit;
using ProxyWard.Management.Api.Dashboard;

namespace ProxyWard.Management.Api;

public partial class Program
{
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

    public static Task Main(string[] args) =>
        CreateApp(args).RunAsync();

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = LoadOptions(builder.Configuration);
        var auditReadOptions = LoadAuditReadOptions(builder.Configuration);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(auditReadOptions);
        builder.Services.AddScoped(services => new ManagementAuditEventRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        builder.Services.AddScoped<IProxyTelemetryReader>(services => new AuditDbProxyTelemetryReader(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));

        var app = builder.Build();

        app.MapGet("/", () => Results.Redirect("/api/status"));

        app.MapGet("/api/status", (ManagementApiOptions managementOptions) => Results.Ok(new
        {
            status = "healthy",
            service = "MCP ProxyWard Management API",
            audit = new
            {
                sqlitePath = managementOptions.AuditDatabasePath
            },
            proxyControl = new
            {
                baseUrl = managementOptions.ProxyControlBaseUrl.ToString()
            }
        }));

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

        return new ManagementApiOptions(auditDatabasePath, proxyControlBaseUrl);
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
}

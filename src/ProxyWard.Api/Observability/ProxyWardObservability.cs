using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProxyWard.Policy.Configuration;
using PolicyOtlpExporterOptions = ProxyWard.Policy.Configuration.OtlpExporterOptions;

namespace ProxyWard.Api.Observability;

public static class ProxyWardObservability
{
    private const string DefaultEnvironmentName = "Production";

    public static ObservabilityExportPlan AddProxyWardObservability(
        this WebApplicationBuilder builder,
        ProxyWardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Services.AddProxyWardObservability(
            builder.Logging,
            policy,
            Environment.GetEnvironmentVariable);
    }

    public static ObservabilityExportPlan AddProxyWardObservability(
        this IServiceCollection services,
        ILoggingBuilder logging,
        ProxyWardPolicy policy,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logging);

        var plan = CreateExportPlan(policy, getEnvironmentVariable);
        var openTelemetry = services.AddOpenTelemetry();

        if (plan.ApplicationInsightsEnabled)
        {
            openTelemetry.UseAzureMonitor(options =>
            {
                options.ConnectionString = plan.ApplicationInsightsConnectionString;
                options.SamplingRatio = (float)plan.TracesRatio;
            });
        }

        openTelemetry
            .ConfigureResource(resource => ConfigureResource(resource, plan))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.EnrichWithHttpRequest = (activity, request) =>
                            ProxyWardTelemetry.RedactHttpRequestQueryTags(
                                activity,
                                request.Path.Value,
                                request.QueryString.Value,
                                request.Scheme,
                                request.Host.Value);
                        options.EnrichWithHttpResponse = (activity, response) =>
                        {
                            var request = response.HttpContext.Request;
                            ProxyWardTelemetry.RedactHttpRequestQueryTags(
                                activity,
                                request.Path.Value,
                                request.QueryString.Value,
                                request.Scheme,
                                request.Host.Value);
                        };
                    })
                    .AddSource(ProxyWardTelemetry.ActivitySourceName)
                    .SetSampler(new TraceIdRatioBasedSampler(plan.TracesRatio));

                if (plan.OtlpEnabled)
                {
                    tracing.AddOtlpExporter(options => ConfigureOtlpExporter(options, plan.OtlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddMeter(ProxyWardTelemetry.MeterName);

                if (plan.OtlpEnabled)
                {
                    metrics.AddOtlpExporter(options => ConfigureOtlpExporter(options, plan.OtlpEndpoint!));
                }
            });

        if (plan.ApplicationInsightsEnabled)
        {
            services.Configure<OpenTelemetryLoggerOptions>(options => ConfigureLoggerOptions(options, plan));
        }
        else if (plan.OtlpEnabled)
        {
            logging.AddOpenTelemetry(options => ConfigureLoggerOptions(options, plan));
        }

        return plan;
    }

    public static ObservabilityExportPlan CreateExportPlan(
        ProxyWardPolicy policy,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var serviceName = string.IsNullOrWhiteSpace(policy.Observability.ServiceName)
            ? ProxyWardTelemetry.ServiceName
            : policy.Observability.ServiceName.Trim();

        var otlpEndpoint = ResolveOtlpEndpoint(policy.Observability.Otlp);
        var connectionStringEnvironmentVariable = string.IsNullOrWhiteSpace(
                policy.Observability.ApplicationInsights.ConnectionStringEnv)
            ? "APPLICATIONINSIGHTS_CONNECTION_STRING"
            : policy.Observability.ApplicationInsights.ConnectionStringEnv.Trim();
        var applicationInsightsConnectionString = policy.Observability.ApplicationInsights.Enabled
            ? Normalize(getEnvironmentVariable(connectionStringEnvironmentVariable))
            : null;
        var applicationInsightsEnabled = policy.Observability.ApplicationInsights.Enabled
            && !string.IsNullOrWhiteSpace(applicationInsightsConnectionString);

        return new ObservabilityExportPlan(
            serviceName,
            otlpEndpoint is not null,
            otlpEndpoint,
            applicationInsightsEnabled,
            connectionStringEnvironmentVariable,
            applicationInsightsConnectionString,
            policy.Observability.ApplicationInsights.Enabled && applicationInsightsConnectionString is null,
            policy.Observability.Sampling.TracesRatio);
    }

    private static Uri? ResolveOtlpEndpoint(PolicyOtlpExporterOptions options)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("observability.otlp.endpoint is required when OTLP export is enabled");
        }

        return new Uri(options.Endpoint.Trim(), UriKind.Absolute);
    }

    private static void ConfigureResource(ResourceBuilder resource, ObservabilityExportPlan plan)
    {
        resource.AddService(
            serviceName: plan.ServiceName,
            serviceVersion: GetServiceVersion());

        var attributes = new List<KeyValuePair<string, object>>
        {
            new("service.instance.id", Environment.MachineName),
            new("deployment.environment.name", GetEnvironmentName())
        };

        resource.AddAttributes(attributes);
    }

    private static ResourceBuilder CreateResourceBuilder(ObservabilityExportPlan plan)
    {
        var resource = ResourceBuilder.CreateDefault();
        ConfigureResource(resource, plan);
        return resource;
    }

    private static void ConfigureOtlpExporter(OpenTelemetry.Exporter.OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
    }

    private static void ConfigureLoggerOptions(
        OpenTelemetryLoggerOptions options,
        ObservabilityExportPlan plan)
    {
        options.IncludeScopes = true;
        options.IncludeFormattedMessage = false;
        options.ParseStateValues = true;
        options.SetResourceBuilder(CreateResourceBuilder(plan));

        if (plan.OtlpEnabled)
        {
            options.AddOtlpExporter(exporter => ConfigureOtlpExporter(exporter, plan.OtlpEndpoint!));
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetServiceVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static string GetEnvironmentName() =>
        Normalize(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
        ?? Normalize(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
        ?? DefaultEnvironmentName;
}

public sealed record ObservabilityExportPlan(
    string ServiceName,
    bool OtlpEnabled,
    Uri? OtlpEndpoint,
    bool ApplicationInsightsEnabled,
    string ApplicationInsightsConnectionStringEnvironmentVariable,
    string? ApplicationInsightsConnectionString,
    bool ApplicationInsightsMissingConnectionString,
    double TracesRatio);

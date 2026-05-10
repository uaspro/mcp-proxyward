using Microsoft.Net.Http.Headers;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Audit;
using ProxyWard.Management.Application.Dashboard;
using ProxyWard.Management.Application.Drift;
using ProxyWard.Management.Application.Policy;
using ProxyWard.Management.Application.Security;
using ProxyWard.Management.Application.Settings;
using ProxyWard.Management.Application.Status;
using ProxyWard.Management.Application.Tools;
using ProxyWard.Management.Infrastructure.Audit;
using ProxyWard.Management.Infrastructure.Dashboard;
using ProxyWard.Management.Infrastructure.Drift;
using ProxyWard.Management.Infrastructure.Policy;
using ProxyWard.Management.Infrastructure.Security;
using ProxyWard.Management.Infrastructure.Status;
using ProxyWard.Management.Infrastructure.Tools;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.Management.Api.Configuration;

internal static class ManagementApiServiceCollectionExtensions
{
    public const string CorsPolicyName = "ProxyWardManagementCors";

    private static readonly TimeSpan ProxyControlProbeTimeout = TimeSpan.FromSeconds(2);

    public static IServiceCollection AddProxyWardManagementApi(
        this IServiceCollection services,
        ManagementApiOptions options,
        ManagementAuditReadOptions auditReadOptions)
    {
        services.AddControllers();
        services.AddManagementCors(options);
        services.AddManagementOptions(options, auditReadOptions);
        services.AddManagementAudit();
        services.AddManagementDashboard();
        services.AddManagementSchemaDrift();
        services.AddManagementTools();
        services.AddManagementPolicy(options);
        services.AddManagementSecurity();
        services.AddManagementSettings();
        services.AddManagementStatus(options);

        return services;
    }

    private static void AddManagementCors(this IServiceCollection services, ManagementApiOptions options)
    {
        services.AddCors(cors =>
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
    }

    private static void AddManagementOptions(
        this IServiceCollection services,
        ManagementApiOptions options,
        ManagementAuditReadOptions auditReadOptions)
    {
        services.AddSingleton(options);
        services.AddSingleton(auditReadOptions);
    }

    private static void AddManagementAudit(this IServiceCollection services)
    {
        services.AddScoped<IManagementAuditEventRepository>(services => new ManagementAuditEventRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
    }

    private static void AddManagementDashboard(this IServiceCollection services)
    {
        services.AddScoped<IProxyTelemetryReader>(services => new AuditDbProxyTelemetryReader(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
    }

    private static void AddManagementSchemaDrift(this IServiceCollection services)
    {
        services.AddScoped<ManagementSchemaDriftRepository>(services => new ManagementSchemaDriftRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        services.AddScoped<IManagementSchemaDriftRepository>(services =>
            services.GetRequiredService<ManagementSchemaDriftRepository>());
        services.AddScoped<IManagementSchemaDriftActionService>(services => new ManagementSchemaDriftActionService(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementSchemaDriftRepository>()));
    }

    private static void AddManagementTools(this IServiceCollection services)
    {
        services.AddScoped<IManagementToolInventoryRepository, ManagementToolInventoryRepository>();
        services.AddHttpClient<IManagementToolDiscoveryService, ManagementToolDiscoveryService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
    }

    private static void AddManagementPolicy(this IServiceCollection services, ManagementApiOptions options)
    {
        services.AddSingleton(_ => new SqlitePolicyStore(options.AuditDatabasePath));
        services.AddSingleton<IManagementPolicySnapshotStore, SqliteManagementPolicySnapshotStore>();
        services.AddSingleton<IManagementPolicyYamlSanitizer, YamlManagementPolicySanitizer>();
        services.AddSingleton<IManagementPolicyModelYamlSerializer, YamlManagementPolicyModelSerializer>();
        services.AddSingleton<IManagementPolicyAuditStore, SqliteManagementPolicyAuditStore>();
        services.AddScoped<ManagementPolicyReader>();
        services.AddScoped<ManagementPolicyValidationService>();
        services.AddScoped<ManagementPolicyApplyService>();
        services.AddScoped<ManagementPolicyModeService>();
    }

    private static void AddManagementSecurity(this IServiceCollection services)
    {
        services.AddScoped<ManagementWriteAuthorization>();
        services.AddScoped<IManagementSecurityAuditWriter, SqliteManagementSecurityAuditWriter>();
    }

    private static void AddManagementSettings(this IServiceCollection services)
    {
        services.AddScoped<ManagementSettingsService>();
    }

    private static void AddManagementStatus(this IServiceCollection services, ManagementApiOptions options)
    {
        services.AddScoped<IManagementStatusStoreProbe, SqliteManagementStatusStoreProbe>();
        services.AddHttpClient<IProxyControlClient, HttpProxyControlClient>(client =>
        {
            client.BaseAddress = options.ProxyControlBaseUrl;
            client.Timeout = ProxyControlProbeTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddScoped<ManagementStatusService>();
    }
}

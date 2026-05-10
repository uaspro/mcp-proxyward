using Microsoft.Net.Http.Headers;
using ProxyWard.Management.Api.Security;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Audit;
using ProxyWard.Management.Application.Dashboard;
using ProxyWard.Management.Application.Drift;
using ProxyWard.Management.Application.Policy;
using ProxyWard.Management.Application.Settings;
using ProxyWard.Management.Application.Status;
using ProxyWard.Management.Application.Tools;
using ProxyWard.Management.Infrastructure.Dashboard;
using ProxyWard.Management.Infrastructure.Drift;
using ProxyWard.Management.Infrastructure.Audit;
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

        services.AddSingleton(options);
        services.AddSingleton(auditReadOptions);
        services.AddScoped<IManagementAuditEventRepository>(services => new ManagementAuditEventRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        services.AddScoped<ManagementSchemaDriftRepository>(services => new ManagementSchemaDriftRepository(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        services.AddScoped<IManagementSchemaDriftRepository>(services =>
            services.GetRequiredService<ManagementSchemaDriftRepository>());
        services.AddScoped<IManagementSchemaDriftActionService>(services => new ManagementSchemaDriftActionService(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementSchemaDriftRepository>()));
        services.AddScoped<IProxyTelemetryReader>(services => new AuditDbProxyTelemetryReader(
            services.GetRequiredService<ManagementApiOptions>().AuditDatabasePath,
            services.GetRequiredService<ManagementAuditReadOptions>()));
        services.AddScoped<IManagementToolInventoryRepository, ManagementToolInventoryRepository>();
        services.AddHttpClient<IManagementToolDiscoveryService, ManagementToolDiscoveryService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddSingleton(_ => new SqlitePolicyStore(options.AuditDatabasePath));
        services.AddScoped<ManagementPolicyReader>();
        services.AddScoped<ManagementPolicyValidationService>();
        services.AddScoped<ManagementPolicyApplyService>();
        services.AddScoped<ManagementPolicyModeService>();
        services.AddScoped<ManagementSecurityAuditService>();
        services.AddScoped<ManagementSettingsService>();
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

        return services;
    }
}

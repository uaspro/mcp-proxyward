using System.Net;
using Microsoft.Net.Http.Headers;
using Npgsql;
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
using ProxyWard.Locking.Persistence;
using ProxyWard.Core.Persistence;
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
        services.AddManagementPersistence(options);
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

    private static void AddManagementPersistence(
        this IServiceCollection services,
        ManagementApiOptions options)
    {
        var database = options.EffectivePersistenceDatabase;
        services.AddSingleton(database);
        if (database.Provider == PersistenceDatabaseProvider.PostgreSql)
        {
            services.AddSingleton(_ => NpgsqlDataSource.Create(database.PostgresConnectionString!));
        }

        services.AddSingleton<IPolicyStore>(CreatePolicyStore);
        services.AddSingleton<ITrackedToolSchemaStore>(CreateTrackedToolSchemaStore);
        services.AddSingleton<ISchemaDriftReviewStore>(CreateSchemaDriftReviewStore);
        services.AddSingleton<IToolSchemaDiffMetadataStore>(CreateToolSchemaDiffMetadataStore);
    }

    private static void AddManagementAudit(this IServiceCollection services)
    {
        services.AddScoped<IManagementAuditEventRepository>(services =>
        {
            var options = services.GetRequiredService<ManagementAuditReadOptions>();
            return CreateForPersistence<IManagementAuditEventRepository>(
                services,
                postgres => new PostgresManagementAuditEventRepository(postgres, options),
                sqlitePath => new ManagementAuditEventRepository(sqlitePath, options));
        });
    }

    private static void AddManagementDashboard(this IServiceCollection services)
    {
        services.AddScoped<IProxyTelemetryReader>(services =>
        {
            var options = services.GetRequiredService<ManagementAuditReadOptions>();
            return CreateForPersistence<IProxyTelemetryReader>(
                services,
                postgres => new PostgresPersistenceProxyTelemetryReader(postgres, options),
                sqlitePath => new SqlitePersistenceProxyTelemetryReader(sqlitePath, options));
        });
    }

    private static void AddManagementSchemaDrift(this IServiceCollection services)
    {
        services.AddScoped<IManagementSchemaDriftRepository>(services =>
        {
            var options = services.GetRequiredService<ManagementAuditReadOptions>();
            return CreateForPersistence<IManagementSchemaDriftRepository>(
                services,
                postgres => new PostgresManagementSchemaDriftRepository(postgres, options),
                sqlitePath => new ManagementSchemaDriftRepository(sqlitePath, options));
        });
        services.AddScoped<IManagementSchemaDriftActionService>(services =>
        {
            var options = services.GetRequiredService<ManagementAuditReadOptions>();
            return CreateForPersistence<IManagementSchemaDriftActionService>(
                services,
                postgres =>
                {
                    var repository = new PostgresManagementSchemaDriftRepository(postgres, options);
                    return new PostgresManagementSchemaDriftActionService(postgres, repository);
                },
                sqlitePath =>
                {
                    var repository = new ManagementSchemaDriftRepository(sqlitePath, options);
                    return new ManagementSchemaDriftActionService(sqlitePath, repository);
                });
        });
    }

    private static void AddManagementTools(this IServiceCollection services)
    {
        services.AddScoped<IManagementToolInventoryRepository>(services =>
        {
            var policyStore = services.GetRequiredService<IPolicyStore>();
            return CreateForPersistence<IManagementToolInventoryRepository>(
                services,
                postgres => new PostgresManagementToolInventoryRepository(postgres, policyStore),
                _ => new ManagementToolInventoryRepository(
                    services.GetRequiredService<ManagementApiOptions>(),
                    policyStore));
        });
        services.AddHttpClient<IManagementToolDiscoveryService, ManagementToolDiscoveryService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        });
    }

    private static void AddManagementPolicy(this IServiceCollection services, ManagementApiOptions options)
    {
        services.AddSingleton<IManagementPolicySnapshotStore, ManagementPolicySnapshotStore>();
        services.AddSingleton<IManagementPolicyYamlSanitizer, YamlManagementPolicySanitizer>();
        services.AddSingleton<IManagementPolicyModelYamlSerializer, YamlManagementPolicyModelSerializer>();
        services.AddSingleton<IManagementPolicyYamlCodec, ManagementPolicyYamlCodec>();
        services.AddSingleton<IManagementPolicyAuditStore>(services =>
        {
            return CreateForPersistence<IManagementPolicyAuditStore>(
                services,
                postgres => new PostgresManagementPolicyAuditStore(postgres),
                _ => new SqliteManagementPolicyAuditStore(options));
        });
        services.AddScoped<ManagementPolicyReader>();
        services.AddScoped<ManagementPolicyValidationService>();
        services.AddScoped<ManagementPolicyApplyService>();
        services.AddScoped<ManagementPolicyModeService>();
    }

    private static void AddManagementSecurity(this IServiceCollection services)
    {
        services.AddScoped<ManagementWriteAuthorization>();
        services.AddScoped<IManagementSecurityAuditWriter>(services =>
        {
            return CreateForPersistence<IManagementSecurityAuditWriter>(
                services,
                postgres => new PostgresManagementSecurityAuditWriter(
                    postgres,
                    services.GetRequiredService<ILogger<PostgresManagementSecurityAuditWriter>>()),
                _ => new SqliteManagementSecurityAuditWriter(
                    services.GetRequiredService<ManagementApiOptions>(),
                    services.GetRequiredService<ILogger<SqliteManagementSecurityAuditWriter>>()));
        });
    }

    private static void AddManagementSettings(this IServiceCollection services)
    {
        services.AddScoped<ManagementSettingsService>();
    }

    private static void AddManagementStatus(this IServiceCollection services, ManagementApiOptions options)
    {
        services.AddScoped<IManagementStatusStoreProbe>(services =>
        {
            return CreateForPersistence<IManagementStatusStoreProbe>(
                services,
                postgres => new PostgresManagementStatusStoreProbe(postgres),
                _ => new SqliteManagementStatusStoreProbe(options));
        });
        services.AddHttpClient<IProxyControlClient, HttpProxyControlClient>(client =>
        {
            client.BaseAddress = EnsureTrailingSlash(options.ProxyControlBaseUrl);
            client.Timeout = ProxyControlProbeTimeout;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddScoped<ManagementStatusService>();
    }

    private static IPolicyStore CreatePolicyStore(IServiceProvider services) =>
        CreateForPersistence<IPolicyStore>(
            services,
            postgres => new PostgresPolicyStore(postgres),
            sqlitePath => new SqlitePolicyStore(sqlitePath));

    private static ITrackedToolSchemaStore CreateTrackedToolSchemaStore(IServiceProvider services) =>
        CreateForPersistence<ITrackedToolSchemaStore>(
            services,
            postgres => new PostgresTrackedToolSchemaStore(postgres),
            sqlitePath => new SqliteTrackedToolSchemaStore(sqlitePath));

    private static ISchemaDriftReviewStore CreateSchemaDriftReviewStore(IServiceProvider services) =>
        CreateForPersistence<ISchemaDriftReviewStore>(
            services,
            postgres => new PostgresSchemaDriftReviewStore(postgres),
            sqlitePath => new SqliteSchemaDriftReviewStore(sqlitePath));

    private static IToolSchemaDiffMetadataStore CreateToolSchemaDiffMetadataStore(IServiceProvider services) =>
        CreateForPersistence<IToolSchemaDiffMetadataStore>(
            services,
            postgres => new PostgresToolSchemaDiffMetadataStore(postgres),
            sqlitePath => new SqliteToolSchemaDiffMetadataStore(sqlitePath));

    private static T CreateForPersistence<T>(
        IServiceProvider services,
        Func<NpgsqlDataSource, T> postgres,
        Func<string, T> sqlite)
    {
        var database = services.GetRequiredService<PersistenceDatabaseOptions>();
        return database.Provider switch
        {
            PersistenceDatabaseProvider.PostgreSql => postgres(services.GetRequiredService<NpgsqlDataSource>()),
            _ => sqlite(database.SqlitePath!)
        };
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath + "/"
        };

        return builder.Uri;
    }
}

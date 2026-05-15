using ProxyWard.Api.Control;
using ProxyWard.Api.Middleware;
using ProxyWard.Api.OAuth;
using ProxyWard.Api.Observability;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Redaction;
using ProxyWard.Audit.Sinks;
using ProxyWard.Core.Persistence;
using ProxyWard.Core.Mcp;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;
using ProxyWard.Policy.Persistence;
using ProxyWard.Proxy.Application.Runtime;
using ProxyWard.Proxy.Infrastructure.Hosts;
using ProxyWard.Proxy.Infrastructure.Yarp;
using Npgsql;
using Yarp.ReverseProxy.Configuration;

namespace ProxyWard.Api.Configuration;

public static class ProxyWardApiStartupExtensions
{
    public static async Task AddProxyWardApiAsync(this WebApplicationBuilder builder)
    {
        var persistenceDatabase = ResolvePersistenceDatabase(builder.Configuration);
        var persistenceServices = CreatePersistenceServices(persistenceDatabase);
        var snapshot = await persistenceServices.PolicyStore.InitializeAndReadCurrentAsync(
            ProxyWardDefaultPolicy.CreateYaml(),
            CancellationToken.None);
        await EnsurePersistenceSchemasAsync(
            persistenceServices.SchemaInitializers,
            CancellationToken.None).ConfigureAwait(false);
        var policy = snapshot.Policy;
        var yarpConfigProvider = new DynamicProxyWardYarpConfigProvider(
            ProxyWardYarpConfig.CreateRoutes(policy),
            ProxyWardYarpConfig.CreateClusters(policy));

        builder.AddProxyWardObservability(policy);

        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(persistenceDatabase);
        if (persistenceServices.PostgresDataSource is not null)
        {
            builder.Services.AddSingleton<NpgsqlDataSource>(_ => persistenceServices.PostgresDataSource);
        }

        builder.Services.AddSingleton<IPolicyStore>(_ => persistenceServices.PolicyStore);
        builder.Services.AddSingleton<IProxyWardPolicyProvider>(new InMemoryProxyWardPolicyProvider(policy));
        builder.Services.AddSingleton(ProxyWardControlOptions.Load(builder.Configuration));
        builder.Services.AddSingleton(CreateToolSchemaDiffMetadataOptions(builder.Configuration));
        builder.Services.AddSingleton<ProxyWardYarpConfigFactory>();
        builder.Services.AddSingleton<IProxyWardYarpConfigProvider>(yarpConfigProvider);
        builder.Services.AddSingleton<IProxyConfigProvider>(yarpConfigProvider);
        builder.Services.AddSingleton<IMcpMessageParser, McpMessageParser>();
        builder.Services.AddSingleton<IMcpMethodClassifier, McpMethodClassifier>();
        builder.Services.AddSingleton<IRedactor, Redactor>();
        builder.Services.AddSingleton<IToolDefinitionExtractor, ToolDefinitionExtractor>();
        builder.Services.AddSingleton<ResponseInspectionTargetResolver>();
        builder.Services.AddSingleton<ResponseInspectionDriftReviewCoordinator>();
        builder.Services.AddSingleton<IToolFingerprinter, ToolFingerprinter>();
        builder.Services.AddSingleton<ITrackedToolSchemaStore>(_ =>
            persistenceServices.TrackedToolSchemaStore);
        builder.Services.AddSingleton<ISchemaDriftReviewStore>(_ =>
            persistenceServices.SchemaDriftReviewStore);
        builder.Services.AddSingleton<IToolSchemaDiffMetadataStore>(_ =>
            persistenceServices.ToolSchemaDiffMetadataStore);
        builder.Services.AddSingleton<ToolSurfaceDriftEvaluator>();
        builder.Services.AddSingleton<ServerAllowlistPolicyEvaluator>();
        builder.Services.AddSingleton<ToolPolicyEvaluator>();
        builder.Services.AddSingleton<PathArgumentRuleEvaluator>();
        builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
        builder.Services.AddSingleton<HostArgumentRuleEvaluator>();
        builder.Services.AddSingleton<CommandArgumentRuleEvaluator>();
        builder.Services.AddSingleton<ArgumentPolicyOverrideResolver>();
        builder.Services.AddSingleton<IAuditSink>(services =>
            CreateAuditSink(
                services.GetRequiredService<IProxyWardPolicyProvider>(),
                persistenceServices.AuditSink,
                services.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddScoped<ProxyWardControlAuthorizationService>();
        builder.Services.AddHttpClient();
        builder.Services.AddControllers();
        builder.Services.AddReverseProxy();
    }

    public static void UseProxyWardApi(this WebApplication app)
    {
        app.MapControllers();

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ServerAllowlistMiddleware>();
        app.UseProxyWardOAuthChallengeMetadataRewrite();
        app.UseMiddleware<RequestInspectionMiddleware>();
        app.UseMiddleware<ToolPolicyMiddleware>();
        app.UseMiddleware<ResponseInspectionMiddleware>();

        app.MapReverseProxy();
    }

    private static PersistenceDatabaseOptions ResolvePersistenceDatabase(IConfiguration configuration)
    {
        var provider = NormalizeProvider(
            Environment.GetEnvironmentVariable("PROXYWARD_PERSISTENCE_PROVIDER")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_DB_PROVIDER")
            ?? configuration["ProxyWard:Persistence:Provider"]
            ?? "sqlite");

        if (provider == "postgres")
        {
            var connectionString =
                Environment.GetEnvironmentVariable("PROXYWARD_PERSISTENCE_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("PROXYWARD_POSTGRES_CONNECTION_STRING")
                ?? configuration["ProxyWard:Persistence:PostgresConnectionString"]
                ?? configuration.GetConnectionString("ProxyWardPersistence");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "PostgreSQL persistence requires PROXYWARD_PERSISTENCE_CONNECTION_STRING, PROXYWARD_POSTGRES_CONNECTION_STRING, ProxyWard:Persistence:PostgresConnectionString, or ConnectionStrings:ProxyWardPersistence.");
            }

            return PersistenceDatabaseOptions.ForPostgreSql(connectionString);
        }

        if (provider != "sqlite")
        {
            throw new InvalidOperationException(
                "Persistence provider must be 'sqlite' or 'postgres'. Configure PROXYWARD_PERSISTENCE_PROVIDER or ProxyWard:Persistence:Provider.");
        }

        var databasePath = Environment.GetEnvironmentVariable("PROXYWARD_DB_PATH")
            ?? configuration["ProxyWard:Persistence:SqlitePath"]
            ?? configuration["ProxyWard:DatabasePath"]
            ?? "./data/proxyward.db";

        return PersistenceDatabaseOptions.ForSqlite(databasePath);
    }

    private static ProxyWardPersistenceServices CreatePersistenceServices(PersistenceDatabaseOptions database)
    {
        if (database.Provider == PersistenceDatabaseProvider.PostgreSql)
        {
            var dataSource = NpgsqlDataSource.Create(database.PostgresConnectionString!);
            var policyStore = new PostgresPolicyStore(dataSource);
            var trackedToolSchemaStore = new PostgresTrackedToolSchemaStore(dataSource);
            var schemaDriftReviewStore = new PostgresSchemaDriftReviewStore(dataSource);
            var toolSchemaDiffMetadataStore = new PostgresToolSchemaDiffMetadataStore(dataSource);
            var auditSink = new PostgresAuditSink(dataSource);

            return new ProxyWardPersistenceServices(
                policyStore,
                trackedToolSchemaStore,
                schemaDriftReviewStore,
                toolSchemaDiffMetadataStore,
                auditSink,
                [
                    policyStore,
                    auditSink,
                    trackedToolSchemaStore,
                    schemaDriftReviewStore,
                    toolSchemaDiffMetadataStore
                ],
                dataSource);
        }

        return new ProxyWardPersistenceServices(
            new SqlitePolicyStore(database.SqlitePath!),
            new SqliteTrackedToolSchemaStore(database.SqlitePath!),
            new SqliteSchemaDriftReviewStore(database.SqlitePath!),
            new SqliteToolSchemaDiffMetadataStore(database.SqlitePath!),
            new SqliteAuditSink(database.SqlitePath!),
            [],
            PostgresDataSource: null);
    }

    private static async Task EnsurePersistenceSchemasAsync(
        IReadOnlyList<IPersistenceSchemaInitializer> initializers,
        CancellationToken cancellationToken)
    {
        foreach (var initializer in initializers)
        {
            await initializer.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ToolSchemaDiffMetadataOptions CreateToolSchemaDiffMetadataOptions(
        IConfiguration configuration)
    {
        var captureValues = ReadBool(
            configuration,
            "PROXYWARD_SCHEMA_DIFF_CAPTURE_VALUES",
            "ProxyWard:SchemaDiff:CaptureValues",
            defaultValue: true);
        var maxValueBytes = ReadInt(
            configuration,
            "PROXYWARD_SCHEMA_DIFF_MAX_VALUE_BYTES",
            "ProxyWard:SchemaDiff:MaxValueBytes",
            ToolSchemaDiffMetadataOptions.Default.MaxValueBytes);

        return new ToolSchemaDiffMetadataOptions(captureValues, maxValueBytes).Normalize();
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey,
        bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[configurationKey];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static int ReadInt(
        IConfiguration configuration,
        string environmentVariable,
        string configurationKey,
        int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable)
            ?? configuration[configurationKey];
        return int.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static IAuditSink CreateAuditSink(
        IProxyWardPolicyProvider policyProvider,
        IAuditSink enabledSink,
        ILoggerFactory loggerFactory)
    {
        return new PolicyControlledAuditSink(
            policyProvider,
            CreateQueuedAuditSink(enabledSink, loggerFactory));
    }

    private static IAuditSink CreateQueuedAuditSink(IAuditSink sink, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ProxyWard.Audit.Sinks.QueuedAuditSink");
        return new QueuedAuditSink(
            sink,
            onFailure: (auditEvent, exception) =>
            {
                ProxyWardTelemetry.RecordAuditSinkFailure(new TelemetryMetadata(
                    CorrelationId: auditEvent.CorrelationId,
                    ServerId: auditEvent.ServerId,
                    Method: auditEvent.Method,
                    ToolName: auditEvent.ToolName,
                    Mode: auditEvent.Mode,
                    Decision: FormatAuditDecision(auditEvent.Decision),
                    Reasons: auditEvent.Reasons,
                    PolicyVersion: auditEvent.PolicyVersion,
                    AuditEventType: auditEvent.EventType));

                logger.LogWarning(
                    exception,
                    "ProxyWard queued audit sink failed to persist {AuditEventType} event for server {ServerId}.",
                    auditEvent.EventType,
                    auditEvent.ServerId);
            });
    }

    private static string NormalizeProvider(string? value)
    {
        var normalized =
        value?.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() ?? string.Empty;

        return normalized switch
        {
            "postgresql" => "postgres",
            _ => normalized
        };
    }

    private static string FormatAuditDecision(AuditDecision decision) =>
        decision switch
        {
            AuditDecision.Block => "block",
            AuditDecision.WouldBlock => "would_block",
            AuditDecision.Warn => "warn",
            _ => "allow"
        };

    private sealed record ProxyWardPersistenceServices(
        IPolicyStore PolicyStore,
        ITrackedToolSchemaStore TrackedToolSchemaStore,
        ISchemaDriftReviewStore SchemaDriftReviewStore,
        IToolSchemaDiffMetadataStore ToolSchemaDiffMetadataStore,
        IAuditSink AuditSink,
        IReadOnlyList<IPersistenceSchemaInitializer> SchemaInitializers,
        NpgsqlDataSource? PostgresDataSource);
}

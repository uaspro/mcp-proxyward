using ProxyWard.Management.Application.Policy;
using ProxyWard.Core.Persistence;

namespace ProxyWard.Management.Application.Settings;

public sealed class ManagementSettingsService
{
    private readonly ManagementPolicyReader _policyReader;
    private readonly ManagementApiOptions _options;

    public ManagementSettingsService(
        ManagementPolicyReader policyReader,
        ManagementApiOptions options)
    {
        _policyReader = policyReader ?? throw new ArgumentNullException(nameof(policyReader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ManagementSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var policy = await _policyReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var model = policy.Model;

        return new ManagementSettingsResponse(
            Observability: new ManagementSettingsObservability(
                ServiceName: model.Observability.ServiceName,
                ConsoleEnabled: model.Observability.Console.Enabled,
                OtlpEnabled: model.Observability.Otlp.Enabled,
                OtlpEndpoint: model.Observability.Otlp.Endpoint,
                ApplicationInsightsEnabled: model.Observability.ApplicationInsights.Enabled,
                ApplicationInsightsConnectionStringEnv: model.Observability.ApplicationInsights.ConnectionStringEnv,
                TracesRatio: model.Observability.Sampling.TracesRatio),
            Audit: new ManagementSettingsAudit(
                Enabled: model.Audit.Enabled),
            Persistence: new ManagementSettingsPersistence(
                Provider: _options.EffectivePersistenceDatabase.ProviderName,
                Source: _options.EffectivePersistenceDatabase.SourceDescription,
                ConnectionConfigured: _options.EffectivePersistenceDatabase.Provider == PersistenceDatabaseProvider.Sqlite
                    || !string.IsNullOrWhiteSpace(_options.EffectivePersistenceDatabase.PostgresConnectionString)),
            Inspection: new ManagementSettingsInspection(
                MaxBodyBytes: model.Inspection.MaxBodyBytes,
                UnsupportedStreaming: model.Inspection.UnsupportedStreaming,
                BatchToolCalls: model.Inspection.BatchToolCalls),
            Service: new ManagementSettingsServiceInfo(
                PolicyHash: policy.PolicyHash,
                SourcePath: policy.ReadOnly.SourcePath,
                ServerCount: policy.ReadOnly.ServerCount,
                LoadedAtUtc: policy.ReadOnly.LoadedAtUtc,
                SourceLastModifiedUtc: policy.Source.LastModifiedUtc,
                SourceSizeBytes: policy.Source.SizeBytes),
            Runtime: new ManagementSettingsRuntime(
                EditingSupported: true,
                SettingsWritable: true));
    }
}

public sealed record ManagementSettingsResponse(
    ManagementSettingsObservability Observability,
    ManagementSettingsAudit Audit,
    ManagementSettingsPersistence Persistence,
    ManagementSettingsInspection Inspection,
    ManagementSettingsServiceInfo Service,
    ManagementSettingsRuntime Runtime);

public sealed record ManagementSettingsObservability(
    string ServiceName,
    bool ConsoleEnabled,
    bool OtlpEnabled,
    string? OtlpEndpoint,
    bool ApplicationInsightsEnabled,
    string ApplicationInsightsConnectionStringEnv,
    double TracesRatio);

public sealed record ManagementSettingsAudit(bool Enabled);

public sealed record ManagementSettingsPersistence(
    string Provider,
    string Source,
    bool ConnectionConfigured);

public sealed record ManagementSettingsInspection(
    int MaxBodyBytes,
    string UnsupportedStreaming,
    string BatchToolCalls);

public sealed record ManagementSettingsServiceInfo(
    string PolicyHash,
    string SourcePath,
    int ServerCount,
    DateTimeOffset LoadedAtUtc,
    DateTimeOffset? SourceLastModifiedUtc,
    long? SourceSizeBytes);

public sealed record ManagementSettingsRuntime(
    bool EditingSupported,
    bool SettingsWritable);

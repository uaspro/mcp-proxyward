using System.Text;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

public sealed class ManagementPolicyReader
{
    private const string MaskedUserInfo = "***";

    private readonly IManagementPolicySnapshotStore _policySnapshots;
    private readonly IManagementPolicyYamlSanitizer _yamlSanitizer;

    public ManagementPolicyReader(
        IManagementPolicySnapshotStore policySnapshots,
        IManagementPolicyYamlSanitizer yamlSanitizer)
    {
        _policySnapshots = policySnapshots ?? throw new ArgumentNullException(nameof(policySnapshots));
        _yamlSanitizer = yamlSanitizer ?? throw new ArgumentNullException(nameof(yamlSanitizer));
    }

    public async Task<ManagementPolicyResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _policySnapshots.InitializeAndReadCurrentAsync(
            ProxyWardDefaultPolicy.CreateYaml(_policySnapshots.DatabasePath),
            cancellationToken).ConfigureAwait(false);
        var source = FormatSource(_policySnapshots.DatabasePath);

        return new ManagementPolicyResponse(
            Yaml: _yamlSanitizer.MaskSensitiveValues(snapshot.Yaml),
            PolicyHash: snapshot.Policy.VersionHash,
            Source: new ManagementPolicySource(
                Path: source,
                Format: "sqlite",
                Exists: true,
                LastModifiedUtc: snapshot.CreatedAtUtc,
                SizeBytes: null),
            Model: CreateModel(snapshot.Policy),
            ReadOnly: new ManagementPolicyReadOnlyFields(
                PolicyHash: snapshot.Policy.VersionHash,
                SourcePath: source,
                ServerCount: snapshot.Policy.Servers.Count,
                LoadedAtUtc: DateTimeOffset.UtcNow));
    }

    private static string FormatSource(string databasePath) =>
        $"sqlite:{databasePath}#policy_snapshots";

    public static ManagementPolicyModel CreateModel(ProxyWardPolicy policy) =>
        new(
            Mode: FormatMode(policy.Mode),
            Inspection: new ManagementInspectionPolicyModel(
                MaxBodyBytes: policy.Inspection.MaxBodyBytes,
                UnsupportedStreaming: FormatUnsupportedInspectionBehavior(policy.Inspection.UnsupportedStreaming),
                BatchToolCalls: FormatBatchToolCallBehavior(policy.Inspection.BatchToolCalls)),
            Audit: new ManagementAuditPolicyModel(
                Sink: policy.Audit.Sink,
                SqlitePath: policy.Audit.SqlitePath),
            Observability: new ManagementObservabilityPolicyModel(
                ServiceName: policy.Observability.ServiceName,
                Console: new ManagementConsoleExporterPolicyModel(policy.Observability.Console.Enabled),
                Otlp: new ManagementOtlpExporterPolicyModel(
                    policy.Observability.Otlp.Enabled,
                    policy.Observability.Otlp.Endpoint),
                ApplicationInsights: new ManagementApplicationInsightsPolicyModel(
                    policy.Observability.ApplicationInsights.Enabled,
                    policy.Observability.ApplicationInsights.ConnectionStringEnv),
                Sampling: new ManagementSamplingPolicyModel(policy.Observability.Sampling.TracesRatio)),
            Servers: policy.Servers.ToDictionary(
                pair => pair.Key,
                pair => CreateServerModel(pair.Value),
                StringComparer.Ordinal));

    private static ManagementServerPolicyModel CreateServerModel(ServerPolicy server) =>
        new(
            Id: server.Id,
            Route: server.Route,
            Upstream: MaskUriUserInfo(server.Upstream.ToString()),
            Allowed: server.Allowed,
            Secrets: new ManagementSecretsPolicyModel(
                RedactInLogs: server.Secrets.RedactInLogs,
                BlockReturn: server.Secrets.BlockReturn,
                Patterns: server.Secrets.Patterns),
            Tools: new ManagementToolPolicyModel(
                Default: FormatToolDefaultMode(server.Tools.Default),
                Allow: server.Tools.Allow,
                Block: server.Tools.Block),
            Arguments: new ManagementArgumentPolicyModel(
                Paths: new ManagementPathArgumentPolicyModel(
                    AllowedRoots: server.Arguments.Paths.AllowedRoots,
                    BlockTraversal: server.Arguments.Paths.BlockTraversal),
                Hosts: new ManagementHostArgumentPolicyModel(
                    Allow: server.Arguments.Hosts.Allow,
                    BlockPrivateNetworks: server.Arguments.Hosts.BlockPrivateNetworks),
                Commands: new ManagementCommandArgumentPolicyModel(
                    BlockShell: server.Arguments.Commands.BlockShell,
                    Dangerous: server.Arguments.Commands.Dangerous),
                Overrides: server.Arguments.Overrides.ToDictionary(
                    pair => pair.Key,
                    pair => CreateOverrideModel(pair.Value),
                    StringComparer.Ordinal)));

    private static ManagementToolArgumentPolicyOverrideModel CreateOverrideModel(
        ToolArgumentPolicyOverride overridePolicy) =>
        new(
            ToolName: overridePolicy.ToolName,
            Paths: overridePolicy.Paths is null
                ? null
                : new ManagementPathArgumentPolicyOverrideModel(
                    overridePolicy.Paths.AllowedRoots,
                    overridePolicy.Paths.BlockTraversal),
            Hosts: overridePolicy.Hosts is null
                ? null
                : new ManagementHostArgumentPolicyOverrideModel(
                    overridePolicy.Hosts.Allow,
                    overridePolicy.Hosts.BlockPrivateNetworks),
            Commands: overridePolicy.Commands is null
                ? null
                : new ManagementCommandArgumentPolicyOverrideModel(
                    overridePolicy.Commands.BlockShell,
                    overridePolicy.Commands.Dangerous));

    private static string? MaskUriUserInfo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.UserInfo))
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme).Append("://").Append(MaskedUserInfo).Append('@').Append(uri.Host);
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port);
        }

        builder.Append(uri.PathAndQuery);
        builder.Append(uri.Fragment);
        return builder.ToString();
    }

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string FormatToolDefaultMode(ToolDefaultMode mode) =>
        mode == ToolDefaultMode.Allow ? "allow" : "deny";

    private static string FormatUnsupportedInspectionBehavior(UnsupportedInspectionBehavior behavior) =>
        behavior switch
        {
            UnsupportedInspectionBehavior.Block => "block",
            UnsupportedInspectionBehavior.PassThrough => "passThrough",
            _ => "warn"
        };

    private static string FormatBatchToolCallBehavior(BatchToolCallBehavior behavior) =>
        behavior switch
        {
            _ => "failClosed"
        };
}

public sealed record ManagementPolicyResponse(
    string Yaml,
    string PolicyHash,
    ManagementPolicySource Source,
    ManagementPolicyModel Model,
    ManagementPolicyReadOnlyFields ReadOnly);

public sealed record ManagementPolicySource(
    string Path,
    string Format,
    bool Exists,
    DateTimeOffset? LastModifiedUtc,
    long? SizeBytes);

public sealed record ManagementPolicyReadOnlyFields(
    string PolicyHash,
    string SourcePath,
    int ServerCount,
    DateTimeOffset LoadedAtUtc);

public sealed record ManagementPolicyModel(
    string Mode,
    ManagementInspectionPolicyModel Inspection,
    ManagementAuditPolicyModel Audit,
    ManagementObservabilityPolicyModel Observability,
    IReadOnlyDictionary<string, ManagementServerPolicyModel> Servers);

public sealed record ManagementInspectionPolicyModel(
    int MaxBodyBytes,
    string UnsupportedStreaming,
    string BatchToolCalls);

public sealed record ManagementAuditPolicyModel(
    string Sink,
    string? SqlitePath);

public sealed record ManagementObservabilityPolicyModel(
    string ServiceName,
    ManagementConsoleExporterPolicyModel Console,
    ManagementOtlpExporterPolicyModel Otlp,
    ManagementApplicationInsightsPolicyModel ApplicationInsights,
    ManagementSamplingPolicyModel Sampling);

public sealed record ManagementConsoleExporterPolicyModel(bool Enabled);

public sealed record ManagementOtlpExporterPolicyModel(
    bool Enabled,
    string? Endpoint);

public sealed record ManagementApplicationInsightsPolicyModel(
    bool Enabled,
    string ConnectionStringEnv);

public sealed record ManagementSamplingPolicyModel(double TracesRatio);

public sealed record ManagementServerPolicyModel(
    string Id,
    string Route,
    string? Upstream,
    bool Allowed,
    ManagementSecretsPolicyModel? Secrets,
    ManagementToolPolicyModel Tools,
    ManagementArgumentPolicyModel Arguments);

public sealed record ManagementSecretsPolicyModel(
    bool RedactInLogs,
    bool BlockReturn,
    IReadOnlyCollection<string> Patterns);

public sealed record ManagementToolPolicyModel(
    string Default,
    IReadOnlyCollection<string> Allow,
    IReadOnlyCollection<string> Block);

public sealed record ManagementArgumentPolicyModel(
    ManagementPathArgumentPolicyModel Paths,
    ManagementHostArgumentPolicyModel Hosts,
    ManagementCommandArgumentPolicyModel Commands,
    IReadOnlyDictionary<string, ManagementToolArgumentPolicyOverrideModel> Overrides);

public sealed record ManagementPathArgumentPolicyModel(
    IReadOnlyCollection<string> AllowedRoots,
    bool BlockTraversal);

public sealed record ManagementHostArgumentPolicyModel(
    IReadOnlyCollection<string> Allow,
    bool BlockPrivateNetworks);

public sealed record ManagementCommandArgumentPolicyModel(
    bool BlockShell,
    IReadOnlyCollection<string> Dangerous);

public sealed record ManagementToolArgumentPolicyOverrideModel(
    string ToolName,
    ManagementPathArgumentPolicyOverrideModel? Paths,
    ManagementHostArgumentPolicyOverrideModel? Hosts,
    ManagementCommandArgumentPolicyOverrideModel? Commands);

public sealed record ManagementPathArgumentPolicyOverrideModel(
    IReadOnlyCollection<string>? AllowedRoots,
    bool? BlockTraversal);

public sealed record ManagementHostArgumentPolicyOverrideModel(
    IReadOnlyCollection<string>? Allow,
    bool? BlockPrivateNetworks);

public sealed record ManagementCommandArgumentPolicyOverrideModel(
    bool? BlockShell,
    IReadOnlyCollection<string>? Dangerous);

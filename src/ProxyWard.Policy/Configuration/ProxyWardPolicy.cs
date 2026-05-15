namespace ProxyWard.Policy.Configuration;

public sealed record ProxyWardPolicy(
    ProxyWardMode Mode,
    InspectionOptions Inspection,
    AuditOptions Audit,
    ObservabilityOptions Observability,
    IReadOnlyDictionary<string, ServerPolicy> Servers,
    string VersionHash);

public sealed record InspectionOptions(
    int MaxBodyBytes,
    UnsupportedInspectionBehavior UnsupportedStreaming,
    BatchToolCallBehavior BatchToolCalls);

public enum BatchToolCallBehavior
{
    FailClosed
}

public sealed record AuditOptions(bool Enabled);

public sealed record ObservabilityOptions(
    string ServiceName,
    ConsoleExporterOptions Console,
    OtlpExporterOptions Otlp,
    ApplicationInsightsOptions ApplicationInsights,
    SamplingOptions Sampling);

public sealed record ConsoleExporterOptions(bool Enabled);

public sealed record OtlpExporterOptions(bool Enabled, string? Endpoint);

public sealed record ApplicationInsightsOptions(
    bool Enabled,
    string ConnectionStringEnv);

public sealed record SamplingOptions(double TracesRatio);

public sealed record ServerPolicy(
    string Id,
    string Route,
    Uri Upstream,
    bool Allowed,
    SecretsPolicy Secrets,
    ToolPolicy Tools,
    ArgumentPolicy Arguments);

public sealed record SecretsPolicy(
    bool RedactInLogs,
    bool BlockReturn,
    IReadOnlyCollection<string> Patterns);

public sealed record ToolPolicy(
    ToolDefaultMode Default,
    IReadOnlyCollection<string> Allow,
    IReadOnlyCollection<string> Block,
    IReadOnlyCollection<string> Hide);

public sealed record ArgumentPolicy(
    PathArgumentPolicy Paths,
    HostArgumentPolicy Hosts,
    CommandArgumentPolicy Commands,
    IReadOnlyDictionary<string, ToolArgumentPolicyOverride> Overrides);

public sealed record PathArgumentPolicy(
    IReadOnlyCollection<string> AllowedRoots,
    bool BlockTraversal);

public sealed record HostArgumentPolicy(
    IReadOnlyCollection<string> Allow,
    bool BlockPrivateNetworks);

public sealed record CommandArgumentPolicy(
    bool BlockShell,
    IReadOnlyCollection<string> Dangerous);

public sealed record ToolArgumentPolicyOverride(
    string ToolName,
    PathArgumentPolicyOverride? Paths,
    HostArgumentPolicyOverride? Hosts,
    CommandArgumentPolicyOverride? Commands);

public sealed record PathArgumentPolicyOverride(
    IReadOnlyCollection<string>? AllowedRoots,
    bool? BlockTraversal);

public sealed record HostArgumentPolicyOverride(
    IReadOnlyCollection<string>? Allow,
    bool? BlockPrivateNetworks);

public sealed record CommandArgumentPolicyOverride(
    bool? BlockShell,
    IReadOnlyCollection<string>? Dangerous);

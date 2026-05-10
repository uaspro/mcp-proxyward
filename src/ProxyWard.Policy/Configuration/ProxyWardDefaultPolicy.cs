namespace ProxyWard.Policy.Configuration;

public static class ProxyWardDefaultPolicy
{
    public const string DefaultSampleUpstream = "http://sample-mcp:8080/mcp";

    public static string CreateYaml(
        string databasePath,
        string sampleUpstream = DefaultSampleUpstream,
        bool otlpEnabled = true)
    {
        var policy = new ProxyWardPolicy(
            Mode: ProxyWardMode.Audit,
            Inspection: new InspectionOptions(
                MaxBodyBytes: 1_048_576,
                UnsupportedStreaming: UnsupportedInspectionBehavior.Warn,
                BatchToolCalls: BatchToolCallBehavior.FailClosed),
            Audit: new AuditOptions("sqlite", databasePath),
            Observability: new ObservabilityOptions(
                "mcp-proxyward",
                new ConsoleExporterOptions(true),
                new OtlpExporterOptions(otlpEnabled, "http://otel-collector:4317"),
                new ApplicationInsightsOptions(false, "APPLICATIONINSIGHTS_CONNECTION_STRING"),
                new SamplingOptions(1.0)),
            Servers: new SortedDictionary<string, ServerPolicy>(StringComparer.Ordinal)
            {
                ["sample"] = new(
                    Id: "sample",
                    Route: "/sample/mcp",
                    Upstream: new Uri(sampleUpstream),
                    Allowed: true,
                    Secrets: new SecretsPolicy(
                        RedactInLogs: true,
                        BlockReturn: false,
                        Patterns: []),
                    Tools: new ToolPolicy(
                        Default: ToolDefaultMode.Allow,
                        Allow: [],
                        Block: []),
                    Arguments: new ArgumentPolicy(
                        Paths: new PathArgumentPolicy(
                            AllowedRoots: ["/workspace"],
                            BlockTraversal: false),
                        Hosts: new HostArgumentPolicy(
                            Allow: [],
                            BlockPrivateNetworks: false),
                        Commands: new CommandArgumentPolicy(
                            BlockShell: false,
                            Dangerous: []),
                        Overrides: new SortedDictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal)))
            },
            VersionHash: string.Empty);

        return ProxyWardPolicySerializer.ToYaml(ProxyWardPolicyLoader.Load(ProxyWardPolicySerializer.ToYaml(policy)));
    }
}

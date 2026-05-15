namespace ProxyWard.Policy.Configuration;

public static class ProxyWardDefaultPolicy
{
    public static string CreateYaml(
        string? databasePath = null,
        bool otlpEnabled = true)
    {
        var policy = new ProxyWardPolicy(
            Mode: ProxyWardMode.Audit,
            Inspection: new InspectionOptions(
                MaxBodyBytes: 1_048_576,
                UnsupportedStreaming: UnsupportedInspectionBehavior.Warn,
                BatchToolCalls: BatchToolCallBehavior.FailClosed),
            Audit: new AuditOptions(Enabled: true),
            Observability: new ObservabilityOptions(
                "mcp-proxyward",
                new ConsoleExporterOptions(true),
                new OtlpExporterOptions(otlpEnabled, "http://otel-collector:4317"),
                new ApplicationInsightsOptions(false, "APPLICATIONINSIGHTS_CONNECTION_STRING"),
                new SamplingOptions(1.0)),
            Servers: new SortedDictionary<string, ServerPolicy>(StringComparer.Ordinal),
            VersionHash: string.Empty);

        return ProxyWardPolicySerializer.ToYaml(ProxyWardPolicyLoader.Load(ProxyWardPolicySerializer.ToYaml(policy)));
    }
}

using ProxyWard.Api.Runtime;
using ProxyWard.Audit.Events;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class PolicyControlledAuditSinkTests
{
    [Fact]
    public async Task WriteAsyncUsesConfiguredBackendWhenAuditIsEnabled()
    {
        var backend = new CapturingAuditSink();
        var provider = new InMemoryProxyWardPolicyProvider(CreatePolicy(new AuditOptions(Enabled: true), "sha256:one"));
        await using var sink = new PolicyControlledAuditSink(provider, backend);

        await sink.WriteAsync(CreateAuditEvent("one"), CancellationToken.None);

        var written = Assert.Single(backend.Events);
        Assert.Equal("one", written.CorrelationId);
    }

    [Fact]
    public async Task WriteAsyncDropsEventWhenAuditIsDisabledByPolicy()
    {
        var backend = new CapturingAuditSink();
        var provider = new InMemoryProxyWardPolicyProvider(CreatePolicy(new AuditOptions(Enabled: false), "sha256:one"));
        await using var sink = new PolicyControlledAuditSink(provider, backend);

        await sink.WriteAsync(CreateAuditEvent("one"), CancellationToken.None);

        Assert.Empty(backend.Events);
    }

    [Fact]
    public async Task WriteAsyncHonorsRuntimeAuditEnabledFlagWithoutChangingBackend()
    {
        var backend = new CapturingAuditSink();
        var provider = new InMemoryProxyWardPolicyProvider(CreatePolicy(new AuditOptions(Enabled: false), "sha256:one"));
        await using var sink = new PolicyControlledAuditSink(provider, backend);

        await sink.WriteAsync(CreateAuditEvent("one"), CancellationToken.None);
        provider.Replace(CreatePolicy(new AuditOptions(Enabled: true), "sha256:two"));
        await sink.WriteAsync(CreateAuditEvent("two"), CancellationToken.None);

        var written = Assert.Single(backend.Events);
        Assert.Equal("two", written.CorrelationId);
    }

    private static ProxyWardPolicy CreatePolicy(AuditOptions audit, string versionHash) =>
        new(
            ProxyWardMode.Audit,
            new InspectionOptions(
                MaxBodyBytes: 1024,
                UnsupportedStreaming: UnsupportedInspectionBehavior.Warn,
                BatchToolCalls: BatchToolCallBehavior.FailClosed),
            audit,
            new ObservabilityOptions(
                "test",
                new ConsoleExporterOptions(false),
                new OtlpExporterOptions(false, null),
                new ApplicationInsightsOptions(false, "APPLICATIONINSIGHTS_CONNECTION_STRING"),
                new SamplingOptions(1.0)),
            new SortedDictionary<string, ServerPolicy>(StringComparer.Ordinal),
            versionHash);

    private static AuditEvent CreateAuditEvent(string correlationId) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "tool_call",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "sample",
            Method: "tools/call",
            ToolName: "sample.tool",
            Reasons: ["allowed"],
            PolicyVersion: "sha256:test",
            CorrelationId: correlationId,
            RequestBytes: 12,
            DurationMs: 3,
            ArgumentSummary: null,
            BatchSize: 1);

    private sealed class CapturingAuditSink : IAuditSink, IDisposable
    {
        public List<AuditEvent> Events { get; } = [];

        public bool Disposed { get; private set; }

        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}

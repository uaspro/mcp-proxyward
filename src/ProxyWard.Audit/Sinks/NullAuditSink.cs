using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public sealed class NullAuditSink : IAuditSink
{
    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

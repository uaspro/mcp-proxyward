using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public interface IBatchedAuditSink
{
    ValueTask WriteBatchAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken);
}

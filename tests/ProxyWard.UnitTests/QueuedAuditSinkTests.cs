using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;

namespace ProxyWard.UnitTests;

public class QueuedAuditSinkTests
{
    [Fact]
    public async Task WriteAsyncCompletesAfterEnqueueWithoutWaitingForInnerSink()
    {
        var inner = new BlockingAuditSink();
        await using var sink = new QueuedAuditSink(inner, capacity: 8);

        await sink.WriteAsync(CreateAuditEvent(1), CancellationToken.None);

        await inner.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inner.FirstWriteReleased.Task.IsCompleted);

        inner.ReleaseFirstWrite();
    }

    [Fact]
    public async Task DisposeAsyncFlushesQueuedEvents()
    {
        var inner = new CapturingAuditSink();
        var sink = new QueuedAuditSink(inner, capacity: 32);

        for (var index = 0; index < 25; index++)
        {
            await sink.WriteAsync(CreateAuditEvent(index), CancellationToken.None);
        }

        await sink.DisposeAsync();

        Assert.Equal(25, inner.Events.Count);
        Assert.Equal("corr-24", inner.Events[^1].CorrelationId);
    }

    [Fact]
    public async Task InnerSinkFailuresAreReportedAndDrainContinues()
    {
        var failures = new List<(AuditEvent Event, Exception Exception)>();
        var inner = new FailingFirstAuditSink();
        await using var sink = new QueuedAuditSink(
            inner,
            capacity: 8,
            onFailure: (auditEvent, exception) => failures.Add((auditEvent, exception)));

        await sink.WriteAsync(CreateAuditEvent(1), CancellationToken.None);
        await sink.WriteAsync(CreateAuditEvent(2), CancellationToken.None);

        await sink.DisposeAsync();

        var failure = Assert.Single(failures);
        Assert.Equal("corr-1", failure.Event.CorrelationId);
        Assert.Equal(2, inner.Attempts);
    }

    private static AuditEvent CreateAuditEvent(int index) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "sample",
            Method: "tools/call",
            ToolName: "fs.read",
            Reasons: [],
            PolicyVersion: "sha256:abc",
            CorrelationId: $"corr-{index}",
            RequestBytes: 128,
            DurationMs: 1,
            ArgumentSummary: null,
            BatchSize: 1);

    private sealed class BlockingAuditSink : IAuditSink
    {
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstWriteReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            FirstWriteStarted.TrySetResult();
            await FirstWriteReleased.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseFirstWrite() =>
            FirstWriteReleased.TrySetResult();
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingFirstAuditSink : IAuditSink
    {
        public int Attempts { get; private set; }

        public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new InvalidOperationException("synthetic audit failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}

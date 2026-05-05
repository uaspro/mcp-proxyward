using System.Threading.Channels;
using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public sealed class QueuedAuditSink : IAuditSink, IAsyncDisposable, IDisposable
{
    private readonly IAuditSink _inner;
    private readonly Channel<AuditEvent> _queue;
    private readonly Task _worker;
    private readonly Action<AuditEvent, Exception>? _onFailure;
    private int _disposed;

    public QueuedAuditSink(
        IAuditSink inner,
        int capacity = 8192,
        Action<AuditEvent, Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Audit queue capacity must be greater than zero.");
        }

        _inner = inner;
        _onFailure = onFailure;
        _queue = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(DrainAsync);
    }

    public async ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(QueuedAuditSink));
        }

        await _queue.Writer.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            await DisposeInnerAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        finally
        {
            DisposeInner();
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var auditEvent in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _inner.WriteAsync(auditEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportFailure(auditEvent, ex);
            }
        }
    }

    private void ReportFailure(AuditEvent auditEvent, Exception exception)
    {
        if (_onFailure is null)
        {
            return;
        }

        try
        {
            _onFailure(auditEvent, exception);
        }
        catch
        {
            // Audit failure reporting must not stop the background drain loop.
        }
    }

    private async ValueTask DisposeInnerAsync()
    {
        if (_inner is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void DisposeInner()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (_inner is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

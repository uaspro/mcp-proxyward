using System.Threading.Channels;
using ProxyWard.Audit.Events;

namespace ProxyWard.Audit.Sinks;

public sealed class QueuedAuditSink : IAuditSink, IAsyncDisposable, IDisposable
{
    private readonly IAuditSink _inner;
    private readonly Channel<AuditEvent> _queue;
    private readonly Task _worker;
    private readonly Action<AuditEvent, Exception>? _onFailure;
    private readonly int _batchSize;
    private int _disposed;

    public QueuedAuditSink(
        IAuditSink inner,
        int capacity = 8192,
        int batchSize = 128,
        Action<AuditEvent, Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Audit queue capacity must be greater than zero.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Audit queue batch size must be greater than zero.");
        }

        _inner = inner;
        _onFailure = onFailure;
        _batchSize = batchSize;
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
        var batch = new List<AuditEvent>(_batchSize);
        await foreach (var auditEvent in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(auditEvent);
            while (batch.Count < _batchSize && _queue.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            await WriteBatchOrReportAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private async Task WriteBatchOrReportAsync(IReadOnlyList<AuditEvent> batch)
    {
        if (_inner is IBatchedAuditSink batched)
        {
            try
            {
                await batched.WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                foreach (var auditEvent in batch)
                {
                    ReportFailure(auditEvent, ex);
                }

                return;
            }
        }

        foreach (var auditEvent in batch)
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

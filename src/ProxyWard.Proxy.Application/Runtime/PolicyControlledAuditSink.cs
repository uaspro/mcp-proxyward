using ProxyWard.Audit.Events;

namespace ProxyWard.Proxy.Application.Runtime;

public sealed class PolicyControlledAuditSink : IAuditSink, IAsyncDisposable, IDisposable
{
    private readonly IProxyWardPolicyProvider _policyProvider;
    private readonly IAuditSink _enabledSink;
    private bool _disposed;

    public PolicyControlledAuditSink(
        IProxyWardPolicyProvider policyProvider,
        IAuditSink enabledSink)
    {
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _enabledSink = enabledSink ?? throw new ArgumentNullException(nameof(enabledSink));
    }

    public ValueTask WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PolicyControlledAuditSink));
        }

        return _policyProvider.Current.Audit.Enabled
            ? _enabledSink.WriteAsync(auditEvent, cancellationToken)
            : ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_enabledSink is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_enabledSink is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_enabledSink is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

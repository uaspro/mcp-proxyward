namespace ProxyWard.Management.Api.Status;

public interface IProxyControlClient
{
    Task<ProxyControlProbeResult> ProbeAsync(CancellationToken cancellationToken);

    Task<ProxyControlStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<ProxyControlStatus> ApplyModeAsync(string mode, CancellationToken cancellationToken);

    Task<ProxyControlStatus> ApplyPolicySnapshotAsync(string yaml, CancellationToken cancellationToken);

    Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
        ProxyControlYarpConfigRequest request,
        CancellationToken cancellationToken);
}

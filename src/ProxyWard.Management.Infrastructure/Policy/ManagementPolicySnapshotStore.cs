using ProxyWard.Management.Application.Policy;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class ManagementPolicySnapshotStore : IManagementPolicySnapshotStore
{
    private readonly IPolicyStore _policyStore;

    public ManagementPolicySnapshotStore(IPolicyStore policyStore)
    {
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
    }

    public string SourceDescription => _policyStore.SourceDescription;

    public async Task<ManagementStoredPolicySnapshot> InitializeAndReadCurrentAsync(
        string defaultYaml,
        CancellationToken cancellationToken)
    {
        var snapshot = await _policyStore
            .InitializeAndReadCurrentAsync(defaultYaml, cancellationToken)
            .ConfigureAwait(false);

        return Map(snapshot);
    }

    public async Task<ManagementStoredPolicySnapshot?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _policyStore.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : Map(snapshot);
    }

    public async Task<ManagementStoredPolicySnapshot> SaveAsync(
        string yaml,
        string? requestedBy,
        string? note,
        CancellationToken cancellationToken)
    {
        var snapshot = await _policyStore.SaveAsync(
            yaml,
            requestedBy,
            note,
            cancellationToken).ConfigureAwait(false);

        return Map(snapshot);
    }

    private static ManagementStoredPolicySnapshot Map(StoredPolicySnapshot snapshot) =>
        new(
            snapshot.Id,
            snapshot.CreatedAtUtc,
            snapshot.PolicyHash,
            snapshot.Yaml,
            snapshot.Policy,
            snapshot.RequestedBy,
            snapshot.Note);
}

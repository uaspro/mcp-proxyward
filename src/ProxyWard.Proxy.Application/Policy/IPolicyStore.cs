using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Persistence;

public interface IPolicyStore
{
    string SourceDescription { get; }

    Task<StoredPolicySnapshot> InitializeAndReadCurrentAsync(
        string defaultYaml,
        CancellationToken cancellationToken = default);

    Task<StoredPolicySnapshot?> ReadCurrentAsync(CancellationToken cancellationToken = default);

    Task<StoredPolicySnapshot> SaveAsync(
        string yaml,
        string? requestedBy = null,
        string? note = null,
        CancellationToken cancellationToken = default);
}

public sealed record StoredPolicySnapshot(
    long Id,
    DateTimeOffset CreatedAtUtc,
    string PolicyHash,
    string Yaml,
    ProxyWardPolicy Policy,
    string? RequestedBy,
    string? Note);

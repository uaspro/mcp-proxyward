using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

public interface IManagementPolicySnapshotStore
{
    string SourceDescription { get; }

    Task<ManagementStoredPolicySnapshot> InitializeAndReadCurrentAsync(
        string defaultYaml,
        CancellationToken cancellationToken);

    Task<ManagementStoredPolicySnapshot?> ReadCurrentAsync(CancellationToken cancellationToken);

    Task<ManagementStoredPolicySnapshot> SaveAsync(
        string yaml,
        string? requestedBy,
        string? note,
        CancellationToken cancellationToken);
}

public interface IManagementPolicyYamlSanitizer
{
    string MaskSensitiveValues(string yaml);
}

public interface IManagementPolicyModelYamlSerializer
{
    string ToYaml(ManagementPolicyModel model);
}

public interface IManagementPolicyYamlCodec
{
    string RemovedLockfileMessage { get; }

    string CreateDefaultYaml();

    ProxyWardPolicy Load(string yaml);

    ProxyWardPolicy WithMode(ProxyWardPolicy policy, ProxyWardMode mode);

    string ToYaml(ProxyWardPolicy policy);
}

public interface IManagementPolicyAuditStore
{
    Task<IReadOnlyList<ManagementPolicyModeImpactItem>> ReadModeImpactAsync(
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken);

    Task WriteAsync(
        ManagementPolicyAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public sealed record ManagementStoredPolicySnapshot(
    long Id,
    DateTimeOffset CreatedAtUtc,
    string PolicyHash,
    string Yaml,
    ProxyWardPolicy Policy,
    string? RequestedBy,
    string? Note);

public sealed record ManagementPolicyAuditEvent(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Method,
    string Reasons,
    string PolicyVersion,
    string CorrelationId,
    long DurationMs,
    string PayloadJson);

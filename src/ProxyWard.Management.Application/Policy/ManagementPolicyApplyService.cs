using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

public sealed class ManagementPolicyApplyService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ManagementPolicyValidationService _validationService;
    private readonly IProxyControlClient _proxyControlClient;
    private readonly IManagementPolicySnapshotStore _policySnapshots;
    private readonly IManagementPolicyAuditStore _auditStore;

    public ManagementPolicyApplyService(
        ManagementPolicyValidationService validationService,
        IProxyControlClient proxyControlClient,
        IManagementPolicySnapshotStore policySnapshots,
        IManagementPolicyAuditStore auditStore)
    {
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));
        _policySnapshots = policySnapshots ?? throw new ArgumentNullException(nameof(policySnapshots));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
    }

    public async Task<ManagementPolicyApplyOutcome> ApplyAsync(
        ManagementPolicyValidationRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var proposal = await _validationService
            .ValidateProposalAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!proposal.Response.Valid || proposal.Policy is null)
        {
            return ManagementPolicyApplyOutcome.ValidationFailed(proposal.Response);
        }

        var previousSnapshot = await _policySnapshots.InitializeAndReadCurrentAsync(
            ProxyWardDefaultPolicy.CreateYaml(),
            cancellationToken).ConfigureAwait(false);
        var yarpConfig = ManagementPolicyYarpConfigFactory.Create(proposal.Policy);
        var previousStatus = await ReadCurrentStatusAsync(cancellationToken).ConfigureAwait(false);
        var yarpStatus = await ApplyYarpConfigAsync(yarpConfig, cancellationToken).ConfigureAwait(false);
        var appliedStatus = await ApplyPolicySnapshotAsync(proposal.Yaml, cancellationToken).ConfigureAwait(false);

        await PersistPolicySnapshotAsync(
            proposal.Yaml,
            previousSnapshot.Yaml,
            proposal,
            cancellationToken).ConfigureAwait(false);

        await WritePolicyApplyAuditAsync(
            previousStatus,
            appliedStatus,
            yarpStatus,
            proposal,
            stopwatch.ElapsedMilliseconds,
            cancellationToken).ConfigureAwait(false);

        return ManagementPolicyApplyOutcome.Applied(new ManagementPolicyApplyResponse(
            PreviousMode: previousStatus.Mode,
            Mode: appliedStatus.Mode,
            PreviousPolicyHash: previousStatus.PolicyVersion,
            PolicyHash: appliedStatus.PolicyVersion,
            ServerCount: appliedStatus.ServerCount,
            RouteVersion: appliedStatus.RouteVersion,
            Yarp: yarpStatus));
    }

    private async Task<ProxyControlStatus> ReadCurrentStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            throw new ManagementPolicyApplyException(
                "status",
                ex.Message,
                rollbackAttempted: false,
                rollbackApplied: false,
                ex);
        }
    }

    private async Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
        ProxyControlYarpConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient
                .ApplyYarpConfigAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            throw new ManagementPolicyApplyException(
                "yarp_config",
                ex.Message,
                rollbackAttempted: false,
                rollbackApplied: false,
                ex);
        }
    }

    private async Task<ProxyControlStatus> ApplyPolicySnapshotAsync(
        string yaml,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _proxyControlClient
                .ApplyPolicySnapshotAsync(yaml, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProxyControlClientException ex)
        {
            var rollback = await TryRollbackYarpAsync(cancellationToken).ConfigureAwait(false);
            throw new ManagementPolicyApplyException(
                "policy_snapshot",
                ex.Message,
                rollback.Attempted,
                rollback.Applied,
                ex);
        }
    }

    private async Task PersistPolicySnapshotAsync(
        string yaml,
        string? rollbackYaml,
        ManagementPolicyValidationOutcome proposal,
        CancellationToken cancellationToken)
    {
        try
        {
            await _policySnapshots.SaveAsync(
                yaml,
                proposal.RequestedBy,
                proposal.Note,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rollback = await TryRollbackRuntimePolicyAsync(rollbackYaml, cancellationToken).ConfigureAwait(false);
            throw new ManagementPolicyApplyException(
                "policy_db",
                $"Policy applied to runtime but could not save policy snapshot: {ex.Message}",
                rollback.Attempted,
                rollback.Applied,
                ex);
        }
    }

    private async Task<(bool Attempted, bool Applied)> TryRollbackYarpAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _policySnapshots.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return (Attempted: false, Applied: false);
        }

        try
        {
            var rollbackConfig = ManagementPolicyYarpConfigFactory.Create(snapshot.Policy);
            await _proxyControlClient.ApplyYarpConfigAsync(rollbackConfig, cancellationToken).ConfigureAwait(false);
            return (Attempted: true, Applied: true);
        }
        catch
        {
            return (Attempted: true, Applied: false);
        }
    }

    private async Task<(bool Attempted, bool Applied)> TryRollbackRuntimePolicyAsync(
        string? rollbackYaml,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rollbackYaml))
        {
            return (Attempted: false, Applied: false);
        }

        try
        {
            var rollbackPolicy = ProxyWardPolicyLoader.Load(rollbackYaml);
            var rollbackConfig = ManagementPolicyYarpConfigFactory.Create(rollbackPolicy);
            await _proxyControlClient.ApplyYarpConfigAsync(rollbackConfig, cancellationToken).ConfigureAwait(false);
            await _proxyControlClient.ApplyPolicySnapshotAsync(rollbackYaml, cancellationToken).ConfigureAwait(false);
            return (Attempted: true, Applied: true);
        }
        catch
        {
            return (Attempted: true, Applied: false);
        }
    }

    private async Task WritePolicyApplyAuditAsync(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ProxyControlYarpConfigStatus yarpStatus,
        ManagementPolicyValidationOutcome proposal,
        long durationMs,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var requestedBy = NormalizeText(proposal.RequestedBy) ?? "admin";
        var note = NormalizeText(proposal.Note);
        var correlationId = $"mgmt-{Guid.NewGuid():N}";
        var payloadJson = CreatePayloadJson(
            previousStatus,
            appliedStatus,
            yarpStatus,
            timestamp,
            requestedBy,
            note,
            correlationId,
            durationMs);

        await _auditStore.WriteAsync(
            new ManagementPolicyAuditEvent(
                TimestampUtc: timestamp,
                EventType: "policy_apply",
                Method: "policy/apply",
                Reasons: "policy_apply_accepted",
                PolicyVersion: appliedStatus.PolicyVersion,
                CorrelationId: correlationId,
                DurationMs: durationMs,
                PayloadJson: payloadJson),
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreatePayloadJson(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ProxyControlYarpConfigStatus yarpStatus,
        DateTimeOffset timestamp,
        string requestedBy,
        string? note,
        string correlationId,
        long durationMs)
    {
        var argumentSummary = new JsonObject
        {
            ["previousMode"] = NormalizeMode(previousStatus.Mode),
            ["mode"] = NormalizeMode(appliedStatus.Mode),
            ["previousPolicyHash"] = previousStatus.PolicyVersion,
            ["policyHash"] = appliedStatus.PolicyVersion,
            ["requestedBy"] = requestedBy,
            ["note"] = note,
            ["serverCount"] = appliedStatus.ServerCount,
            ["routeVersion"] = appliedStatus.RouteVersion,
            ["yarpRouteVersion"] = yarpStatus.RouteVersion,
            ["routeCount"] = yarpStatus.RouteCount,
            ["clusterCount"] = yarpStatus.ClusterCount
        };

        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["eventType"] = "policy_apply",
            ["mode"] = "management",
            ["decision"] = "allow",
            ["serverId"] = "management",
            ["method"] = "policy/apply",
            ["toolName"] = null,
            ["reasons"] = new JsonArray(JsonValue.Create("policy_apply_accepted")),
            ["policyVersion"] = appliedStatus.PolicyVersion,
            ["correlationId"] = correlationId,
            ["requestBytes"] = 0,
            ["durationMs"] = durationMs,
            ["batchSize"] = 0,
            ["batchIndex"] = null,
            ["argumentOverrideApplied"] = false,
            ["argumentSummary"] = argumentSummary
        };

        return payload.ToJsonString(CompactJsonOptions);
    }

    private static string NormalizeMode(string? mode) =>
        string.Equals(mode, "enforce", StringComparison.OrdinalIgnoreCase)
            ? "enforce"
            : "audit";

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed record ManagementPolicyApplyOutcome(
    ManagementPolicyApplyResponse? Response,
    ManagementPolicyValidationResponse? ValidationFailure)
{
    public bool IsApplied => Response is not null;

    public static ManagementPolicyApplyOutcome Applied(ManagementPolicyApplyResponse response) =>
        new(response, ValidationFailure: null);

    public static ManagementPolicyApplyOutcome ValidationFailed(ManagementPolicyValidationResponse validation) =>
        new(Response: null, validation);
}

public sealed record ManagementPolicyApplyResponse(
    string PreviousMode,
    string Mode,
    string PreviousPolicyHash,
    string PolicyHash,
    int ServerCount,
    int? RouteVersion,
    ProxyControlYarpConfigStatus Yarp);

public sealed class ManagementPolicyApplyException : Exception
{
    public ManagementPolicyApplyException(
        string phase,
        string message,
        bool rollbackAttempted,
        bool rollbackApplied,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        RollbackAttempted = rollbackAttempted;
        RollbackApplied = rollbackApplied;
    }

    public string Phase { get; }

    public bool RollbackAttempted { get; }

    public bool RollbackApplied { get; }
}

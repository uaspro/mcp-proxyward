using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Management.Application.Status;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Management.Application.Policy;

public sealed class ManagementPolicyModeService
{
    private static readonly TimeSpan DefaultImpactWindow = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IProxyControlClient _proxyControlClient;
    private readonly IManagementPolicySnapshotStore _policySnapshots;
    private readonly IManagementPolicyAuditStore _auditStore;

    public ManagementPolicyModeService(
        IProxyControlClient proxyControlClient,
        IManagementPolicySnapshotStore policySnapshots,
        IManagementPolicyAuditStore auditStore)
    {
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));
        _policySnapshots = policySnapshots ?? throw new ArgumentNullException(nameof(policySnapshots));
        _auditStore = auditStore ?? throw new ArgumentNullException(nameof(auditStore));
    }

    public async Task<ManagementPolicyModeImpactResponse> GetImpactAsync(
        string? mode,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken)
    {
        var targetMode = NormalizeMode(mode);
        var currentStatus = await _proxyControlClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var window = NormalizeWindow(fromUtc, toUtc);
        return await BuildImpactAsync(targetMode, currentStatus, window, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManagementPolicyModeSwitchResponse> SwitchModeAsync(
        ManagementPolicyModeSwitchRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ManagementPolicyModeRequestException(
                "mode_switch_request_invalid",
                "Request body is required.");
        }

        var targetMode = NormalizeMode(request.Mode);
        var currentStatus = await _proxyControlClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var currentPolicy = await _policySnapshots.InitializeAndReadCurrentAsync(
            ProxyWardDefaultPolicy.CreateYaml(_policySnapshots.DatabasePath),
            cancellationToken).ConfigureAwait(false);
        var window = NormalizeWindow(request.ImpactFromUtc, request.ImpactToUtc);
        var impact = await BuildImpactAsync(targetMode, currentStatus, window, cancellationToken).ConfigureAwait(false);

        if (impact.RequiresConfirmation)
        {
            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                throw new ManagementPolicyModeRequestException(
                    "mode_confirmation_required",
                    "Audit-to-enforce mode switch requires a confirmation token from the impact preview.");
            }

            if (!string.Equals(request.ConfirmationToken.Trim(), impact.ConfirmationToken, StringComparison.Ordinal))
            {
                throw new ManagementPolicyModeRequestException(
                    "mode_confirmation_invalid",
                    "Confirmation token does not match the current impact preview.");
            }
        }

        var appliedStatus = await _proxyControlClient
            .ApplyModeAsync(targetMode, cancellationToken)
            .ConfigureAwait(false);

        var persistedPolicy = ProxyWardPolicyLoader.WithMode(
            currentPolicy.Policy,
            targetMode == "enforce" ? ProxyWardMode.Enforce : ProxyWardMode.Audit);
        var persistedYaml = ProxyWardPolicySerializer.ToYaml(persistedPolicy);
        try
        {
            await _policySnapshots.SaveAsync(
                persistedYaml,
                request.RequestedBy,
                request.Note,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackModeAsync(currentStatus.Mode, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await WriteModeSwitchAuditAsync(
            currentStatus,
            appliedStatus,
            request,
            impact,
            cancellationToken).ConfigureAwait(false);

        return new ManagementPolicyModeSwitchResponse(
            Mode: appliedStatus.Mode,
            PreviousMode: currentStatus.Mode,
            PolicyHash: appliedStatus.PolicyVersion,
            PreviousPolicyHash: currentStatus.PolicyVersion,
            ServerCount: appliedStatus.ServerCount,
            RouteVersion: appliedStatus.RouteVersion,
            Impact: impact);
    }

    private async Task TryRollbackModeAsync(string previousMode, CancellationToken cancellationToken)
    {
        try
        {
            await _proxyControlClient.ApplyModeAsync(NormalizeMode(previousMode), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<ManagementPolicyModeImpactResponse> BuildImpactAsync(
        string targetMode,
        ProxyControlStatus currentStatus,
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken)
    {
        var affected = await _auditStore.ReadModeImpactAsync(window, cancellationToken).ConfigureAwait(false);
        var wouldBlockCount = affected.Sum(item => item.WouldBlockCount);
        var pendingDriftCount = affected.Sum(item => item.PendingDriftCount);
        var unapprovedDriftCount = affected.Sum(item => item.UnapprovedDriftCount);
        var requiresConfirmation = string.Equals(currentStatus.Mode, "audit", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetMode, "enforce", StringComparison.Ordinal);

        var response = new ManagementPolicyModeImpactResponse(
            CurrentMode: NormalizeMode(currentStatus.Mode),
            TargetMode: targetMode,
            CurrentPolicyHash: currentStatus.PolicyVersion,
            RequiresConfirmation: requiresConfirmation,
            ConfirmationToken: null,
            Window: window,
            WouldBlockCount: wouldBlockCount,
            PendingDriftCount: pendingDriftCount,
            UnapprovedDriftCount: unapprovedDriftCount,
            Affected: affected);

        return response with
        {
            ConfirmationToken = requiresConfirmation ? CreateConfirmationToken(response) : null
        };
    }

    private async Task WriteModeSwitchAuditAsync(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ManagementPolicyModeSwitchRequest request,
        ManagementPolicyModeImpactResponse impact,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var reason = $"mode_switch_{NormalizeMode(appliedStatus.Mode)}";
        var requestedBy = NormalizeText(request.RequestedBy) ?? "admin";
        var note = NormalizeText(request.Note);
        var correlationId = $"mgmt-{Guid.NewGuid():N}";
        var payloadJson = CreatePayloadJson(
            previousStatus,
            appliedStatus,
            impact,
            timestamp,
            reason,
            requestedBy,
            note,
            correlationId);

        await _auditStore.WriteAsync(
            new ManagementPolicyAuditEvent(
                TimestampUtc: timestamp,
                EventType: "policy_mode_switch",
                Method: "policy/mode",
                Reasons: reason,
                PolicyVersion: appliedStatus.PolicyVersion,
                CorrelationId: correlationId,
                PayloadJson: payloadJson),
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreatePayloadJson(
        ProxyControlStatus previousStatus,
        ProxyControlStatus appliedStatus,
        ManagementPolicyModeImpactResponse impact,
        DateTimeOffset timestamp,
        string reason,
        string requestedBy,
        string? note,
        string correlationId)
    {
        var argumentSummary = new JsonObject
        {
            ["previousMode"] = NormalizeMode(previousStatus.Mode),
            ["mode"] = NormalizeMode(appliedStatus.Mode),
            ["previousPolicyHash"] = previousStatus.PolicyVersion,
            ["policyHash"] = appliedStatus.PolicyVersion,
            ["requestedBy"] = requestedBy,
            ["note"] = note,
            ["wouldBlockCount"] = impact.WouldBlockCount,
            ["pendingDriftCount"] = impact.PendingDriftCount,
            ["unapprovedDriftCount"] = impact.UnapprovedDriftCount
        };

        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["eventType"] = "policy_mode_switch",
            ["mode"] = "management",
            ["decision"] = "allow",
            ["serverId"] = "management",
            ["method"] = "policy/mode",
            ["toolName"] = null,
            ["reasons"] = new JsonArray(JsonValue.Create(reason)),
            ["policyVersion"] = appliedStatus.PolicyVersion,
            ["correlationId"] = correlationId,
            ["requestBytes"] = 0,
            ["durationMs"] = 0,
            ["batchSize"] = 0,
            ["batchIndex"] = null,
            ["argumentOverrideApplied"] = false,
            ["argumentSummary"] = argumentSummary
        };

        return payload.ToJsonString(CompactJsonOptions);
    }

    private static string CreateConfirmationToken(ManagementPolicyModeImpactResponse impact)
    {
        var material = JsonSerializer.Serialize(new
        {
            impact.CurrentMode,
            impact.TargetMode,
            impact.CurrentPolicyHash,
            impact.Window.FromUtc,
            impact.Window.ToUtc,
            impact.WouldBlockCount,
            impact.PendingDriftCount,
            impact.UnapprovedDriftCount,
            Affected = impact.Affected.Select(item => new
            {
                item.ServerId,
                item.ToolName,
                item.WouldBlockCount,
                item.PendingDriftCount,
                item.UnapprovedDriftCount,
                item.Reasons
            })
        }, CompactJsonOptions);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static ManagementPolicyImpactWindow NormalizeWindow(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc.HasValue != toUtc.HasValue)
        {
            throw new ManagementPolicyModeRequestException(
                "impact_window_invalid",
                "fromUtc and toUtc must be provided together.");
        }

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            if (toUtc.Value < fromUtc.Value)
            {
                throw new ManagementPolicyModeRequestException(
                    "impact_window_invalid",
                    "toUtc must be greater than or equal to fromUtc.");
            }

            return new ManagementPolicyImpactWindow(fromUtc.Value.ToUniversalTime(), toUtc.Value.ToUniversalTime());
        }

        var to = DateTimeOffset.UtcNow;
        return new ManagementPolicyImpactWindow(to - DefaultImpactWindow, to);
    }

    private static string NormalizeMode(string? mode)
    {
        var normalized = mode?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "audit" => "audit",
            "enforce" => "enforce",
            _ => throw new ManagementPolicyModeRequestException(
                "mode_invalid",
                "mode must be 'audit' or 'enforce'.")
        };
    }

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

public sealed class ManagementPolicyModeRequestException : Exception
{
    public ManagementPolicyModeRequestException(string error, string message)
        : base(message)
    {
        Error = error;
    }

    public string Error { get; }
}

public sealed record ManagementPolicyModeSwitchRequest(
    string? Mode,
    string? ConfirmationToken,
    DateTimeOffset? ImpactFromUtc,
    DateTimeOffset? ImpactToUtc,
    string? RequestedBy,
    string? Note);

public sealed record ManagementPolicyImpactWindow(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record ManagementPolicyModeImpactResponse(
    string CurrentMode,
    string TargetMode,
    string CurrentPolicyHash,
    bool RequiresConfirmation,
    string? ConfirmationToken,
    ManagementPolicyImpactWindow Window,
    long WouldBlockCount,
    long PendingDriftCount,
    long UnapprovedDriftCount,
    IReadOnlyList<ManagementPolicyModeImpactItem> Affected);

public sealed record ManagementPolicyModeImpactItem(
    string ServerId,
    string? ToolName,
    long WouldBlockCount,
    long PendingDriftCount,
    long UnapprovedDriftCount,
    IReadOnlyList<string> Reasons);

public sealed record ManagementPolicyModeSwitchResponse(
    string Mode,
    string PreviousMode,
    string PolicyHash,
    string PreviousPolicyHash,
    int ServerCount,
    int? RouteVersion,
    ManagementPolicyModeImpactResponse Impact);

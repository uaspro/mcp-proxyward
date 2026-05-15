using ProxyWard.Core.Policies;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.Locking.Lockfiles;

public sealed class ToolSurfaceDriftEvaluator
{
    private readonly IToolFingerprinter _fingerprinter;
    private readonly ITrackedToolSchemaStore _store;
    private readonly ToolSchemaDiffMetadataOptions _metadataOptions;

    public ToolSurfaceDriftEvaluator(IToolFingerprinter fingerprinter, ITrackedToolSchemaStore store)
        : this(fingerprinter, store, ToolSchemaDiffMetadataOptions.Default)
    {
    }

    public ToolSurfaceDriftEvaluator(
        IToolFingerprinter fingerprinter,
        ITrackedToolSchemaStore store,
        ToolSchemaDiffMetadataOptions metadataOptions)
    {
        _fingerprinter = fingerprinter;
        _store = store;
        _metadataOptions = metadataOptions.Normalize();
    }

    public async Task<ToolSurfaceDriftResult> EvaluateAsync(
        string serverId,
        string upstreamUrl,
        string mcpProtocol,
        IReadOnlyCollection<DiscoveredTool> discoveredTools,
        string? policyVersion,
        string? sourceCorrelationId,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("serverId is required.", nameof(serverId));
        }

        ArgumentNullException.ThrowIfNull(discoveredTools);

        var fingerprintEntries = discoveredTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .Select(tool => SafeToolSchemaMetadata.CreateSnapshotEntry(tool, _fingerprinter, _metadataOptions))
            .ToArray();

        ToolSchemaVersionRow? latest;
        RecordedVersion recorded;
        try
        {
            latest = await _store.GetLatestAsync(serverId, cancellationToken).ConfigureAwait(false);

            var snapshot = new ToolSchemaSnapshotInput(
                serverId,
                upstreamUrl,
                mcpProtocol,
                fingerprintEntries,
                policyVersion,
                sourceCorrelationId);

            recorded = await _store
                .RecordAsync(snapshot, capturedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SchemaLockWriteFailedException ex)
        {
            return ToolSurfaceDriftResult.SkippedForWriteFailure(ex.Reason);
        }

        if (latest is null)
        {
            // Empty-state per server: first observation persists v1, no drift event.
            return ToolSurfaceDriftResult.NoDrift(recorded.Version, recorded.WasNewVersion);
        }

        var drifts = ComputeDrifts(latest, fingerprintEntries, mcpProtocol);

        if (drifts.Count == 0)
        {
            return new ToolSurfaceDriftResult(
                [],
                [],
                recorded.Version,
                recorded.WasNewVersion,
                UpstreamChanged: recorded.UpstreamChanged,
                PreviousUpstreamUrl: recorded.PreviousUpstreamUrl,
                CurrentUpstreamUrl: recorded.CurrentUpstreamUrl);
        }

        var aggregateReasons = drifts
            .SelectMany(drift => drift.Reasons)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ToolSurfaceDriftResult(
            drifts,
            aggregateReasons,
            recorded.Version,
            recorded.WasNewVersion,
            PreviousVersion: latest.Version,
            PreviousMcpProtocol: latest.McpProtocol,
            CurrentMcpProtocol: mcpProtocol);
    }

    private static IReadOnlyList<ToolSurfaceDrift> ComputeDrifts(
        ToolSchemaVersionRow? latest,
        IReadOnlyList<ToolSchemaSnapshotEntry> currentEntries,
        string currentMcpProtocol)
    {
        if (latest is null)
        {
            return [];
        }

        var stored = latest.Fingerprints.ToDictionary(entry => entry.ToolName, StringComparer.Ordinal);
        var protocolChanged = !string.Equals(latest.McpProtocol, currentMcpProtocol, StringComparison.Ordinal);

        var drifts = new List<ToolSurfaceDrift>();

        foreach (var entry in currentEntries)
        {
            if (!stored.TryGetValue(entry.ToolName, out var storedEntry))
            {
                continue;
            }

            var reasons = new SortedSet<string>(StringComparer.Ordinal);

            if (!StringEquals(storedEntry.Fingerprint.DescriptionHash, entry.Fingerprint.DescriptionHash)
                || !StringEquals(storedEntry.Fingerprint.TitleHash, entry.Fingerprint.TitleHash))
            {
                reasons.Add(PolicyReasonCodes.ToolDescriptionChanged);
            }

            if (!StringEquals(storedEntry.Fingerprint.InputSchemaHash, entry.Fingerprint.InputSchemaHash)
                || !StringEquals(storedEntry.Fingerprint.OutputSchemaHash, entry.Fingerprint.OutputSchemaHash))
            {
                reasons.Add(PolicyReasonCodes.ToolSchemaChanged);
            }

            if (protocolChanged)
            {
                reasons.Add(PolicyReasonCodes.McpProtocolChanged);
            }

            if (reasons.Count > 0)
            {
                drifts.Add(new ToolSurfaceDrift(entry.ToolName, reasons.ToArray(), storedEntry, entry));
            }
        }

        // Protocol-only drift with otherwise-identical fingerprints still surfaces as a drift event,
        // attached to every overlapping tool so operators see the protocol change in audit summary.
        if (protocolChanged && drifts.Count == 0)
        {
            foreach (var entry in currentEntries)
            {
                if (stored.ContainsKey(entry.ToolName))
                {
                    drifts.Add(new ToolSurfaceDrift(entry.ToolName, [PolicyReasonCodes.McpProtocolChanged], stored[entry.ToolName], entry));
                }
            }
        }

        return drifts;
    }

    private static bool StringEquals(string? first, string? second) =>
        string.Equals(first, second, StringComparison.Ordinal);
}

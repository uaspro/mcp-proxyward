using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProxyWard.Core.Policies;
using ProxyWard.Locking.Lockfiles;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.UnitTests;

public class ToolSurfaceDriftEvaluatorTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-drift-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var path in new[]
        {
            _databasePath,
            $"{_databasePath}-shm",
            $"{_databasePath}-wal"
        })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best-effort cleanup; SQLite may still hold the file briefly.
                }
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstObservationPersistsVersionOneWithNoDrift()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var tool = new DiscoveredTool("repos.search", "Search", "Description", new JsonObject(), null);
        var result = await evaluator.EvaluateAsync(
            serverId: "github",
            upstreamUrl: "https://github-mcp/",
            mcpProtocol: "2025-11-25",
            discoveredTools: [tool],
            policyVersion: "sha256:abc",
            sourceCorrelationId: "corr-1",
            capturedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.False(result.HasDrift);
        Assert.Empty(result.Reasons);
        Assert.Equal(1, result.Version);
        Assert.True(result.WasNewVersion);
    }

    [Fact]
    public async Task IdempotentReObservationDoesNotProduceDrift()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var tool = new DiscoveredTool("repos.search", "Search", "Description", new JsonObject(), null);
        await evaluator.EvaluateAsync(
            serverId: "github",
            upstreamUrl: "https://github-mcp/",
            mcpProtocol: "2025-11-25",
            discoveredTools: [tool],
            policyVersion: null,
            sourceCorrelationId: null,
            capturedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        var second = await evaluator.EvaluateAsync(
            serverId: "github",
            upstreamUrl: "https://github-mcp/",
            mcpProtocol: "2025-11-25",
            discoveredTools: [tool],
            policyVersion: null,
            sourceCorrelationId: null,
            capturedAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken: CancellationToken.None);

        Assert.False(second.HasDrift);
        Assert.Empty(second.Reasons);
        Assert.Equal(1, second.Version);
        Assert.False(second.WasNewVersion);
    }

    [Fact]
    public async Task DescriptionDriftBumpsVersionAndRecordsReason()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var baseline = new DiscoveredTool("repos.search", "Search", "Old description", new JsonObject(), null);
        var changed = baseline with { Description = "New description" };

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [baseline], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [changed], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.True(second.HasDrift);
        Assert.Contains(PolicyReasonCodes.ToolDescriptionChanged, second.Reasons);
        Assert.Equal(2, second.Version);
        Assert.True(second.WasNewVersion);
        var drift = Assert.Single(second.Drifts);
        Assert.Equal("repos.search", drift.ToolName);
        Assert.Contains(PolicyReasonCodes.ToolDescriptionChanged, drift.Reasons);
    }

    [Fact]
    public async Task MetadataCaptureDoesNotChangeSnapshotHash()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var tool = new DiscoveredTool(
            "repos.search",
            "Search",
            "Description",
            JsonNode.Parse("""{"type":"object","properties":{"token":{"type":"string","default":"secret-value"}}}"""),
            null);

        var captureEvaluator = new ToolSurfaceDriftEvaluator(
            new ToolFingerprinter(),
            store,
            new ToolSchemaDiffMetadataOptions(CaptureValues: true, MaxValueBytes: 4096));
        var hashOnlyEvaluator = new ToolSurfaceDriftEvaluator(
            new ToolFingerprinter(),
            store,
            new ToolSchemaDiffMetadataOptions(CaptureValues: false, MaxValueBytes: 4096));

        var first = await captureEvaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await hashOnlyEvaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, first.Version);
        Assert.True(first.WasNewVersion);
        Assert.Equal(1, second.Version);
        Assert.False(second.WasNewVersion);
        Assert.False(second.HasDrift);
    }

    [Fact]
    public async Task MetadataCaptureStoresBoundedRedactedSchemaMetadata()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(
            new ToolFingerprinter(),
            store,
            new ToolSchemaDiffMetadataOptions(CaptureValues: true, MaxValueBytes: 4096));
        var tool = new DiscoveredTool(
            "repos.search",
            "Search",
            "Description",
            JsonNode.Parse("""{"properties":{"token":{"default":"secret-value","type":"string"},"q":{"type":"string"}},"type":"object"}"""),
            null);

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprints FROM tool_schema_versions WHERE server_id = 'github';";
        var fingerprintsJson = (string)(await command.ExecuteScalarAsync())!;

        using var document = JsonDocument.Parse(fingerprintsJson);
        var toolElement = document.RootElement.GetProperty("tools")[0];
        Assert.Equal("Description", toolElement.GetProperty("description").GetString());
        var inputSchema = toolElement.GetProperty("inputSchemaJson").GetString();
        Assert.NotNull(inputSchema);
        Assert.Contains("[redacted]", inputSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", inputSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataCaptureOmitsValuesOverConfiguredLimit()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(
            new ToolFingerprinter(),
            store,
            new ToolSchemaDiffMetadataOptions(CaptureValues: true, MaxValueBytes: 24));
        var tool = new DiscoveredTool(
            "repos.search",
            "Search",
            new string('d', 100),
            JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
            null);

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprints FROM tool_schema_versions WHERE server_id = 'github';";
        var fingerprintsJson = (string)(await command.ExecuteScalarAsync())!;

        using var document = JsonDocument.Parse(fingerprintsJson);
        var toolElement = document.RootElement.GetProperty("tools")[0];
        Assert.Equal(JsonValueKind.Null, toolElement.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, toolElement.GetProperty("inputSchemaJson").ValueKind);
    }

    [Fact]
    public async Task SchemaDriftBumpsVersionAndRecordsReason()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var baseline = new DiscoveredTool(
            "repos.search",
            "Search",
            "Description",
            JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
            JsonNode.Parse("""{"type":"object","properties":{"items":{"type":"array"}}}"""));
        var changed = baseline with
        {
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"items":{"type":"array"},"total":{"type":"integer"}}}""")
        };

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [baseline], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [changed], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.True(second.HasDrift);
        Assert.Contains(PolicyReasonCodes.ToolSchemaChanged, second.Reasons);
        Assert.Equal(2, second.Version);
    }

    [Fact]
    public async Task McpProtocolChangeAloneBumpsVersionAndRecordsProtocolDrift()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var tool = new DiscoveredTool("repos.search", "Search", "Description", new JsonObject(), null);

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2026-01-01",
            [tool], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.True(second.HasDrift);
        Assert.Contains(PolicyReasonCodes.McpProtocolChanged, second.Reasons);
        Assert.Equal(2, second.Version);
        Assert.True(second.WasNewVersion);
        var drift = Assert.Single(second.Drifts);
        Assert.Contains(PolicyReasonCodes.McpProtocolChanged, drift.Reasons);
    }

    [Fact]
    public async Task UnknownToolsAgainstStoredBaselineDoNotProduceDrift()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var baseline = new DiscoveredTool("repos.search", null, null, null, null);
        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [baseline], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var unknownTool = new DiscoveredTool("issues.list", null, "Different description", new JsonObject(), null);

        var second = await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [unknownTool], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        // The new tool produces a different snapshot_hash → version=2, but no per-tool drift reason
        // because the unknown tool wasn't in the baseline.
        Assert.False(second.HasDrift);
        Assert.Empty(second.Reasons);
        Assert.Equal(2, second.Version);
        Assert.True(second.WasNewVersion);
    }

    [Fact]
    public async Task PolicyVersionAndCorrelationIdArePersistedToTheRow()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);

        var tool = new DiscoveredTool("repos.search", "Search", "Description", new JsonObject(), null);
        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool],
            policyVersion: "sha256:policy-abc",
            sourceCorrelationId: "corr-xyz",
            capturedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT policy_version, source_correlation_id
            FROM tool_schema_versions
            WHERE server_id = 'github';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("sha256:policy-abc", reader.GetString(0));
        Assert.Equal("corr-xyz", reader.GetString(1));
    }

    [Fact]
    public async Task UpstreamUrlChangeWithoutFingerprintChangeReturnsOperationalSignal()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), store);
        var tool = new DiscoveredTool("repos.search", "Search", "Description", new JsonObject(), null);

        await evaluator.EvaluateAsync(
            "github", "https://github-mcp/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await evaluator.EvaluateAsync(
            "github", "https://github-mcp-new/", "2025-11-25",
            [tool], null, null, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.False(second.HasDrift);
        Assert.True(second.UpstreamChanged);
        Assert.Equal(1, second.Version);
        Assert.Equal("https://github-mcp/", second.PreviousUpstreamUrl);
        Assert.Equal("https://github-mcp-new/", second.CurrentUpstreamUrl);
    }

    [Fact]
    public async Task WriteFailureSkipsDriftEvaluationAndDoesNotThrowPastEvaluator()
    {
        using (var writable = new SqliteTrackedToolSchemaStore(_databasePath))
        {
            await writable.RecordAsync(
                new ToolSchemaSnapshotInput(
                    "github",
                    "https://github-mcp/",
                    "2025-11-25",
                    [new ToolSchemaSnapshotEntry(
                        "repos.search",
                        new ToolFingerprinter().Fingerprint(new DiscoveredTool("repos.search", "Search", "Old", new JsonObject(), null)))]),
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        using var readOnly = new SqliteTrackedToolSchemaStore(
            _databasePath,
            busyTimeoutMilliseconds: 50,
            openMode: SqliteOpenMode.ReadOnly);
        var evaluator = new ToolSurfaceDriftEvaluator(new ToolFingerprinter(), readOnly);

        var result = await evaluator.EvaluateAsync(
            "github",
            "https://github-mcp/",
            "2025-11-25",
            [new DiscoveredTool("repos.search", "Search", "Changed", new JsonObject(), null)],
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.False(result.HasDrift);
        Assert.Equal(SchemaLockWriteFailureReasons.DbReadonly, result.WriteFailure!.Reason);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Locking.Persistence;
using ProxyWard.Management.Application.Status;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementPolicyModeEndpointTests : IAsyncLifetime
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string AdminTokenEnv = "PROXYWARD_MANAGEMENT_ADMIN_TOKEN";

    private static readonly DateTimeOffset WindowFrom = new(2026, 5, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowTo = new(2026, 5, 10, 11, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-policy-mode-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, null);
        Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        DeleteDbFiles(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ImpactEndpointReturnsWouldBlockDriftAffectedToolsAndConfirmationToken()
    {
        await SeedImpactDataAsync();
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);

        var stub = new StubProxyControlClient
        {
            Status = new ProxyControlStatus("audit", "sha256:old", 2, 3)
        };

        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/policy/impact?mode=enforce&fromUtc={Uri.EscapeDataString(WindowFrom.UtcDateTime.ToString("o"))}&toUtc={Uri.EscapeDataString(WindowTo.UtcDateTime.ToString("o"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;

        Assert.Equal("audit", root.GetProperty("currentMode").GetString());
        Assert.Equal("enforce", root.GetProperty("targetMode").GetString());
        Assert.Equal("sha256:old", root.GetProperty("currentPolicyHash").GetString());
        Assert.True(root.GetProperty("requiresConfirmation").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("confirmationToken").GetString()));
        Assert.Equal(2, root.GetProperty("wouldBlockCount").GetInt64());
        Assert.Equal(2, root.GetProperty("pendingDriftCount").GetInt64());
        Assert.Equal(3, root.GetProperty("unapprovedDriftCount").GetInt64());

        var affected = root.GetProperty("affected").EnumerateArray().ToArray();
        var alpha = Assert.Single(affected, item =>
            item.GetProperty("serverId").GetString() == "alpha"
            && item.GetProperty("toolName").GetString() == "fs.read");
        Assert.Equal(2, alpha.GetProperty("wouldBlockCount").GetInt64());
        Assert.Equal(2, alpha.GetProperty("pendingDriftCount").GetInt64());
        Assert.Equal(2, alpha.GetProperty("unapprovedDriftCount").GetInt64());
        Assert.Contains("path_traversal", alpha.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));

        var beta = Assert.Single(affected, item =>
            item.GetProperty("serverId").GetString() == "beta"
            && item.GetProperty("toolName").GetString() == "net.fetch");
        Assert.Equal(0, beta.GetProperty("wouldBlockCount").GetInt64());
        Assert.Equal(0, beta.GetProperty("pendingDriftCount").GetInt64());
        Assert.Equal(1, beta.GetProperty("unapprovedDriftCount").GetInt64());
    }

    [Fact]
    public async Task ModeSwitchRequiresAdminAuthorization()
    {
        await SeedImpactDataAsync();
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            JsonBody("""{"mode":"enforce"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.AppliedModes);
    }

    [Fact]
    public async Task AuditToEnforceSwitchRequiresMatchingConfirmationToken()
    {
        await SeedImpactDataAsync();
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient
        {
            Status = new ProxyControlStatus("audit", "sha256:old", 2, 3)
        };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            JsonBody($$"""
            {
              "mode": "enforce",
              "confirmationToken": "wrong-token",
              "impactFromUtc": "{{WindowFrom.UtcDateTime:o}}",
              "impactToUtc": "{{WindowTo.UtcDateTime:o}}"
            }
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(stub.AppliedModes);

        using var payload = await ReadJsonAsync(response);
        Assert.Equal("mode_confirmation_invalid", payload.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfirmedModeSwitchCallsProxyAndWritesAuditEvent()
    {
        await SeedImpactDataAsync();
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        var stub = new StubProxyControlClient
        {
            Status = new ProxyControlStatus("audit", "sha256:old", 2, 3),
            ApplyResult = new ProxyControlStatus("enforce", "sha256:new", 2, 3)
        };

        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        var token = await ReadConfirmationTokenAsync(client);

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            JsonBody($$"""
            {
              "mode": "enforce",
              "confirmationToken": "{{token}}",
              "impactFromUtc": "{{WindowFrom.UtcDateTime:o}}",
              "impactToUtc": "{{WindowTo.UtcDateTime:o}}",
              "requestedBy": "alice",
              "note": "Reviewed impact"
            }
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["enforce"], stub.AppliedModes);

        using var payload = await ReadJsonAsync(response);
        var root = payload.RootElement;
        Assert.Equal("audit", root.GetProperty("previousMode").GetString());
        Assert.Equal("enforce", root.GetProperty("mode").GetString());
        Assert.Equal("sha256:old", root.GetProperty("previousPolicyHash").GetString());
        Assert.Equal("sha256:new", root.GetProperty("policyHash").GetString());
        Assert.Equal(2, root.GetProperty("impact").GetProperty("wouldBlockCount").GetInt64());

        var audit = Assert.Single(await ReadModeSwitchAuditRowsAsync());
        Assert.Equal("policy_mode_switch", audit.EventType);
        Assert.Equal("management", audit.Mode);
        Assert.Equal("allow", audit.Decision);
        Assert.Equal("management", audit.ServerId);
        Assert.Equal("policy/mode", audit.Method);
        Assert.Null(audit.ToolName);
        Assert.Equal("mode_switch_enforce", audit.Reasons);
        Assert.Equal("sha256:new", audit.PolicyVersion);
        Assert.Contains("\"previousMode\":\"audit\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"enforce\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"requestedBy\":\"alice\"", audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(token, audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedModeSwitchWritesAuditWhenReadOnlySharedCacheConnectionExists()
    {
        await CreateEmptyDatabaseAsync();
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        await using var readOnlyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await readOnlyConnection.OpenAsync();

        var stub = new StubProxyControlClient
        {
            Status = new ProxyControlStatus("audit", "sha256:old", 1, 1),
            ApplyResult = new ProxyControlStatus("enforce", "sha256:new", 1, 1)
        };

        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");

        var token = await ReadConfirmationTokenAsync(client);

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            JsonBody($$"""
            {
              "mode": "enforce",
              "confirmationToken": "{{token}}",
              "impactFromUtc": "{{WindowFrom.UtcDateTime:o}}",
              "impactToUtc": "{{WindowTo.UtcDateTime:o}}",
              "requestedBy": "alice"
            }
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["enforce"], stub.AppliedModes);

        var audit = Assert.Single(await ReadModeSwitchAuditRowsAsync());
        Assert.Equal("policy_mode_switch", audit.EventType);
        Assert.Equal("mode_switch_enforce", audit.Reasons);
    }

    private async Task<string> ReadConfirmationTokenAsync(HttpClient client)
    {
        using var impactResponse = await client.GetAsync(
            $"/api/policy/impact?mode=enforce&fromUtc={Uri.EscapeDataString(WindowFrom.UtcDateTime.ToString("o"))}&toUtc={Uri.EscapeDataString(WindowTo.UtcDateTime.ToString("o"))}");
        Assert.Equal(HttpStatusCode.OK, impactResponse.StatusCode);

        using var payload = await ReadJsonAsync(impactResponse);
        return payload.RootElement.GetProperty("confirmationToken").GetString()!;
    }

    private async Task SeedImpactDataAsync()
    {
        using (var sink = new SqliteAuditSink(_databasePath))
        {
            await sink.WriteAsync(CreateEvent(
                WindowFrom.AddMinutes(5),
                AuditDecision.WouldBlock,
                "alpha",
                "fs.read",
                ["path_traversal"]), CancellationToken.None);
            await sink.WriteAsync(CreateEvent(
                WindowFrom.AddMinutes(10),
                AuditDecision.WouldBlock,
                "alpha",
                "fs.read",
                ["private_network"]), CancellationToken.None);
            await sink.WriteAsync(CreateEvent(
                WindowFrom.AddMinutes(15),
                AuditDecision.Block,
                "alpha",
                "fs.write",
                ["blocked"]), CancellationToken.None);
            await sink.WriteAsync(CreateEvent(
                WindowFrom.AddHours(-2),
                AuditDecision.WouldBlock,
                "gamma",
                "old.tool",
                ["outside_window"]), CancellationToken.None);
        }

        await SeedDriftAsync("alpha", "fs.read", "description", "pending", WindowFrom.AddMinutes(20));
        await SeedDriftAsync("alpha", "fs.read", "schema", "pending", WindowFrom.AddMinutes(25));
        await SeedDriftAsync("beta", "net.fetch", "schema", "rejected", WindowFrom.AddMinutes(30));
        await SeedDriftAsync("gamma", "old.tool", "schema", "pending", WindowFrom.AddHours(-2));
    }

    private async Task SeedDriftAsync(
        string serverId,
        string toolName,
        string fieldName,
        string status,
        DateTimeOffset detectedAtUtc)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        var result = await store.RecordObservationAsync(
            new DriftReviewObservation(
                ServerId: serverId,
                ToolName: toolName,
                FieldName: fieldName,
                FromVersion: 1,
                ToVersion: 2,
                Reasons: [$"{fieldName}_changed"],
                PolicyVersion: "sha256:old",
                DetectedAtUtc: detectedAtUtc),
            CancellationToken.None);

        if (!string.Equals(status, "pending", StringComparison.Ordinal))
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_drift_reviews SET status = $status WHERE id = $id;";
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$id", result.Row.Id);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static AuditEvent CreateEvent(
        DateTimeOffset timestamp,
        AuditDecision decision,
        string serverId,
        string? toolName,
        IReadOnlyCollection<string> reasons) =>
        new(
            Timestamp: timestamp,
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: decision,
            ServerId: serverId,
            Method: "tools/call",
            ToolName: toolName,
            Reasons: reasons,
            PolicyVersion: "sha256:old",
            CorrelationId: $"corr-{Guid.NewGuid():N}",
            RequestBytes: 12,
            DurationMs: 3,
            ArgumentSummary: JsonNode.Parse("""{"path":"/workspace/file.txt"}"""),
            BatchSize: 0);

    private async Task<IReadOnlyList<ModeSwitchAuditRow>> ReadModeSwitchAuditRowsAsync()
    {
        var rows = new List<ModeSwitchAuditRow>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type, mode, decision, server_id, method, tool_name, reasons, policy_version, payload_json
            FROM audit_events
            WHERE event_type = 'policy_mode_switch'
            ORDER BY id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ModeSwitchAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static WebApplicationFactory<ManagementProgram> CreateFactory(IProxyControlClient stub) =>
        new WebApplicationFactory<ManagementProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(stub);
                services.AddSingleton<IProxyControlClient>(stub);
            }));

    private async Task CreateEmptyDatabaseAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync();
    }

    private static void DeleteDbFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private sealed class StubProxyControlClient : IProxyControlClient
    {
        public ProxyControlStatus Status { get; set; } =
            new("audit", "sha256:old", 1, 1);

        public ProxyControlStatus ApplyResult { get; set; } =
            new("enforce", "sha256:new", 1, 1);

        public List<string> AppliedModes { get; } = [];

        public Task<ProxyControlProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlProbeResult(
                ComponentStatusValues.Healthy,
                Notes: null,
                Details: Status.ToDetails()));

        public Task<ProxyControlStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<ProxyControlStatus> ApplyModeAsync(string mode, CancellationToken cancellationToken)
        {
            AppliedModes.Add(mode);
            Status = ApplyResult;
            return Task.FromResult(ApplyResult);
        }

        public Task<ProxyControlStatus> ApplyPolicySnapshotAsync(string yaml, CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
            ProxyControlYarpConfigRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlYarpConfigStatus(
                RouteVersion: Status.RouteVersion ?? 1,
                RouteCount: 0,
                ClusterCount: 0));
    }

    private sealed record ModeSwitchAuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PolicyVersion,
        string PayloadJson);
}

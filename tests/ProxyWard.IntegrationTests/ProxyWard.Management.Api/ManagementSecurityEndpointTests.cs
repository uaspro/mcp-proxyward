using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ProxyWard.Management.Application.Status;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementSecurityEndpointTests : IAsyncLifetime
{
    private const string AuditDbEnv = "PROXYWARD_MANAGEMENT_AUDIT_DB_PATH";
    private const string AdminTokenEnv = "PROXYWARD_MANAGEMENT_ADMIN_TOKEN";
    private const string SharedAdminTokenEnv = "PROXYWARD_ADMIN_TOKEN";
    private const string LocalDevEnv = "PROXYWARD_MANAGEMENT_LOCAL_DEV";
    private const string CorsOriginsEnv = "PROXYWARD_MANAGEMENT_CORS_ALLOWED_ORIGINS";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-security-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, null);
        Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        Environment.SetEnvironmentVariable(SharedAdminTokenEnv, null);
        Environment.SetEnvironmentVariable(LocalDevEnv, null);
        Environment.SetEnvironmentVariable(CorsOriginsEnv, null);
        TestFiles.DeleteSqlite(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ManagementWriteRequiresAdminTokenOutsideLocalDevMode()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            TestJson.Content("""{"mode":"audit"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.AppliedModes);

        var audit = Assert.Single(await ReadAuthFailureAuditRowsAsync());
        Assert.Equal("admin_token_not_configured", audit.Reasons);
        Assert.True(audit.DurationMs >= 0);
        Assert.DoesNotContain("Authorization", audit.PayloadJson, StringComparison.OrdinalIgnoreCase);
        using (var auditPayload = JsonDocument.Parse(audit.PayloadJson))
        {
            Assert.Equal(audit.DurationMs, auditPayload.RootElement.GetProperty("durationMs").GetInt64());
        }
    }

    [Fact]
    public async Task ManagementWriteAllowsExplicitLocalDevModeWithoutAdminToken()
    {
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(LocalDevEnv, "true");

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            TestJson.Content("""{"mode":"audit"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["audit"], stub.AppliedModes);
    }

    [Fact]
    public async Task ManagementAuthFailureAuditDoesNotPersistTokenValues()
    {
        const string expectedToken = "test-admin-token";
        const string suppliedToken = "wrong-token";
        Environment.SetEnvironmentVariable(AuditDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, expectedToken);

        var stub = new StubProxyControlClient();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", suppliedToken);

        using var response = await client.PatchAsync(
            "/api/policy/mode",
            TestJson.Content("""{"mode":"audit"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(stub.AppliedModes);

        var audit = Assert.Single(await ReadAuthFailureAuditRowsAsync());
        Assert.Equal("bearer_token_invalid", audit.Reasons);
        Assert.DoesNotContain(expectedToken, audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(suppliedToken, audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedToken, audit.Reasons, StringComparison.Ordinal);
        Assert.DoesNotContain(suppliedToken, audit.Reasons, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagementCorsDefaultDoesNotAllowCrossOriginWritePreflight()
    {
        await using var factory = CreateFactory(new StubProxyControlClient());
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreatePreflight("https://evil.example", "PATCH"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ManagementCorsAllowsConfiguredOriginPreflight()
    {
        Environment.SetEnvironmentVariable(CorsOriginsEnv, "http://localhost:5173");

        await using var factory = CreateFactory(new StubProxyControlClient());
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(CreatePreflight("http://localhost:5173", "PATCH"));

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("http://localhost:5173", Assert.Single(origins));
    }

    private static HttpRequestMessage CreatePreflight(string origin, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/policy/mode");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        return request;
    }

    private async Task<IReadOnlyList<AuthFailureAuditRow>> ReadAuthFailureAuditRowsAsync()
    {
        var rows = new List<AuthFailureAuditRow>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reasons, duration_ms, payload_json
            FROM audit_events
            WHERE event_type = 'management_auth_failure'
            ORDER BY id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuthFailureAuditRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    private static WebApplicationFactory<ManagementProgram> CreateFactory(IProxyControlClient stub) =>
        new WebApplicationFactory<ManagementProgram>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(stub);
                services.AddSingleton<IProxyControlClient>(stub);
            }));

    private sealed class StubProxyControlClient : IProxyControlClient
    {
        public ProxyControlStatus Status { get; set; } =
            new("audit", "sha256:old", 1, 1);

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
            Status = Status with { Mode = mode };
            return Task.FromResult(Status);
        }

        public Task<ProxyControlStatus> ApplyPolicySnapshotAsync(string yaml, CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<ProxyControlYarpConfigStatus> ApplyYarpConfigAsync(
            ProxyControlYarpConfigRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProxyControlYarpConfigStatus(1, 1, 1));
    }

    private sealed record AuthFailureAuditRow(string Reasons, long DurationMs, string PayloadJson);
}

using Npgsql;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.UnitTests;

public class PostgresAuditSinkTests
{
    [Fact]
    public void ConstructorRejectsBlankConnectionString()
    {
        Assert.Throws<ArgumentException>(() => new PostgresAuditSink(""));
    }

    [Fact]
    public void ConstructorCreatesBatchedSinkWithoutOpeningConnection()
    {
        using var sink = new PostgresAuditSink(
            "Host=localhost;Database=proxyward;Username=proxyward;Password=proxyward");

        Assert.IsAssignableFrom<IBatchedAuditSink>(sink);
    }

    [Fact]
    public void SchemaCreatesAuditEventsWithJsonbPayload()
    {
        var schema = PostgresAuditSchema.SchemaSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS audit_events", schema, StringComparison.Ordinal);
        Assert.Contains("payload_json JSONB NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_audit_events_timestamp", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_audit_events_reasons", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsyncStoresEventWhenPostgresConnectionStringIsProvided()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYWARD_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var correlationId = $"pg-smoke-{Guid.NewGuid():N}";
        await using (var sink = new PostgresAuditSink(connectionString))
        {
            await sink.WriteAsync(CreateAuditEvent(correlationId), CancellationToken.None);
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT decision, payload_json->>'correlationId'
            FROM audit_events
            WHERE correlation_id = @correlation_id;
            """;
        command.Parameters.AddWithValue("correlation_id", correlationId);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("allow", reader.GetString(0));
        Assert.Equal(correlationId, reader.GetString(1));
        Assert.False(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SharedPersistenceStoresUsePostgresWhenConnectionStringIsProvided()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROXYWARD_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var serverId = $"postgres-store-{Guid.NewGuid():N}";
        await using (var policyStore = new PostgresPolicyStore(connectionString))
        {
            var saved = await policyStore.SaveAsync(CreatePolicyYaml(serverId), "test", "postgres smoke");
            var current = await policyStore.ReadCurrentAsync();

            Assert.NotNull(current);
            Assert.Equal(saved.PolicyHash, current!.PolicyHash);
            Assert.Contains(serverId, current.Policy.Servers.Keys);
            Assert.DoesNotContain("sink", current.Yaml, StringComparison.Ordinal);
        }

        await using (var schemaStore = new PostgresTrackedToolSchemaStore(connectionString))
        {
            var recorded = await schemaStore.RecordAsync(
                new ToolSchemaSnapshotInput(
                    serverId,
                    "http://postgres.example/mcp",
                    "2025-06-18",
                    [
                        new ToolSchemaSnapshotEntry(
                            "repos.search",
                            new ToolFingerprint(
                                NameHash: "sha256:name",
                                TitleHash: null,
                                DescriptionHash: "sha256:description",
                                InputSchemaHash: "sha256:input",
                                OutputSchemaHash: null))
                    ],
                    PolicyVersion: "sha256:postgres",
                    SourceCorrelationId: "postgres-store-smoke"),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var latest = await schemaStore.GetLatestAsync(serverId, CancellationToken.None);

            Assert.True(recorded.WasNewVersion);
            Assert.Equal(1, latest?.Version);
            Assert.Equal("repos.search", Assert.Single(latest!.Fingerprints).ToolName);
        }
    }

    private static AuditEvent CreateAuditEvent(string correlationId) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "postgres_smoke",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "postgres",
            Method: "tools/list",
            ToolName: null,
            Reasons: ["smoke"],
            PolicyVersion: "sha256:postgres",
            CorrelationId: correlationId,
            RequestBytes: 0,
            DurationMs: 0,
            ArgumentSummary: null,
            BatchSize: 1);

    private static string CreatePolicyYaml(string serverId) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          enabled: true
        observability:
          serviceName: mcp-proxyward-postgres-smoke
          console:
            enabled: false
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          {{serverId}}:
            route: /postgres/mcp
            upstream: http://postgres.example/mcp
            allowed: true
            tools:
              default: allow
              allow: []
              block: []
        """;
}

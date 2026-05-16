using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;

namespace ProxyWard.UnitTests;

public class SqliteAuditSinkTests : IAsyncLifetime
{
    private readonly string _databasePath = TestSqliteFiles.NewPath("proxyward-audit");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        TestSqliteFiles.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WriteAsyncCreatesSchemaAndStoresEvent()
    {
        using var sink = new SqliteAuditSink(_databasePath);

        var auditEvent = new AuditEvent(
            Timestamp: new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero),
            EventType: "request_inspection",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "github",
            Method: "tools/list",
            ToolName: null,
            Reasons: ["allowed"],
            PolicyVersion: "sha256:abc",
            CorrelationId: "corr-1",
            RequestBytes: 128,
            DurationMs: 5,
            ArgumentSummary: null,
            BatchSize: 1);

        await sink.WriteAsync(auditEvent, CancellationToken.None);

        var rows = QueryAuditEvents(_databasePath);
        var row = Assert.Single(rows);

        Assert.Equal("request_inspection", row.EventType);
        Assert.Equal("audit", row.Mode);
        Assert.Equal("allow", row.Decision);
        Assert.Equal("github", row.ServerId);
        Assert.Equal("tools/list", row.Method);
        Assert.Null(row.ToolName);
        Assert.Equal("allowed", row.Reasons);
        Assert.Equal("sha256:abc", row.PolicyVersion);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Equal(128, row.RequestBytes);
        Assert.Equal(5, row.DurationMs);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal("request_inspection", payload.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("audit", payload.RootElement.GetProperty("mode").GetString());
        Assert.Equal("allow", payload.RootElement.GetProperty("decision").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("batchSize").GetInt32());
        Assert.False(payload.RootElement.GetProperty("argumentOverrideApplied").GetBoolean());
    }

    [Fact]
    public async Task WriteAsyncStoresArgumentSummaryInPayload()
    {
        using var sink = new SqliteAuditSink(_databasePath);

        var summary = new JsonObject
        {
            ["path"] = "[redacted-path]",
            ["limit"] = 10
        };

        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "request_inspection",
            Mode: "enforce",
            Decision: AuditDecision.WouldBlock,
            ServerId: "github",
            Method: "tools/call",
            ToolName: "fs.read",
            Reasons: ["tool_not_allowed", "path_traversal"],
            PolicyVersion: "sha256:def",
            CorrelationId: "corr-2",
            RequestBytes: 256,
            DurationMs: 12,
            ArgumentSummary: summary,
            BatchSize: 1,
            BatchIndex: null,
            ArgumentOverrideApplied: true);

        await sink.WriteAsync(auditEvent, CancellationToken.None);

        var rows = QueryAuditEvents(_databasePath);
        var row = Assert.Single(rows);

        Assert.Equal("would_block", row.Decision);
        Assert.Equal("tool_not_allowed,path_traversal", row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var argumentSummary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal("[redacted-path]", argumentSummary.GetProperty("path").GetString());
        Assert.Equal(10, argumentSummary.GetProperty("limit").GetInt32());
        Assert.True(payload.RootElement.GetProperty("argumentOverrideApplied").GetBoolean());

        var reasons = payload.RootElement.GetProperty("reasons");
        Assert.Equal(2, reasons.GetArrayLength());
        Assert.Equal("tool_not_allowed", reasons[0].GetString());
        Assert.Equal("path_traversal", reasons[1].GetString());
    }

    [Fact]
    public async Task WriteAsyncCreatesIndexesOnQueryableColumns()
    {
        using var sink = new SqliteAuditSink(_databasePath);

        var auditEvent = new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "request_inspection",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "github",
            Method: "tools/list",
            ToolName: null,
            Reasons: [],
            PolicyVersion: "sha256:abc",
            CorrelationId: "corr-3",
            RequestBytes: 0,
            DurationMs: 0,
            ArgumentSummary: null,
            BatchSize: 1);

        await sink.WriteAsync(auditEvent, CancellationToken.None);

        var indexes = QueryIndexNames(_databasePath);
        Assert.Contains("idx_audit_events_timestamp", indexes);
        Assert.Contains("idx_audit_events_decision", indexes);
        Assert.Contains("idx_audit_events_server_id", indexes);
        Assert.Contains("idx_audit_events_method", indexes);
        Assert.Contains("idx_audit_events_tool_name", indexes);
        Assert.Contains("idx_audit_events_reasons", indexes);
    }

    [Fact]
    public async Task WriteAsyncHandlesConcurrentBurstThroughSingleSink()
    {
        using var sink = new SqliteAuditSink(_databasePath);

        var writes = Enumerable.Range(0, 200)
            .Select(index => sink.WriteAsync(CreateAuditEvent(index), CancellationToken.None).AsTask())
            .ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(200, QueryAuditEventCount(_databasePath));
    }

    [Fact]
    public async Task WriteAsyncEnablesWalJournalMode()
    {
        using var sink = new SqliteAuditSink(_databasePath);

        await sink.WriteAsync(CreateAuditEvent(1), CancellationToken.None);

        Assert.Equal("wal", QueryJournalMode(_databasePath));
    }

    private static List<AuditEventRow> QueryAuditEvents(string path)
    {
        var rows = new List<AuditEventRow>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_utc, event_type, mode, decision, server_id, method, tool_name,
                   reasons, policy_version, correlation_id, request_bytes, duration_ms, payload_json
            FROM audit_events
            ORDER BY id ASC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AuditEventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetString(12)));
        }

        return rows;
    }

    private static long QueryAuditEventCount(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_events;";

        return (long)command.ExecuteScalar()!;
    }

    private static string QueryJournalMode(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        return (string)command.ExecuteScalar()!;
    }

    private static List<string> QueryIndexNames(string path)
    {
        var names = new List<string>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='audit_events';";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private sealed record AuditEventRow(
        string TimestampUtc,
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PolicyVersion,
        string CorrelationId,
        long RequestBytes,
        long DurationMs,
        string PayloadJson);

    private static AuditEvent CreateAuditEvent(int index) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            EventType: "request_inspection",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "github",
            Method: "tools/call",
            ToolName: "fs.read",
            Reasons: [],
            PolicyVersion: "sha256:abc",
            CorrelationId: $"corr-{index}",
            RequestBytes: 128,
            DurationMs: 5,
            ArgumentSummary: null,
            BatchSize: 1);
}

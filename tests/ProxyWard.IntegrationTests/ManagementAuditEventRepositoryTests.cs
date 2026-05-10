using System.Text.Json.Nodes;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application.Audit;
using ProxyWard.Management.Infrastructure.Audit;

namespace ProxyWard.IntegrationTests;

public class ManagementAuditEventRepositoryTests
{
    [Fact]
    public async Task QueryAsyncReturnsNewestFirstAndCapsPageSize()
    {
        var dbPath = TempDbPath();
        await SeedAsync(
            dbPath,
            CreateEvent(1, Timestamp.AddMinutes(-2), AuditDecision.Allow),
            CreateEvent(2, Timestamp.AddMinutes(-1), AuditDecision.Warn),
            CreateEvent(3, Timestamp, AuditDecision.Block));
        var repository = new ManagementAuditEventRepository(
            dbPath,
            new ManagementAuditReadOptions(MaxPageSize: 2));

        var page = await repository.QueryAsync(
            new ManagementAuditEventQuery(PageSize: 99),
            CancellationToken.None);

        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new long[] { 3L, 2L }, page.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task QueryAsyncAppliesSupportedFilters()
    {
        var dbPath = TempDbPath();
        await SeedAsync(
            dbPath,
            CreateEvent(1, Timestamp.AddMinutes(-3), AuditDecision.Allow, serverId: "alpha", method: "tools/list", toolName: null, correlationId: "corr-alpha", reasons: ["allowed"]),
            CreateEvent(2, Timestamp.AddMinutes(-2), AuditDecision.Block, serverId: "beta", method: "tools/call", toolName: "fs.read", correlationId: "corr-beta", reasons: ["path_traversal"]),
            CreateEvent(3, Timestamp.AddMinutes(-1), AuditDecision.Warn, serverId: "beta", method: "tools/call", toolName: "net.fetch", correlationId: "corr-gamma", reasons: ["private_network"]));
        var repository = new ManagementAuditEventRepository(dbPath);

        Assert.Equal(new long[] { 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(Decision: "block"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 3L, 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(ServerId: "beta"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 3L, 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(Method: "tools/call"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(ToolName: "fs.read"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 3L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(CorrelationId: "corr-gamma"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 3L, 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(FromUtc: Timestamp.AddMinutes(-2.5), ToUtc: Timestamp),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
        Assert.Equal(new long[] { 2L }, (await repository.QueryAsync(
            new ManagementAuditEventQuery(SearchText: "path_traversal"),
            CancellationToken.None)).Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task QueryAsyncReturnsStoredRedactedArgumentSummaryOnly()
    {
        var dbPath = TempDbPath();
        await SeedAsync(
            dbPath,
            CreateEvent(
                1,
                Timestamp,
                AuditDecision.Warn,
                argumentSummary: JsonNode.Parse("""{"token":"[redacted]","path":"[redacted-path]"}""")));
        var repository = new ManagementAuditEventRepository(dbPath);

        var page = await repository.QueryAsync(new ManagementAuditEventQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.NotNull(item.ArgumentSummary);
        Assert.Equal("[redacted]", item.ArgumentSummary!["token"]!.GetValue<string>());
        Assert.Equal("[redacted-path]", item.ArgumentSummary!["path"]!.GetValue<string>());
        Assert.DoesNotContain("secret-token", item.ArgumentSummary.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedAsync(string dbPath, params AuditEvent[] events)
    {
        using var sink = new SqliteAuditSink(dbPath);
        foreach (var auditEvent in events)
        {
            await sink.WriteAsync(auditEvent, CancellationToken.None);
        }
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-management-audit-{Guid.NewGuid():N}.db");

    private static AuditEvent CreateEvent(
        long index,
        DateTimeOffset timestamp,
        AuditDecision decision,
        string serverId = "sample",
        string? method = "tools/call",
        string? toolName = "fs.read",
        string correlationId = "corr",
        IReadOnlyCollection<string>? reasons = null,
        JsonNode? argumentSummary = null) =>
        new(
            Timestamp: timestamp,
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: decision,
            ServerId: serverId,
            Method: method,
            ToolName: toolName,
            Reasons: reasons ?? [],
            PolicyVersion: $"policy-{index}",
            CorrelationId: correlationId,
            RequestBytes: index,
            DurationMs: index * 10,
            ArgumentSummary: argumentSummary,
            BatchSize: 0);

    private static readonly DateTimeOffset Timestamp = new(2026, 5, 6, 10, 0, 0, TimeSpan.Zero);
}

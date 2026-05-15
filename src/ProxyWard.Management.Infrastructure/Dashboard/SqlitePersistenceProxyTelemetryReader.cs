using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Application.Audit;
using ProxyWard.Management.Application.Dashboard;

namespace ProxyWard.Management.Infrastructure.Dashboard;

public sealed class SqlitePersistenceProxyTelemetryReader : IProxyTelemetryReader
{
    private const string MetadataSource = "persistence-db";

    private readonly string _connectionString;
    private readonly ManagementAuditReadOptions _options;

    public SqlitePersistenceProxyTelemetryReader(string sqlitePath, ManagementAuditReadOptions options)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new ArgumentException("sqlitePath is required.", nameof(sqlitePath));
        }

        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxOverviewSampleSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxOverviewSampleSize must be greater than zero.");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sqlitePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<OverviewResponse> GetOverviewAsync(
        OverviewQuery query,
        CancellationToken cancellationToken)
    {
        var fromIso = FormatIso(query.FromUtc);
        var toIso = FormatIso(query.ToUtc);
        var windowSeconds = (query.ToUtc - query.FromUtc).TotalSeconds;
        var bucketSeconds = query.BucketSeconds;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureBusyTimeoutAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;

        var counts = await ReadAggregateCountsAsync(connection, sqliteTransaction, fromIso, toIso, cancellationToken)
            .ConfigureAwait(false);

        long? latencyP95Ms = null;
        var partial = false;
        string? notes = null;

        if (counts.TotalCount == 0)
        {
            partial = true;
            notes = "no audit events in window";
        }
        else if (counts.TotalCount > _options.MaxOverviewSampleSize)
        {
            partial = true;
            notes = $"latency p95 unavailable: sample exceeded MaxOverviewSampleSize ({_options.MaxOverviewSampleSize})";
        }
        else
        {
            latencyP95Ms = await ReadLatencyP95Async(
                    connection,
                    sqliteTransaction,
                    fromIso,
                    toIso,
                    (int)counts.TotalCount,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (counts.TotalCount > 0 && counts.AsOfUtc.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            var windowFreshnessHorizon = query.ToUtc.AddSeconds(2 * (double)bucketSeconds);
            if (now <= windowFreshnessHorizon)
            {
                var hasOlderRows = await ReadHasOlderRowsAsync(connection, sqliteTransaction, fromIso, cancellationToken)
                    .ConfigureAwait(false);
                var lagThreshold = now.AddSeconds(-2 * (double)bucketSeconds);
                if (hasOlderRows && counts.AsOfUtc.Value < lagThreshold)
                {
                    partial = true;
                    var lagNote = "newest event in window is older than 2 bucket sizes ago (ingest may be lagging)";
                    notes = notes is null ? lagNote : notes + "; " + lagNote;
                }
            }
        }

        var topTools = await ReadTopToolsAsync(
                connection,
                sqliteTransaction,
                fromIso,
                toIso,
                query.TopToolsLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var topReasons = await ReadTopReasonsAsync(
                connection,
                sqliteTransaction,
                fromIso,
                toIso,
                query.TopReasonsLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var series = await ReadSeriesAsync(
                connection,
                sqliteTransaction,
                fromIso,
                toIso,
                query.FromUtc,
                query.ToUtc,
                bucketSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var requestRate = windowSeconds > 0 ? counts.DistinctCorrelations / windowSeconds : 0.0;
        var blockRate = counts.TotalCount > 0 ? (double)counts.BlockCount / counts.TotalCount : 0.0;
        var wouldBlockRate = counts.TotalCount > 0 ? (double)counts.WouldBlockCount / counts.TotalCount : 0.0;
        var errorRate = counts.TotalCount > 0 ? (double)(counts.BlockCount + counts.WouldBlockCount) / counts.TotalCount : 0.0;

        return new OverviewResponse(
            Window: new OverviewWindow(query.FromUtc, query.ToUtc, windowSeconds),
            Bucket: new OverviewBucket(bucketSeconds, series.Count),
            Metadata: new OverviewMetadata(MetadataSource, counts.AsOfUtc, partial, notes),
            RequestRate: requestRate,
            BlockRate: blockRate,
            WouldBlockRate: wouldBlockRate,
            ErrorRate: errorRate,
            LatencyP95Ms: latencyP95Ms,
            TopReasons: topReasons,
            TopTools: topTools,
            Series: series);
    }

    private static async Task<AggregateCounts> ReadAggregateCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        string toIso,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            SELECT
                COUNT(*),
                SUM(CASE WHEN decision = 'block' THEN 1 ELSE 0 END),
                SUM(CASE WHEN decision = 'would_block' THEN 1 ELSE 0 END),
                COUNT(DISTINCT correlation_id),
                MAX(timestamp_utc)
            FROM audit_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to;
            """;
        command.Parameters.AddWithValue("$from", fromIso);
        command.Parameters.AddWithValue("$to", toIso);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AggregateCounts(0, 0, 0, 0, null);
        }

        var total = reader.GetInt64(0);
        var blocks = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        var wouldBlocks = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
        var distinctCorr = reader.GetInt64(3);
        DateTimeOffset? asOf = reader.IsDBNull(4)
            ? null
            : TryParseTimestamp(reader.GetString(4));

        return new AggregateCounts(total, blocks, wouldBlocks, distinctCorr, asOf);
    }

    private static async Task<bool> ReadHasOlderRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM audit_events WHERE timestamp_utc < $from);";
        command.Parameters.AddWithValue("$from", fromIso);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<long?> ReadLatencyP95Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        string toIso,
        int totalCount,
        CancellationToken cancellationToken)
    {
        if (totalCount <= 0)
        {
            return null;
        }

        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            SELECT duration_ms
            FROM audit_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY duration_ms ASC;
            """;
        command.Parameters.AddWithValue("$from", fromIso);
        command.Parameters.AddWithValue("$to", toIso);

        var durations = new List<long>(totalCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            durations.Add(reader.GetInt64(0));
        }

        if (durations.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(0.95 * durations.Count) - 1;
        if (index < 0)
        {
            index = 0;
        }
        if (index >= durations.Count)
        {
            index = durations.Count - 1;
        }

        return durations[index];
    }

    private static async Task<IReadOnlyList<OverviewTopRow>> ReadTopToolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        string toIso,
        int topN,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            SELECT tool_name, COUNT(*) AS c
            FROM audit_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to AND tool_name IS NOT NULL
            GROUP BY tool_name
            ORDER BY c DESC, tool_name ASC
            LIMIT $top;
            """;
        command.Parameters.AddWithValue("$from", fromIso);
        command.Parameters.AddWithValue("$to", toIso);
        command.Parameters.AddWithValue("$top", topN);

        var rows = new List<OverviewTopRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            rows.Add(new OverviewTopRow(reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<OverviewTopRow>> ReadTopReasonsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        string toIso,
        int topN,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            SELECT reasons
            FROM audit_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to AND decision != 'allow';
            """;
        command.Parameters.AddWithValue("$from", fromIso);
        command.Parameters.AddWithValue("$to", toIso);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var raw = reader.GetString(0);
            foreach (var reason in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                counts[reason] = counts.TryGetValue(reason, out var existing) ? existing + 1 : 1;
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(kv => new OverviewTopRow(kv.Key, kv.Value))
            .ToList();
    }

    private static async Task<IReadOnlyList<OverviewSeriesPoint>> ReadSeriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fromIso,
        string toIso,
        DateTimeOffset windowFromUtc,
        DateTimeOffset windowToUtc,
        int bucketSeconds,
        CancellationToken cancellationToken)
    {
        var windowSeconds = (windowToUtc - windowFromUtc).TotalSeconds;
        var bucketCount = (int)Math.Ceiling(windowSeconds / bucketSeconds);
        if (bucketCount < 1)
        {
            bucketCount = 1;
        }

        var allow = new int[bucketCount];
        var block = new int[bucketCount];
        var wouldBlock = new int[bucketCount];
        var warn = new int[bucketCount];

        await using var command = CreateCommand(connection, transaction);
        command.CommandText = """
            SELECT timestamp_utc, decision
            FROM audit_events
            WHERE timestamp_utc >= $from AND timestamp_utc <= $to
            ORDER BY timestamp_utc ASC;
            """;
        command.Parameters.AddWithValue("$from", fromIso);
        command.Parameters.AddWithValue("$to", toIso);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || TryParseTimestamp(reader.GetString(0)) is not { } ts)
            {
                continue;
            }

            var decision = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

            var offsetSeconds = (ts - windowFromUtc).TotalSeconds;
            var bucketIndex = (int)(offsetSeconds / bucketSeconds);
            if (bucketIndex < 0)
            {
                bucketIndex = 0;
            }
            if (bucketIndex >= bucketCount)
            {
                bucketIndex = bucketCount - 1;
            }

            switch (decision)
            {
                case "allow":
                    allow[bucketIndex]++;
                    break;
                case "block":
                    block[bucketIndex]++;
                    break;
                case "would_block":
                    wouldBlock[bucketIndex]++;
                    break;
                case "warn":
                    warn[bucketIndex]++;
                    break;
            }
        }

        var points = new List<OverviewSeriesPoint>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var bucketStart = windowFromUtc.AddSeconds(i * (double)bucketSeconds);
            var total = allow[i] + block[i] + wouldBlock[i] + warn[i];
            points.Add(new OverviewSeriesPoint(
                BucketStartUtc: bucketStart,
                Allow: allow[i],
                Block: block[i],
                WouldBlock: wouldBlock[i],
                Warn: warn[i],
                Total: total));
        }

        return points;
    }

    private static async Task ConfigureBusyTimeoutAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    private static DateTimeOffset? TryParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : null;

    private static string FormatIso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private sealed record AggregateCounts(
        long TotalCount,
        long BlockCount,
        long WouldBlockCount,
        long DistinctCorrelations,
        DateTimeOffset? AsOfUtc);
}

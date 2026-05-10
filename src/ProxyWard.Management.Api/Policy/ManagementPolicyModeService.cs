using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Api.Status;

namespace ProxyWard.Management.Api.Policy;

public sealed class ManagementPolicyModeService
{
    private static readonly TimeSpan DefaultImpactWindow = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly IProxyControlClient _proxyControlClient;

    public ManagementPolicyModeService(
        ManagementApiOptions options,
        IProxyControlClient proxyControlClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        _proxyControlClient = proxyControlClient ?? throw new ArgumentNullException(nameof(proxyControlClient));

        _databasePath = Path.GetFullPath(options.AuditDatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
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

    private async Task<ManagementPolicyModeImpactResponse> BuildImpactAsync(
        string targetMode,
        ProxyControlStatus currentStatus,
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken)
    {
        var affected = await ReadAffectedImpactAsync(window, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<ManagementPolicyModeImpactItem>> ReadAffectedImpactAsync(
        ManagementPolicyImpactWindow window,
        CancellationToken cancellationToken)
    {
        var affected = new Dictionary<ImpactKey, ImpactAccumulator>();
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        var readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        await using var connection = new SqliteConnection(readConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureReadConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (await TableExistsAsync(connection, "audit_events", cancellationToken).ConfigureAwait(false))
        {
            await ReadWouldBlockImpactAsync(connection, window, affected, cancellationToken).ConfigureAwait(false);
        }

        if (await TableExistsAsync(connection, "schema_drift_reviews", cancellationToken).ConfigureAwait(false))
        {
            await ReadDriftImpactAsync(connection, window, affected, cancellationToken).ConfigureAwait(false);
        }

        return affected.Values
            .Select(item => item.ToItem())
            .OrderBy(item => item.ServerId, StringComparer.Ordinal)
            .ThenBy(item => item.ToolName ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task ReadWouldBlockImpactAsync(
        SqliteConnection connection,
        ManagementPolicyImpactWindow window,
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_id, tool_name, COUNT(*), GROUP_CONCAT(reasons, ',')
            FROM audit_events
            WHERE decision = 'would_block'
              AND timestamp_utc >= $from_utc
              AND timestamp_utc <= $to_utc
            GROUP BY server_id, tool_name;
            """;
        AddWindowParameters(command, window);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new ImpactKey(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            var accumulator = GetOrAdd(affected, key);
            accumulator.WouldBlockCount += reader.GetInt64(2);
            accumulator.AddReasons(reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    private static async Task ReadDriftImpactAsync(
        SqliteConnection connection,
        ManagementPolicyImpactWindow window,
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                server_id,
                tool_name,
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END) AS pending_count,
                COUNT(*) AS unapproved_count,
                GROUP_CONCAT(reasons, ',')
            FROM schema_drift_reviews
            WHERE status IN ('pending', 'rejected', 'blocked')
              AND detected_at_utc >= $from_utc
              AND detected_at_utc <= $to_utc
            GROUP BY server_id, tool_name;
            """;
        AddWindowParameters(command, window);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new ImpactKey(reader.GetString(0), reader.GetString(1));
            var accumulator = GetOrAdd(affected, key);
            accumulator.PendingDriftCount += reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            accumulator.UnapprovedDriftCount += reader.GetInt64(3);
            accumulator.AddReasons(reader.IsDBNull(4) ? null : reader.GetString(4));
        }
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

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureWriteConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureAuditSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (
                timestamp_utc,
                event_type,
                mode,
                decision,
                server_id,
                method,
                tool_name,
                reasons,
                policy_version,
                correlation_id,
                request_bytes,
                duration_ms,
                payload_json
            ) VALUES (
                $timestamp_utc,
                $event_type,
                $mode,
                $decision,
                $server_id,
                $method,
                $tool_name,
                $reasons,
                $policy_version,
                $correlation_id,
                $request_bytes,
                $duration_ms,
                $payload_json
            );
            """;
        command.Parameters.AddWithValue("$timestamp_utc", timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$event_type", "policy_mode_switch");
        command.Parameters.AddWithValue("$mode", "management");
        command.Parameters.AddWithValue("$decision", "allow");
        command.Parameters.AddWithValue("$server_id", "management");
        command.Parameters.AddWithValue("$method", "policy/mode");
        command.Parameters.AddWithValue("$tool_name", DBNull.Value);
        command.Parameters.AddWithValue("$reasons", reason);
        command.Parameters.AddWithValue("$policy_version", appliedStatus.PolicyVersion);
        command.Parameters.AddWithValue("$correlation_id", correlationId);
        command.Parameters.AddWithValue("$request_bytes", 0);
        command.Parameters.AddWithValue("$duration_ms", 0);
        command.Parameters.AddWithValue("$payload_json", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static ImpactAccumulator GetOrAdd(
        Dictionary<ImpactKey, ImpactAccumulator> affected,
        ImpactKey key)
    {
        if (!affected.TryGetValue(key, out var accumulator))
        {
            accumulator = new ImpactAccumulator(key.ServerId, key.ToolName);
            affected[key] = accumulator;
        }

        return accumulator;
    }

    private static async Task ConfigureReadConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConfigureWriteConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task EnsureAuditSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                mode TEXT NOT NULL,
                decision TEXT NOT NULL,
                server_id TEXT NOT NULL,
                method TEXT NULL,
                tool_name TEXT NULL,
                reasons TEXT NOT NULL,
                policy_version TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                request_bytes INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_events_timestamp ON audit_events(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_audit_events_decision ON audit_events(decision);
            CREATE INDEX IF NOT EXISTS idx_audit_events_server_id ON audit_events(server_id);
            CREATE INDEX IF NOT EXISTS idx_audit_events_method ON audit_events(method);
            CREATE INDEX IF NOT EXISTS idx_audit_events_tool_name ON audit_events(tool_name);
            CREATE INDEX IF NOT EXISTS idx_audit_events_reasons ON audit_events(reasons);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddWindowParameters(SqliteCommand command, ManagementPolicyImpactWindow window)
    {
        command.Parameters.AddWithValue("$from_utc", window.FromUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to_utc", window.ToUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
    }

    private sealed record ImpactKey(string ServerId, string? ToolName);

    private sealed class ImpactAccumulator
    {
        private readonly SortedSet<string> _reasons = new(StringComparer.Ordinal);

        public ImpactAccumulator(string serverId, string? toolName)
        {
            ServerId = serverId;
            ToolName = toolName;
        }

        public string ServerId { get; }

        public string? ToolName { get; }

        public long WouldBlockCount { get; set; }

        public long PendingDriftCount { get; set; }

        public long UnapprovedDriftCount { get; set; }

        public void AddReasons(string? reasons)
        {
            if (string.IsNullOrWhiteSpace(reasons))
            {
                return;
            }

            foreach (var reason in reasons.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _reasons.Add(reason);
            }
        }

        public ManagementPolicyModeImpactItem ToItem() =>
            new(
                ServerId,
                ToolName,
                WouldBlockCount,
                PendingDriftCount,
                UnapprovedDriftCount,
                _reasons.ToArray());
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

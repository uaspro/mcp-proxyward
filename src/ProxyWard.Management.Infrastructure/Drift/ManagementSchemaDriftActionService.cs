using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ProxyWard.Management.Application.Drift;

namespace ProxyWard.Management.Infrastructure.Drift;

public sealed class ManagementSchemaDriftActionService : IManagementSchemaDriftActionService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _connectionString;
    private readonly ManagementSchemaDriftRepository _repository;

    public ManagementSchemaDriftActionService(
        string sqlitePath,
        ManagementSchemaDriftRepository repository)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new ArgumentException("sqlitePath is required.", nameof(sqlitePath));
        }

        _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        var fullPath = Path.GetFullPath(sqlitePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<ManagementSchemaDriftDetail?> ApplyAsync(
        long id,
        string action,
        ManagementSchemaDriftActionRequest? request,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return null;
        }

        var normalizedAction = action.Trim();
        var newStatus = normalizedAction switch
        {
            "approve" => "approved",
            "reject" => "rejected",
            "block" => "blocked",
            _ => throw new ArgumentException("Unsupported drift review action.", nameof(action))
        };

        var reviewedBy = NormalizeText(request?.ReviewedBy) ?? "admin";
        var reviewNote = NormalizeText(request?.ReviewNote);
        var reviewedAtUtc = DateTimeOffset.UtcNow;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureAuditSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, transaction, "schema_drift_reviews", cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var existing = await LoadReviewAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await UpdateReviewAsync(
            connection,
            transaction,
            id,
            newStatus,
            reviewedAtUtc,
            reviewedBy,
            reviewNote,
            cancellationToken).ConfigureAwait(false);
        await InsertAuditEventAsync(
            connection,
            transaction,
            existing,
            normalizedAction,
            newStatus,
            reviewedAtUtc,
            reviewedBy,
            reviewNote,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await _repository.GetByIdAsync(id, null, null, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
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

    private static async Task<ReviewRow?> LoadReviewAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT id, server_id, tool_name, field_name, from_version, to_version,
                   status, reasons, policy_version
            FROM schema_drift_reviews
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReviewRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task UpdateReviewAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        long id,
        string status,
        DateTimeOffset reviewedAtUtc,
        string reviewedBy,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE schema_drift_reviews
            SET status = $status,
                reviewed_at_utc = $reviewed_at_utc,
                reviewed_by = $reviewed_by,
                review_note = $review_note
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reviewed_at_utc", reviewedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reviewed_by", reviewedBy);
        command.Parameters.AddWithValue("$review_note", (object?)reviewNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditEventAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ReviewRow review,
        string action,
        string newStatus,
        DateTimeOffset timestamp,
        string reviewedBy,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        var reason = $"schema_drift_review_{newStatus}";
        var correlationId = $"mgmt-{Guid.NewGuid():N}";
        var payloadJson = CreatePayloadJson(
            review,
            action,
            newStatus,
            timestamp,
            reviewedBy,
            reviewNote,
            reason,
            correlationId);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
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
        command.Parameters.AddWithValue("$event_type", "schema_drift_review_action");
        command.Parameters.AddWithValue("$mode", "management");
        command.Parameters.AddWithValue("$decision", "allow");
        command.Parameters.AddWithValue("$server_id", review.ServerId);
        command.Parameters.AddWithValue("$method", $"schema/drifts/{action}");
        command.Parameters.AddWithValue("$tool_name", review.ToolName);
        command.Parameters.AddWithValue("$reasons", reason);
        command.Parameters.AddWithValue("$policy_version", review.PolicyVersion ?? "unknown");
        command.Parameters.AddWithValue("$correlation_id", correlationId);
        command.Parameters.AddWithValue("$request_bytes", 0);
        command.Parameters.AddWithValue("$duration_ms", 0);
        command.Parameters.AddWithValue("$payload_json", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreatePayloadJson(
        ReviewRow review,
        string action,
        string newStatus,
        DateTimeOffset timestamp,
        string reviewedBy,
        string? reviewNote,
        string reason,
        string correlationId)
    {
        var argumentSummary = new JsonObject
        {
            ["action"] = action,
            ["reviewId"] = review.Id,
            ["serverId"] = review.ServerId,
            ["toolName"] = review.ToolName,
            ["fieldName"] = review.FieldName,
            ["fromVersion"] = review.FromVersion,
            ["toVersion"] = review.ToVersion,
            ["previousStatus"] = review.Status,
            ["status"] = newStatus,
            ["reviewedBy"] = reviewedBy,
            ["reviewNote"] = reviewNote
        };

        var payload = new JsonObject
        {
            ["timestamp"] = timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["eventType"] = "schema_drift_review_action",
            ["mode"] = "management",
            ["decision"] = "allow",
            ["serverId"] = review.ServerId,
            ["method"] = $"schema/drifts/{action}",
            ["toolName"] = review.ToolName,
            ["reasons"] = new JsonArray(JsonValue.Create(reason)),
            ["policyVersion"] = review.PolicyVersion ?? "unknown",
            ["correlationId"] = correlationId,
            ["requestBytes"] = 0,
            ["durationMs"] = 0,
            ["batchSize"] = 0,
            ["batchIndex"] = null,
            ["argumentOverrideApplied"] = false,
            ["argumentSummary"] = argumentSummary
        };

        return payload.ToJsonString(PayloadJsonOptions);
    }

    private sealed record ReviewRow(
        long Id,
        string ServerId,
        string ToolName,
        string FieldName,
        int FromVersion,
        int ToVersion,
        string Status,
        string Reasons,
        string? PolicyVersion);
}

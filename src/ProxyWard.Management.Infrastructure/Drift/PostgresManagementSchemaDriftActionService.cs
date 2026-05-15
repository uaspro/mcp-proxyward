using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using ProxyWard.Audit.Sinks;
using ProxyWard.Management.Application.Drift;

namespace ProxyWard.Management.Infrastructure.Drift;

public sealed class PostgresManagementSchemaDriftActionService : IManagementSchemaDriftActionService, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresManagementSchemaDriftRepository _repository;
    private readonly bool _ownsDataSource;

    public PostgresManagementSchemaDriftActionService(
        string connectionString,
        PostgresManagementSchemaDriftRepository repository)
        : this(CreateDataSource(connectionString), repository, ownsDataSource: true)
    {
    }

    public PostgresManagementSchemaDriftActionService(
        NpgsqlDataSource dataSource,
        PostgresManagementSchemaDriftRepository repository)
        : this(dataSource, repository, ownsDataSource: false)
    {
    }

    private PostgresManagementSchemaDriftActionService(
        NpgsqlDataSource dataSource,
        PostgresManagementSchemaDriftRepository repository,
        bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _ownsDataSource = ownsDataSource;
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

        var stopwatch = Stopwatch.StartNew();
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresAuditSchema.EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

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
            stopwatch.ElapsedMilliseconds,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await _repository.GetByIdAsync(id, null, null, cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT to_regclass(@table_name) IS NOT NULL;";
        command.Parameters.AddWithValue("table_name", tableName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<ReviewRow?> LoadReviewAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, server_id, tool_name, field_name, from_version, to_version,
                   status, reasons, policy_version
            FROM schema_drift_reviews
            WHERE id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("id", id);

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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long id,
        string status,
        DateTimeOffset reviewedAtUtc,
        string reviewedBy,
        string? reviewNote,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE schema_drift_reviews
            SET status = @status,
                reviewed_at_utc = @reviewed_at_utc,
                reviewed_by = @reviewed_by,
                review_note = @review_note
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("reviewed_at_utc", reviewedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("reviewed_by", reviewedBy);
        command.Parameters.AddWithValue("review_note", (object?)reviewNote ?? DBNull.Value);
        command.Parameters.AddWithValue("id", id);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReviewRow review,
        string action,
        string newStatus,
        DateTimeOffset timestamp,
        string reviewedBy,
        string? reviewNote,
        long durationMs,
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
            correlationId,
            durationMs);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                @timestamp_utc,
                @event_type,
                @mode,
                @decision,
                @server_id,
                @method,
                @tool_name,
                @reasons,
                @policy_version,
                @correlation_id,
                @request_bytes,
                @duration_ms,
                CAST(@payload_json AS jsonb)
            );
            """;
        command.Parameters.AddWithValue("timestamp_utc", timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("event_type", "schema_drift_review_action");
        command.Parameters.AddWithValue("mode", "management");
        command.Parameters.AddWithValue("decision", "allow");
        command.Parameters.AddWithValue("server_id", review.ServerId);
        command.Parameters.AddWithValue("method", $"schema/drifts/{action}");
        command.Parameters.AddWithValue("tool_name", review.ToolName);
        command.Parameters.AddWithValue("reasons", reason);
        command.Parameters.AddWithValue("policy_version", review.PolicyVersion ?? "unknown");
        command.Parameters.AddWithValue("correlation_id", correlationId);
        command.Parameters.AddWithValue("request_bytes", 0L);
        command.Parameters.AddWithValue("duration_ms", durationMs);
        command.Parameters.AddWithValue("payload_json", payloadJson);

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
        string correlationId,
        long durationMs)
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
            ["durationMs"] = durationMs,
            ["batchSize"] = 0,
            ["batchIndex"] = null,
            ["argumentOverrideApplied"] = false,
            ["argumentSummary"] = argumentSummary
        };

        return payload.ToJsonString(PayloadJsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }

        return NpgsqlDataSource.Create(connectionString);
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

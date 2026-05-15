using System.Globalization;
using Microsoft.Data.Sqlite;
using ProxyWard.Core.Persistence;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Persistence;

public sealed class SqlitePolicyStore : IPolicyStore, IPersistenceSchemaInitializer
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqlitePolicyStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Policy database path is required.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public string SourceDescription => $"sqlite:{_databasePath}#policy_snapshots";

    public async Task<StoredPolicySnapshot> InitializeAndReadCurrentAsync(
        string defaultYaml,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            return current;
        }

        return await SaveAsync(
            defaultYaml,
            requestedBy: "system",
            note: "default policy bootstrap",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredPolicySnapshot?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at_utc, policy_hash, yaml, requested_by, note
            FROM policy_snapshots
            ORDER BY id DESC
            LIMIT 1;
            """;

        PolicySnapshotReadResult? result;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            result = ReadSnapshot(reader);
        }

        if (result.RequiresNormalization)
        {
            await UpdateNormalizedSnapshotAsync(connection, result.Snapshot, cancellationToken).ConfigureAwait(false);
        }

        return result.Snapshot;
    }

    public async Task<StoredPolicySnapshot> SaveAsync(
        string yaml,
        string? requestedBy = null,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var policy = ProxyWardPolicyLoader.Load(yaml);
        var normalizedYaml = ProxyWardPolicySerializer.ToYaml(policy);
        var timestamp = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO policy_snapshots (
                created_at_utc,
                policy_hash,
                yaml,
                requested_by,
                note
            ) VALUES (
                $created_at_utc,
                $policy_hash,
                $yaml,
                $requested_by,
                $note
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$created_at_utc", timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$policy_hash", policy.VersionHash);
        command.Parameters.AddWithValue("$yaml", normalizedYaml);
        command.Parameters.AddWithValue("$requested_by", NormalizeText(requestedBy) is { } actor ? actor : DBNull.Value);
        command.Parameters.AddWithValue("$note", NormalizeText(note) is { } normalizedNote ? normalizedNote : DBNull.Value);

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Policy snapshot insert did not return an id."));

        return new StoredPolicySnapshot(
            Id: id,
            CreatedAtUtc: timestamp,
            PolicyHash: policy.VersionHash,
            Yaml: normalizedYaml,
            Policy: policy,
            RequestedBy: NormalizeText(requestedBy),
            Note: NormalizeText(note));
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(
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

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS policy_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at_utc TEXT NOT NULL,
                policy_hash TEXT NOT NULL,
                yaml TEXT NOT NULL,
                requested_by TEXT NULL,
                note TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_policy_snapshots_created_at
              ON policy_snapshots(created_at_utc);
            CREATE INDEX IF NOT EXISTS idx_policy_snapshots_hash
              ON policy_snapshots(policy_hash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PolicySnapshotReadResult ReadSnapshot(SqliteDataReader reader)
    {
        var policyHash = reader.GetString(2);
        var yaml = reader.GetString(3);
        var policy = ProxyWardPolicyLoader.LoadStoredSnapshot(yaml);
        var normalizedYaml = ProxyWardPolicySerializer.ToYaml(policy);
        var snapshot = new StoredPolicySnapshot(
            Id: reader.GetInt64(0),
            CreatedAtUtc: DateTimeOffset.Parse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            PolicyHash: policy.VersionHash,
            Yaml: normalizedYaml,
            Policy: policy,
            RequestedBy: reader.IsDBNull(4) ? null : reader.GetString(4),
            Note: reader.IsDBNull(5) ? null : reader.GetString(5));
        return new PolicySnapshotReadResult(snapshot, policyHash, yaml);
    }

    private static async Task UpdateNormalizedSnapshotAsync(
        SqliteConnection connection,
        StoredPolicySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE policy_snapshots
            SET policy_hash = $policy_hash,
                yaml = $yaml
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$policy_hash", snapshot.PolicyHash);
        command.Parameters.AddWithValue("$yaml", snapshot.Yaml);
        command.Parameters.AddWithValue("$id", snapshot.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private sealed record PolicySnapshotReadResult(
        StoredPolicySnapshot Snapshot,
        string StoredPolicyHash,
        string StoredYaml)
    {
        public bool RequiresNormalization =>
            !string.Equals(StoredPolicyHash, Snapshot.PolicyHash, StringComparison.Ordinal)
            || !string.Equals(StoredYaml, Snapshot.Yaml, StringComparison.Ordinal);
    }
}

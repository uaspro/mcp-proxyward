namespace ProxyWard.Core.Persistence;

public enum PersistenceDatabaseProvider
{
    Sqlite,
    PostgreSql
}

public sealed record PersistenceDatabaseOptions(
    PersistenceDatabaseProvider Provider,
    string? SqlitePath,
    string? PostgresConnectionString)
{
    public static PersistenceDatabaseOptions ForSqlite(string sqlitePath)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new ArgumentException("sqlitePath is required.", nameof(sqlitePath));
        }

        return new PersistenceDatabaseOptions(
            PersistenceDatabaseProvider.Sqlite,
            sqlitePath,
            PostgresConnectionString: null);
    }

    public static PersistenceDatabaseOptions ForPostgreSql(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("connectionString is required.", nameof(connectionString));
        }

        return new PersistenceDatabaseOptions(
            PersistenceDatabaseProvider.PostgreSql,
            SqlitePath: null,
            connectionString);
    }

    public string ProviderName =>
        Provider == PersistenceDatabaseProvider.PostgreSql ? "postgres" : "sqlite";

    public string SourceDescription =>
        Provider switch
        {
            PersistenceDatabaseProvider.PostgreSql => "postgresql:#proxyward",
            _ => $"sqlite:{Path.GetFullPath(SqlitePath!)}"
        };
}

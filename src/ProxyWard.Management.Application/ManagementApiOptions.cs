using System.Text;
using ProxyWard.Core.Persistence;

namespace ProxyWard.Management.Application;

public sealed record ManagementApiOptions(
    string SqliteDatabasePath,
    Uri ProxyControlBaseUrl,
    string? ProxyControlToken,
    string? AdminToken,
    bool LocalDevelopmentMode,
    IReadOnlyList<string> CorsAllowedOrigins,
    PersistenceDatabaseOptions? PersistenceDatabase = null)
{
    public PersistenceDatabaseOptions EffectivePersistenceDatabase =>
        PersistenceDatabase ?? PersistenceDatabaseOptions.ForSqlite(SqliteDatabasePath);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("PersistenceProvider = ").Append(EffectivePersistenceDatabase.ProviderName);
        builder.Append(", SqliteDatabasePath = ").Append(SqliteDatabasePath);
        builder.Append(", ProxyControlBaseUrl = ").Append(ProxyControlBaseUrl);
        builder.Append(", ProxyControlToken = ").Append(ProxyControlToken is null ? "null" : "***");
        builder.Append(", AdminToken = ").Append(AdminToken is null ? "null" : "***");
        builder.Append(", LocalDevelopmentMode = ").Append(LocalDevelopmentMode);
        builder.Append(", CorsAllowedOrigins = ").Append(CorsAllowedOrigins.Count);
        return true;
    }
}

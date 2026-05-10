using System.Text;

namespace ProxyWard.Management.Api;

public sealed record ManagementApiOptions(
    string AuditDatabasePath,
    string PolicyPath,
    Uri ProxyControlBaseUrl,
    string? ProxyControlToken,
    string? AdminToken,
    bool LocalDevelopmentMode,
    IReadOnlyList<string> CorsAllowedOrigins)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AuditDatabasePath = ").Append(AuditDatabasePath);
        builder.Append(", PolicyPath = ").Append(PolicyPath);
        builder.Append(", ProxyControlBaseUrl = ").Append(ProxyControlBaseUrl);
        builder.Append(", ProxyControlToken = ").Append(ProxyControlToken is null ? "null" : "***");
        builder.Append(", AdminToken = ").Append(AdminToken is null ? "null" : "***");
        builder.Append(", LocalDevelopmentMode = ").Append(LocalDevelopmentMode);
        builder.Append(", CorsAllowedOrigins = ").Append(CorsAllowedOrigins.Count);
        return true;
    }
}

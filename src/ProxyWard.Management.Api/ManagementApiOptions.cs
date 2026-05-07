namespace ProxyWard.Management.Api;

public sealed record ManagementApiOptions(
    string AuditDatabasePath,
    Uri ProxyControlBaseUrl);


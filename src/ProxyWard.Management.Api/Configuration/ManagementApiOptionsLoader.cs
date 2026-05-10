using System.Globalization;
using ProxyWard.Management.Application;
using ProxyWard.Management.Application.Audit;

namespace ProxyWard.Management.Api.Configuration;

internal static class ManagementApiOptionsLoader
{
    public static ManagementApiOptions LoadOptions(IConfiguration configuration)
    {
        var auditDatabasePath = Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_AUDIT_DB_PATH")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_DB_PATH")
            ?? configuration["Management:Audit:SqlitePath"]
            ?? "./data/proxyward.db";

        var proxyControlBaseUrlValue = Environment.GetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_URL")
            ?? configuration["Management:ProxyControl:BaseUrl"]
            ?? "http://localhost:8080";

        if (!Uri.TryCreate(proxyControlBaseUrlValue, UriKind.Absolute, out var proxyControlBaseUrl)
            || (proxyControlBaseUrl.Scheme != Uri.UriSchemeHttp && proxyControlBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "Management:ProxyControl:BaseUrl or PROXYWARD_PROXY_CONTROL_URL must be an absolute http or https URL.");
        }

        var proxyControlTokenValue = Environment.GetEnvironmentVariable("PROXYWARD_PROXY_CONTROL_TOKEN")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN")
            ?? configuration["Management:ProxyControl:Token"];
        var proxyControlToken = string.IsNullOrWhiteSpace(proxyControlTokenValue) ? null : proxyControlTokenValue;

        var adminTokenValue = Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_ADMIN_TOKEN")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN")
            ?? configuration["Management:AdminToken"];
        var adminToken = string.IsNullOrWhiteSpace(adminTokenValue) ? null : adminTokenValue;

        var localDevelopmentMode = ReadBool(
            configuration,
            "PROXYWARD_MANAGEMENT_LOCAL_DEV",
            "Management:LocalDevMode",
            defaultValue: false);

        var corsAllowedOrigins = ReadStringList(
            Environment.GetEnvironmentVariable("PROXYWARD_MANAGEMENT_CORS_ALLOWED_ORIGINS")
                ?? configuration["Management:Cors:AllowedOrigins"]);

        return new ManagementApiOptions(
            auditDatabasePath,
            proxyControlBaseUrl,
            proxyControlToken,
            adminToken,
            localDevelopmentMode,
            corsAllowedOrigins);
    }

    public static ManagementAuditReadOptions LoadAuditReadOptions(IConfiguration configuration)
    {
        var defaults = new ManagementAuditReadOptions();

        var maxExportRowCount = ReadPositiveInt(
            configuration,
            "PROXYWARD_MANAGEMENT_AUDIT_MAX_EXPORT_ROWS",
            "Management:Audit:MaxExportRows",
            defaults.MaxExportRowCount);

        var maxOverviewSampleSize = ReadPositiveInt(
            configuration,
            "PROXYWARD_MANAGEMENT_OVERVIEW_MAX_SAMPLE_SIZE",
            "Management:Overview:MaxSampleSize",
            defaults.MaxOverviewSampleSize);

        return defaults with
        {
            MaxExportRowCount = maxExportRowCount,
            MaxOverviewSampleSize = maxOverviewSampleSize
        };
    }

    private static int ReadPositiveInt(
        IConfiguration configuration,
        string envVarName,
        string configKey,
        int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName) ?? configuration[configKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidOperationException(
                $"{configKey} or {envVarName} must be a positive integer.");
        }

        return parsed;
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string envVarName,
        string configKey,
        bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName) ?? configuration[configKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            throw new InvalidOperationException($"{configKey} or {envVarName} must be true or false.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> ReadStringList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(origin => origin.TrimEnd('/'))
                .Where(origin => origin.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

namespace ProxyWard.Api.Control;

public sealed record ProxyWardControlOptions(
    bool Enabled,
    string? Token)
{
    public static ProxyWardControlOptions Load(IConfiguration configuration)
    {
        var enabledValue = Environment.GetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED")
            ?? configuration["ProxyWard:Control:Enabled"];

        var enabled = TryParseBoolean(enabledValue);
        var token = Environment.GetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN")
            ?? Environment.GetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN")
            ?? configuration["ProxyWard:Control:Token"];

        if (enabled && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Runtime control is enabled, but no control token was configured. Set PROXYWARD_CONTROL_TOKEN, PROXYWARD_ADMIN_TOKEN, or ProxyWard:Control:Token.");
        }

        return new ProxyWardControlOptions(enabled, token);
    }

    private static bool TryParseBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return bool.TryParse(value, out var parsed) && parsed;
    }
}

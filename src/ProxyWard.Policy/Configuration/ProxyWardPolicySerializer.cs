using YamlDotNet.Serialization;

namespace ProxyWard.Policy.Configuration;

public static class ProxyWardPolicySerializer
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .Build();

    public static string ToYaml(ProxyWardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return YamlSerializer.Serialize(ToRaw(policy));
    }

    private static SortedDictionary<string, object?> ToRaw(ProxyWardPolicy policy) =>
        new(StringComparer.Ordinal)
        {
            ["audit"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sink"] = policy.Audit.Sink,
                ["sqlitePath"] = policy.Audit.SqlitePath
            },
            ["inspection"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchToolCalls"] = "failClosed",
                ["maxBodyBytes"] = policy.Inspection.MaxBodyBytes,
                ["unsupportedStreaming"] = FormatUnsupportedBehavior(policy.Inspection.UnsupportedStreaming)
            },
            ["mode"] = FormatMode(policy.Mode),
            ["observability"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applicationInsights"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["connectionStringEnv"] = policy.Observability.ApplicationInsights.ConnectionStringEnv,
                    ["enabled"] = policy.Observability.ApplicationInsights.Enabled
                },
                ["console"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["enabled"] = policy.Observability.Console.Enabled
                },
                ["otlp"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["enabled"] = policy.Observability.Otlp.Enabled,
                    ["endpoint"] = policy.Observability.Otlp.Endpoint
                },
                ["sampling"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tracesRatio"] = policy.Observability.Sampling.TracesRatio
                },
                ["serviceName"] = policy.Observability.ServiceName
            },
            ["servers"] = policy.Servers.ToDictionary(
                pair => pair.Key,
                pair => (object?)ToRaw(pair.Value),
                StringComparer.Ordinal)
        };

    private static SortedDictionary<string, object?> ToRaw(ServerPolicy server) =>
        new(StringComparer.Ordinal)
        {
            ["allowed"] = server.Allowed,
            ["arguments"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["commands"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["blockShell"] = server.Arguments.Commands.BlockShell,
                    ["dangerous"] = server.Arguments.Commands.Dangerous
                },
                ["hosts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allow"] = server.Arguments.Hosts.Allow,
                    ["blockPrivateNetworks"] = server.Arguments.Hosts.BlockPrivateNetworks
                },
                ["paths"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowedRoots"] = server.Arguments.Paths.AllowedRoots,
                    ["blockTraversal"] = server.Arguments.Paths.BlockTraversal
                },
                ["overrides"] = server.Arguments.Overrides.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)ToRaw(pair.Value),
                    StringComparer.Ordinal)
            },
            ["route"] = server.Route,
            ["secrets"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["blockReturn"] = server.Secrets.BlockReturn,
                ["patterns"] = server.Secrets.Patterns,
                ["redactInLogs"] = server.Secrets.RedactInLogs
            },
            ["tools"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["allow"] = server.Tools.Allow,
                ["block"] = server.Tools.Block,
                ["default"] = FormatToolDefault(server.Tools.Default),
                ["hide"] = server.Tools.Hide
            },
            ["upstream"] = server.Upstream.ToString()
        };

    private static SortedDictionary<string, object?> ToRaw(ToolArgumentPolicyOverride overridePolicy) =>
        new(StringComparer.Ordinal)
        {
            ["commands"] = overridePolicy.Commands is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["blockShell"] = overridePolicy.Commands.BlockShell,
                    ["dangerous"] = overridePolicy.Commands.Dangerous
                },
            ["hosts"] = overridePolicy.Hosts is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allow"] = overridePolicy.Hosts.Allow,
                    ["blockPrivateNetworks"] = overridePolicy.Hosts.BlockPrivateNetworks
                },
            ["paths"] = overridePolicy.Paths is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowedRoots"] = overridePolicy.Paths.AllowedRoots,
                    ["blockTraversal"] = overridePolicy.Paths.BlockTraversal
                }
        };

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string FormatToolDefault(ToolDefaultMode mode) =>
        mode switch
        {
            ToolDefaultMode.Allow => "allow",
            ToolDefaultMode.Hide => "hide",
            _ => "deny"
        };

    private static string FormatUnsupportedBehavior(UnsupportedInspectionBehavior behavior) =>
        behavior switch
        {
            UnsupportedInspectionBehavior.Block => "block",
            UnsupportedInspectionBehavior.PassThrough => "passThrough",
            _ => "warn"
        };
}

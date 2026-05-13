using ProxyWard.Management.Application.Policy;
using YamlDotNet.Serialization;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class YamlManagementPolicyModelSerializer : IManagementPolicyModelYamlSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .Build();

    public string ToYaml(ManagementPolicyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var raw = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["audit"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sink"] = model.Audit?.Sink,
                ["sqlitePath"] = model.Audit?.SqlitePath
            },
            ["inspection"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchToolCalls"] = model.Inspection?.BatchToolCalls,
                ["maxBodyBytes"] = model.Inspection?.MaxBodyBytes,
                ["unsupportedStreaming"] = model.Inspection?.UnsupportedStreaming
            },
            ["mode"] = model.Mode,
            ["observability"] = CreateObservability(model.Observability),
            ["servers"] = model.Servers?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? null : (object?)CreateServer(pair.Value),
                StringComparer.Ordinal)
        };

        return Serializer.Serialize(raw);
    }

    private static SortedDictionary<string, object?> CreateObservability(
        ManagementObservabilityPolicyModel? observability) =>
        new(StringComparer.Ordinal)
        {
            ["applicationInsights"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["connectionStringEnv"] = observability?.ApplicationInsights?.ConnectionStringEnv,
                ["enabled"] = observability?.ApplicationInsights?.Enabled
            },
            ["console"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["enabled"] = observability?.Console?.Enabled
            },
            ["otlp"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["enabled"] = observability?.Otlp?.Enabled,
                ["endpoint"] = observability?.Otlp?.Endpoint
            },
            ["sampling"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tracesRatio"] = observability?.Sampling?.TracesRatio
            },
            ["serviceName"] = observability?.ServiceName
        };

    private static SortedDictionary<string, object?> CreateServer(ManagementServerPolicyModel server) =>
        new(StringComparer.Ordinal)
        {
            ["allowed"] = server.Allowed,
            ["arguments"] = CreateArguments(server.Arguments),
            ["route"] = server.Route,
            ["secrets"] = server.Secrets is null ? null : CreateSecrets(server.Secrets),
            ["tools"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["allow"] = server.Tools?.Allow ?? [],
                ["block"] = server.Tools?.Block ?? [],
                ["default"] = server.Tools?.Default,
                ["hide"] = server.Tools?.Hide ?? []
            },
            ["upstream"] = server.Upstream
        };

    private static SortedDictionary<string, object?> CreateArguments(
        ManagementArgumentPolicyModel? arguments) =>
        new(StringComparer.Ordinal)
        {
            ["commands"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["blockShell"] = arguments?.Commands?.BlockShell,
                ["dangerous"] = arguments?.Commands?.Dangerous ?? []
            },
            ["hosts"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["allow"] = arguments?.Hosts?.Allow ?? [],
                ["blockPrivateNetworks"] = arguments?.Hosts?.BlockPrivateNetworks
            },
            ["paths"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["allowedRoots"] = arguments?.Paths?.AllowedRoots ?? [],
                ["blockTraversal"] = arguments?.Paths?.BlockTraversal
            },
            ["overrides"] = CreateOverrides(arguments?.Overrides)
        };

    private static SortedDictionary<string, object?> CreateSecrets(ManagementSecretsPolicyModel secrets) =>
        new(StringComparer.Ordinal)
        {
            ["blockReturn"] = secrets.BlockReturn,
            ["patterns"] = secrets.Patterns,
            ["redactInLogs"] = secrets.RedactInLogs
        };

    private static SortedDictionary<string, object?> CreateOverrides(
        IReadOnlyDictionary<string, ManagementToolArgumentPolicyOverrideModel>? overrides)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (overrides is null)
        {
            return result;
        }

        foreach (var (key, value) in overrides)
        {
            result[key] = value is null ? null : CreateOverride(value);
        }

        return result;
    }

    private static SortedDictionary<string, object?> CreateOverride(
        ManagementToolArgumentPolicyOverrideModel overrideModel) =>
        new(StringComparer.Ordinal)
        {
            ["commands"] = overrideModel.Commands is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["blockShell"] = overrideModel.Commands.BlockShell,
                    ["dangerous"] = overrideModel.Commands.Dangerous
                },
            ["hosts"] = overrideModel.Hosts is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allow"] = overrideModel.Hosts.Allow,
                    ["blockPrivateNetworks"] = overrideModel.Hosts.BlockPrivateNetworks
                },
            ["paths"] = overrideModel.Paths is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowedRoots"] = overrideModel.Paths.AllowedRoots,
                    ["blockTraversal"] = overrideModel.Paths.BlockTraversal
                }
        };
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ProxyWard.Policy.Configuration;

public static class ProxyWardPolicyLoader
{
    public const string RemovedLockfileMessage = "The 'lockfile:' config key has been removed. Tool schema lock is now persisted in the audit SQLite database. Remove this key from your policy YAML.";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ProxyWardPolicy Load(string yaml)
    {
        RejectRemovedLockfileKey(yaml);

        ProxyWardPolicyYaml? raw;

        try
        {
            raw = Deserializer.Deserialize<ProxyWardPolicyYaml>(yaml);
        }
        catch (Exception ex)
        {
            throw new PolicyValidationException([$"YAML could not be parsed: {ex.Message}"]);
        }

        raw ??= new ProxyWardPolicyYaml();

        var errors = Validate(raw);
        if (errors.Count > 0)
        {
            throw new PolicyValidationException(errors);
        }

        var policy = BuildPolicy(raw);
        return policy with { VersionHash = ComputeVersionHash(policy) };
    }

    public static ProxyWardPolicy WithMode(ProxyWardPolicy policy, ProxyWardMode mode)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var replacement = policy with
        {
            Mode = mode,
            VersionHash = string.Empty
        };

        return replacement with { VersionHash = ComputeVersionHash(replacement) };
    }

    private static List<string> Validate(ProxyWardPolicyYaml raw)
    {
        var errors = new List<string>();

        if (!TryParseMode(raw.Mode, out _))
        {
            errors.Add("mode must be 'audit' or 'enforce'");
        }

        if (raw.Inspection is null)
        {
            errors.Add("inspection section is required");
        }
        else
        {
            if (raw.Inspection.MaxBodyBytes <= 0)
            {
                errors.Add("inspection.maxBodyBytes must be greater than zero");
            }

            if (!TryParseUnsupportedBehavior(raw.Inspection.UnsupportedStreaming, out _))
            {
                errors.Add("inspection.unsupportedStreaming must be 'warn', 'block', or 'passThrough'");
            }

            if (!TryParseBatchToolCallBehavior(raw.Inspection.BatchToolCalls, out _))
            {
                errors.Add("inspection.batchToolCalls must be 'failClosed'");
            }
        }

        if (raw.Audit is null)
        {
            errors.Add("audit section is required");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(raw.Audit.Sink))
            {
                errors.Add("audit.sink is required");
            }

            if (string.Equals(raw.Audit.Sink, "sqlite", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(raw.Audit.SqlitePath))
            {
                errors.Add("audit.sqlitePath is required when audit.sink is sqlite");
            }
        }

        if (raw.Observability is null)
        {
            errors.Add("observability section is required");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(raw.Observability.ServiceName))
            {
                errors.Add("observability.serviceName is required");
            }

            if (raw.Observability.Sampling is not null
                && (raw.Observability.Sampling.TracesRatio < 0 || raw.Observability.Sampling.TracesRatio > 1))
            {
                errors.Add("observability.sampling.tracesRatio must be between 0 and 1");
            }

            if (raw.Observability.ApplicationInsights is not null
                && raw.Observability.ApplicationInsights.Enabled
                && string.IsNullOrWhiteSpace(raw.Observability.ApplicationInsights.ConnectionStringEnv))
            {
                errors.Add("observability.applicationInsights.connectionStringEnv is required when Application Insights is enabled");
            }
        }

        if (raw.Servers is not null)
        {
            foreach (var (serverId, server) in raw.Servers)
            {
                var prefix = $"servers.{serverId}";

                if (string.IsNullOrWhiteSpace(serverId))
                {
                    errors.Add("server id cannot be empty");
                }

                if (server is null)
                {
                    errors.Add($"{prefix} section is required");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(server.Route) || !server.Route.StartsWith('/'))
                {
                    errors.Add($"{prefix}.route must start with '/'");
                }

                if (string.IsNullOrWhiteSpace(server.Upstream)
                    || !Uri.TryCreate(server.Upstream, UriKind.Absolute, out var upstream)
                    || (upstream.Scheme != Uri.UriSchemeHttp && upstream.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"{prefix}.upstream must be an absolute http or https URL");
                }

                if (server.Tools is not null && !TryParseToolDefault(server.Tools.Default, out _))
                {
                    errors.Add($"{prefix}.tools.default must be 'allow' or 'deny'");
                }

                ValidateSecrets(prefix, server.Secrets, errors);
                ValidateArgumentOverrides(prefix, server.Arguments, errors);
            }
        }

        return errors;
    }

    private static void ValidateSecrets(
        string serverPrefix,
        SecretsPolicyYaml? secrets,
        List<string> errors)
    {
        if (secrets?.Patterns is null)
        {
            return;
        }

        for (var index = 0; index < secrets.Patterns.Count; index++)
        {
            var pattern = secrets.Patterns[index];
            var field = $"{serverPrefix}.secrets.patterns[{index}]";
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errors.Add($"{field} must not be empty");
                continue;
            }

            var trimmed = pattern.Trim();
            if (trimmed.Length == 2 && trimmed[0] == '/' && trimmed[^1] == '/')
            {
                errors.Add($"{field} regex body is required");
                continue;
            }

            if (IsRegexPattern(trimmed))
            {
                ValidateSecretRegex(field, trimmed[1..^1], errors);
            }
        }
    }

    private static void ValidateSecretRegex(
        string field,
        string regexBody,
        List<string> errors)
    {
        Regex regex;
        try
        {
            regex = new Regex(regexBody, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            errors.Add($"{field} is not a valid regex");
            return;
        }

        if (regex.IsMatch(string.Empty) || IsTrivialCatchAllRegex(regexBody))
        {
            errors.Add($"{field} is over-broad");
        }
    }

    private static bool IsRegexPattern(string pattern) =>
        pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/';

    private static bool IsTrivialCatchAllRegex(string regexBody)
    {
        var compact = Regex.Replace(regexBody, @"\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return compact switch
        {
            ".*" or ".+" or ".*?" or "^.*$" or "^.+$" or "^.*?$"
                or @"[\s\S]*" or @"[\s\S]+" or @"^[\s\S]*$" or @"^[\s\S]+$" => true,
            _ => false
        };
    }

    private static void ValidateArgumentOverrides(
        string serverPrefix,
        ArgumentPolicyYaml? arguments,
        List<string> errors)
    {
        if (arguments?.Overrides is null)
        {
            return;
        }

        foreach (var (toolName, toolOverride) in arguments.Overrides)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                errors.Add($"{serverPrefix}.arguments.overrides contains an empty tool name");
                continue;
            }

            var prefix = $"{serverPrefix}.arguments.overrides.{toolName.Trim()}";
            if (toolOverride is null)
            {
                errors.Add($"{prefix} section is required");
                continue;
            }

            if (!HasAnyOverride(toolOverride))
            {
                errors.Add($"{prefix} must define at least one argument rule override");
            }
        }
    }

    private static bool HasAnyOverride(ToolArgumentPolicyOverrideYaml toolOverride) =>
        HasAnyPathOverride(toolOverride.Paths)
        || HasAnyHostOverride(toolOverride.Hosts)
        || HasAnyCommandOverride(toolOverride.Commands);

    private static bool HasAnyPathOverride(PathArgumentPolicyOverrideYaml? paths) =>
        paths is not null && (paths.AllowedRoots is not null || paths.BlockTraversal.HasValue);

    private static bool HasAnyHostOverride(HostArgumentPolicyOverrideYaml? hosts) =>
        hosts is not null && (hosts.Allow is not null || hosts.BlockPrivateNetworks.HasValue);

    private static bool HasAnyCommandOverride(CommandArgumentPolicyOverrideYaml? commands) =>
        commands is not null && (commands.BlockShell.HasValue || commands.Dangerous is not null);

    private static ProxyWardPolicy BuildPolicy(ProxyWardPolicyYaml raw)
    {
        var servers = new SortedDictionary<string, ServerPolicy>(StringComparer.Ordinal);

        foreach (var (id, server) in raw.Servers ?? new Dictionary<string, ServerPolicyYaml?>())
        {
            if (server is null)
            {
                throw new InvalidOperationException($"Server '{id}' was null after validation.");
            }

            var tools = server.Tools ?? new ToolPolicyYaml();
            var arguments = server.Arguments ?? new ArgumentPolicyYaml();
            var secrets = server.Secrets ?? new SecretsPolicyYaml();
            var paths = arguments.Paths ?? new PathArgumentPolicyYaml();
            var hosts = arguments.Hosts ?? new HostArgumentPolicyYaml();
            var commands = arguments.Commands ?? new CommandArgumentPolicyYaml();
            var overrides = BuildArgumentOverrides(arguments.Overrides);

            servers[id] = new ServerPolicy(
                id,
                server.Route!,
                new Uri(server.Upstream!, UriKind.Absolute),
                server.Allowed,
                new SecretsPolicy(
                    secrets.RedactInLogs,
                    secrets.BlockReturn,
                    NormalizeList(secrets.Patterns)),
                new ToolPolicy(
                    ParseToolDefault(tools.Default),
                    NormalizeList(tools.Allow),
                    NormalizeList(tools.Block)),
                new ArgumentPolicy(
                    new PathArgumentPolicy(
                        NormalizeList(paths.AllowedRoots),
                        paths.BlockTraversal),
                    new HostArgumentPolicy(
                        NormalizeList(hosts.Allow),
                        hosts.BlockPrivateNetworks),
                    new CommandArgumentPolicy(
                        commands.BlockShell,
                        NormalizeList(commands.Dangerous)),
                    overrides));
        }

        var observability = raw.Observability!;

        return new ProxyWardPolicy(
            ParseMode(raw.Mode),
            new InspectionOptions(
                raw.Inspection!.MaxBodyBytes,
                ParseUnsupportedBehavior(raw.Inspection.UnsupportedStreaming),
                ParseBatchToolCallBehavior(raw.Inspection.BatchToolCalls)),
            new AuditOptions(raw.Audit!.Sink!, raw.Audit.SqlitePath),
            new ObservabilityOptions(
                observability.ServiceName!,
                new ConsoleExporterOptions(observability.Console?.Enabled ?? false),
                new OtlpExporterOptions(
                    observability.Otlp?.Enabled ?? false,
                    observability.Otlp?.Endpoint),
                new ApplicationInsightsOptions(
                    observability.ApplicationInsights?.Enabled ?? false,
                    observability.ApplicationInsights?.ConnectionStringEnv ?? "APPLICATIONINSIGHTS_CONNECTION_STRING"),
                new SamplingOptions(observability.Sampling?.TracesRatio ?? 1.0)),
            servers,
            VersionHash: string.Empty);
    }

    private static IReadOnlyCollection<string> NormalizeList(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static string ComputeVersionHash(ProxyWardPolicy policy)
    {
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["audit"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sink"] = policy.Audit.Sink,
                ["sqlitePath"] = policy.Audit.SqlitePath
            },
            ["inspection"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchToolCalls"] = FormatBatchToolCallBehavior(policy.Inspection.BatchToolCalls),
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
                pair => (object?)CanonicalizeServer(pair.Value),
                StringComparer.Ordinal)
        };

        var json = JsonSerializer.Serialize(canonical);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static SortedDictionary<string, object?> CanonicalizeServer(ServerPolicy server) =>
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
                    pair => (object?)CanonicalizeArgumentOverride(pair.Value),
                    StringComparer.Ordinal)
            },
            ["route"] = server.Route,
            ["tools"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["allow"] = server.Tools.Allow,
                ["block"] = server.Tools.Block,
                ["default"] = FormatToolDefault(server.Tools.Default)
            },
            ["secrets"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["blockReturn"] = server.Secrets.BlockReturn,
                ["patterns"] = server.Secrets.Patterns,
                ["redactInLogs"] = server.Secrets.RedactInLogs
            },
            ["upstream"] = server.Upstream.ToString()
        };

    private static IReadOnlyDictionary<string, ToolArgumentPolicyOverride> BuildArgumentOverrides(
        Dictionary<string, ToolArgumentPolicyOverrideYaml?>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return new SortedDictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal);
        }

        var overrides = new SortedDictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal);
        foreach (var (toolName, toolOverride) in raw)
        {
            if (toolOverride is null)
            {
                continue;
            }

            var normalizedToolName = toolName.Trim();
            overrides[normalizedToolName] = new ToolArgumentPolicyOverride(
                normalizedToolName,
                toolOverride.Paths is null
                    ? null
                    : new PathArgumentPolicyOverride(
                        toolOverride.Paths.AllowedRoots is null
                            ? null
                            : NormalizeList(toolOverride.Paths.AllowedRoots),
                        toolOverride.Paths.BlockTraversal),
                toolOverride.Hosts is null
                    ? null
                    : new HostArgumentPolicyOverride(
                        toolOverride.Hosts.Allow is null
                            ? null
                            : NormalizeList(toolOverride.Hosts.Allow),
                        toolOverride.Hosts.BlockPrivateNetworks),
                toolOverride.Commands is null
                    ? null
                    : new CommandArgumentPolicyOverride(
                        toolOverride.Commands.BlockShell,
                        toolOverride.Commands.Dangerous is null
                            ? null
                            : NormalizeList(toolOverride.Commands.Dangerous)));
        }

        return overrides;
    }

    private static SortedDictionary<string, object?> CanonicalizeArgumentOverride(
        ToolArgumentPolicyOverride toolOverride) =>
        new(StringComparer.Ordinal)
        {
            ["commands"] = toolOverride.Commands is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["blockShell"] = toolOverride.Commands.BlockShell,
                    ["dangerous"] = toolOverride.Commands.Dangerous
                },
            ["hosts"] = toolOverride.Hosts is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allow"] = toolOverride.Hosts.Allow,
                    ["blockPrivateNetworks"] = toolOverride.Hosts.BlockPrivateNetworks
                },
            ["paths"] = toolOverride.Paths is null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["allowedRoots"] = toolOverride.Paths.AllowedRoots,
                    ["blockTraversal"] = toolOverride.Paths.BlockTraversal
                },
            ["toolName"] = toolOverride.ToolName
        };

    private static ProxyWardMode ParseMode(string? value) =>
        TryParseMode(value, out var mode) ? mode : throw new InvalidOperationException();

    private static bool TryParseMode(string? value, out ProxyWardMode mode)
    {
        mode = ProxyWardMode.Audit;
        return Normalize(value) switch
        {
            "audit" => true,
            "enforce" => Set(out mode, ProxyWardMode.Enforce),
            _ => false
        };
    }

    private static UnsupportedInspectionBehavior ParseUnsupportedBehavior(string? value) =>
        TryParseUnsupportedBehavior(value, out var behavior) ? behavior : throw new InvalidOperationException();

    private static bool TryParseUnsupportedBehavior(string? value, out UnsupportedInspectionBehavior behavior)
    {
        behavior = UnsupportedInspectionBehavior.Warn;
        return Normalize(value) switch
        {
            "warn" => true,
            "block" => Set(out behavior, UnsupportedInspectionBehavior.Block),
            "passthrough" => Set(out behavior, UnsupportedInspectionBehavior.PassThrough),
            _ => false
        };
    }

    private static ToolDefaultMode ParseToolDefault(string? value) =>
        TryParseToolDefault(value, out var mode) ? mode : throw new InvalidOperationException();

    private static BatchToolCallBehavior ParseBatchToolCallBehavior(string? value) =>
        TryParseBatchToolCallBehavior(value, out var behavior) ? behavior : throw new InvalidOperationException();

    private static bool TryParseToolDefault(string? value, out ToolDefaultMode mode)
    {
        mode = ToolDefaultMode.Deny;
        return Normalize(value) switch
        {
            "deny" => true,
            "allow" => Set(out mode, ToolDefaultMode.Allow),
            _ => false
        };
    }

    private static bool TryParseBatchToolCallBehavior(string? value, out BatchToolCallBehavior behavior)
    {
        behavior = BatchToolCallBehavior.FailClosed;
        return string.IsNullOrWhiteSpace(value)
            || Normalize(value) == "failclosed";
    }

    private static bool Set<T>(out T target, T value)
    {
        target = value;
        return true;
    }

    private static void RejectRemovedLockfileKey(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);

            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                return;
            }

            foreach (var key in mapping.Children.Keys.OfType<YamlScalarNode>())
            {
                if (string.Equals(key.Value, "lockfile", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PolicyValidationException([RemovedLockfileMessage]);
                }
            }
        }
        catch (PolicyValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PolicyValidationException([$"YAML could not be parsed: {ex.Message}"]);
        }
    }

    private static string Normalize(string? value) =>
        value?.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant() ?? string.Empty;

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Audit ? "audit" : "enforce";

    private static string FormatToolDefault(ToolDefaultMode mode) =>
        mode == ToolDefaultMode.Allow ? "allow" : "deny";

    private static string FormatUnsupportedBehavior(UnsupportedInspectionBehavior behavior) =>
        behavior switch
        {
            UnsupportedInspectionBehavior.Block => "block",
            UnsupportedInspectionBehavior.PassThrough => "passThrough",
            _ => "warn"
        };

    private static string FormatBatchToolCallBehavior(BatchToolCallBehavior behavior) =>
        behavior switch
        {
            _ => "failClosed"
        };

    private sealed class ProxyWardPolicyYaml
    {
        public string? Mode { get; set; }
        public InspectionOptionsYaml? Inspection { get; set; }
        public AuditOptionsYaml? Audit { get; set; }
        public ObservabilityOptionsYaml? Observability { get; set; }
        public Dictionary<string, ServerPolicyYaml?>? Servers { get; set; }
    }

    private sealed class InspectionOptionsYaml
    {
        public int MaxBodyBytes { get; set; }
        public string? UnsupportedStreaming { get; set; }
        public string? BatchToolCalls { get; set; }
    }

    private sealed class AuditOptionsYaml
    {
        public string? Sink { get; set; }
        public string? SqlitePath { get; set; }
    }

    private sealed class ObservabilityOptionsYaml
    {
        public string? ServiceName { get; set; }
        public ConsoleExporterOptionsYaml? Console { get; set; }
        public OtlpExporterOptionsYaml? Otlp { get; set; }
        public ApplicationInsightsOptionsYaml? ApplicationInsights { get; set; }
        public SamplingOptionsYaml? Sampling { get; set; }
    }

    private sealed class ConsoleExporterOptionsYaml
    {
        public bool Enabled { get; set; }
    }

    private sealed class OtlpExporterOptionsYaml
    {
        public bool Enabled { get; set; }
        public string? Endpoint { get; set; }
    }

    private sealed class ApplicationInsightsOptionsYaml
    {
        public bool Enabled { get; set; }
        public string? ConnectionStringEnv { get; set; }
    }

    private sealed class SamplingOptionsYaml
    {
        public double TracesRatio { get; set; } = 1.0;
    }

    private sealed class ServerPolicyYaml
    {
        public string? Route { get; set; }
        public string? Upstream { get; set; }
        public bool Allowed { get; set; }
        public SecretsPolicyYaml? Secrets { get; set; }
        public ToolPolicyYaml? Tools { get; set; }
        public ArgumentPolicyYaml? Arguments { get; set; }
    }

    private sealed class SecretsPolicyYaml
    {
        public bool RedactInLogs { get; set; } = true;
        public bool BlockReturn { get; set; }
        public List<string>? Patterns { get; set; }
    }

    private sealed class ToolPolicyYaml
    {
        public string? Default { get; set; } = "deny";
        public List<string>? Allow { get; set; }
        public List<string>? Block { get; set; }
    }

    private sealed class ArgumentPolicyYaml
    {
        public PathArgumentPolicyYaml? Paths { get; set; }
        public HostArgumentPolicyYaml? Hosts { get; set; }
        public CommandArgumentPolicyYaml? Commands { get; set; }
        public Dictionary<string, ToolArgumentPolicyOverrideYaml?>? Overrides { get; set; }
    }

    private sealed class PathArgumentPolicyYaml
    {
        public List<string>? AllowedRoots { get; set; }
        public bool BlockTraversal { get; set; }
    }

    private sealed class HostArgumentPolicyYaml
    {
        public List<string>? Allow { get; set; }
        public bool BlockPrivateNetworks { get; set; }
    }

    private sealed class CommandArgumentPolicyYaml
    {
        public bool BlockShell { get; set; }
        public List<string>? Dangerous { get; set; }
    }

    private sealed class ToolArgumentPolicyOverrideYaml
    {
        public PathArgumentPolicyOverrideYaml? Paths { get; set; }
        public HostArgumentPolicyOverrideYaml? Hosts { get; set; }
        public CommandArgumentPolicyOverrideYaml? Commands { get; set; }
    }

    private sealed class PathArgumentPolicyOverrideYaml
    {
        public List<string>? AllowedRoots { get; set; }
        public bool? BlockTraversal { get; set; }
    }

    private sealed class HostArgumentPolicyOverrideYaml
    {
        public List<string>? Allow { get; set; }
        public bool? BlockPrivateNetworks { get; set; }
    }

    private sealed class CommandArgumentPolicyOverrideYaml
    {
        public bool? BlockShell { get; set; }
        public List<string>? Dangerous { get; set; }
    }
}

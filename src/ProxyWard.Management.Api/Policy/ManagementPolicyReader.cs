using System.Text;
using ProxyWard.Policy.Configuration;
using YamlDotNet.RepresentationModel;

namespace ProxyWard.Management.Api.Policy;

public sealed class ManagementPolicyReader
{
    private const string MaskedScalar = "[masked]";
    private const string MaskedUserInfo = "***";

    private static readonly string[] SensitiveKeyFragments =
    [
        "token",
        "secret",
        "password",
        "apikey",
        "authorization",
        "connectionstring",
        "credential"
    ];

    private readonly ManagementApiOptions _options;

    public ManagementPolicyReader(ManagementApiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ManagementPolicyResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var policyPath = _options.PolicyPath;
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException($"ProxyWard policy file not found: {policyPath}", policyPath);
        }

        var yaml = await File.ReadAllTextAsync(policyPath, cancellationToken).ConfigureAwait(false);
        var policy = ProxyWardPolicyLoader.Load(yaml);
        var fileInfo = new FileInfo(policyPath);
        var loadedAtUtc = DateTimeOffset.UtcNow;

        return new ManagementPolicyResponse(
            Yaml: MaskYaml(yaml),
            PolicyHash: policy.VersionHash,
            Source: new ManagementPolicySource(
                Path: policyPath,
                Format: "yaml",
                Exists: true,
                LastModifiedUtc: new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                SizeBytes: fileInfo.Length),
            Model: CreateModel(policy),
            ReadOnly: new ManagementPolicyReadOnlyFields(
                PolicyHash: policy.VersionHash,
                SourcePath: policyPath,
                ServerCount: policy.Servers.Count,
                LoadedAtUtc: loadedAtUtc));
    }

    public static ManagementPolicyModel CreateModel(ProxyWardPolicy policy) =>
        new(
            Mode: FormatMode(policy.Mode),
            Inspection: new ManagementInspectionPolicyModel(
                MaxBodyBytes: policy.Inspection.MaxBodyBytes,
                UnsupportedStreaming: FormatUnsupportedInspectionBehavior(policy.Inspection.UnsupportedStreaming),
                BatchToolCalls: FormatBatchToolCallBehavior(policy.Inspection.BatchToolCalls)),
            Audit: new ManagementAuditPolicyModel(
                Sink: policy.Audit.Sink,
                SqlitePath: policy.Audit.SqlitePath),
            Observability: new ManagementObservabilityPolicyModel(
                ServiceName: policy.Observability.ServiceName,
                Console: new ManagementConsoleExporterPolicyModel(policy.Observability.Console.Enabled),
                Otlp: new ManagementOtlpExporterPolicyModel(
                    policy.Observability.Otlp.Enabled,
                    policy.Observability.Otlp.Endpoint),
                ApplicationInsights: new ManagementApplicationInsightsPolicyModel(
                    policy.Observability.ApplicationInsights.Enabled,
                    policy.Observability.ApplicationInsights.ConnectionStringEnv),
                Sampling: new ManagementSamplingPolicyModel(policy.Observability.Sampling.TracesRatio)),
            Servers: policy.Servers.ToDictionary(
                pair => pair.Key,
                pair => CreateServerModel(pair.Value),
                StringComparer.Ordinal));

    private static ManagementServerPolicyModel CreateServerModel(ServerPolicy server) =>
        new(
            Id: server.Id,
            Route: server.Route,
            Upstream: MaskUriUserInfo(server.Upstream.ToString()),
            Allowed: server.Allowed,
            Secrets: new ManagementSecretsPolicyModel(
                RedactInLogs: server.Secrets.RedactInLogs,
                BlockReturn: server.Secrets.BlockReturn,
                Patterns: server.Secrets.Patterns),
            Tools: new ManagementToolPolicyModel(
                Default: FormatToolDefaultMode(server.Tools.Default),
                Allow: server.Tools.Allow,
                Block: server.Tools.Block),
            Arguments: new ManagementArgumentPolicyModel(
                Paths: new ManagementPathArgumentPolicyModel(
                    AllowedRoots: server.Arguments.Paths.AllowedRoots,
                    BlockTraversal: server.Arguments.Paths.BlockTraversal),
                Hosts: new ManagementHostArgumentPolicyModel(
                    Allow: server.Arguments.Hosts.Allow,
                    BlockPrivateNetworks: server.Arguments.Hosts.BlockPrivateNetworks),
                Commands: new ManagementCommandArgumentPolicyModel(
                    BlockShell: server.Arguments.Commands.BlockShell,
                    Dangerous: server.Arguments.Commands.Dangerous),
                Overrides: server.Arguments.Overrides.ToDictionary(
                    pair => pair.Key,
                    pair => CreateOverrideModel(pair.Value),
                    StringComparer.Ordinal)));

    private static ManagementToolArgumentPolicyOverrideModel CreateOverrideModel(
        ToolArgumentPolicyOverride overridePolicy) =>
        new(
            ToolName: overridePolicy.ToolName,
            Paths: overridePolicy.Paths is null
                ? null
                : new ManagementPathArgumentPolicyOverrideModel(
                    overridePolicy.Paths.AllowedRoots,
                    overridePolicy.Paths.BlockTraversal),
            Hosts: overridePolicy.Hosts is null
                ? null
                : new ManagementHostArgumentPolicyOverrideModel(
                    overridePolicy.Hosts.Allow,
                    overridePolicy.Hosts.BlockPrivateNetworks),
            Commands: overridePolicy.Commands is null
                ? null
                : new ManagementCommandArgumentPolicyOverrideModel(
                    overridePolicy.Commands.BlockShell,
                    overridePolicy.Commands.Dangerous));

    private static string MaskYaml(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        var changed = false;
        foreach (var document in stream.Documents)
        {
            changed |= MaskYamlNode(document.RootNode);
        }

        if (!changed)
        {
            return yaml;
        }

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static bool MaskYamlNode(YamlNode node)
    {
        var changed = false;

        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (keyNode, valueNode) in mapping.Children.ToArray())
                {
                    var key = keyNode is YamlScalarNode scalarKey ? scalarKey.Value : null;
                    if (IsSensitiveKey(key))
                    {
                        mapping.Children[keyNode] = new YamlScalarNode(MaskedScalar);
                        changed = true;
                        continue;
                    }

                    changed |= MaskYamlNode(valueNode);
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    changed |= MaskYamlNode(child);
                }

                break;

            case YamlScalarNode scalar:
                var maskedValue = MaskUriUserInfo(scalar.Value);
                if (!string.Equals(maskedValue, scalar.Value, StringComparison.Ordinal))
                {
                    scalar.Value = maskedValue;
                    changed = true;
                }

                break;
        }

        return changed;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = new StringBuilder(key.Length);
        foreach (var character in key)
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        var normalizedKey = normalized.ToString();
        if (normalizedKey.EndsWith("env", StringComparison.Ordinal))
        {
            return false;
        }

        return SensitiveKeyFragments.Any(fragment => normalizedKey.Contains(fragment, StringComparison.Ordinal));
    }

    private static string? MaskUriUserInfo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.UserInfo))
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme).Append("://").Append(MaskedUserInfo).Append('@').Append(uri.Host);
        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port);
        }

        builder.Append(uri.PathAndQuery);
        builder.Append(uri.Fragment);
        return builder.ToString();
    }

    private static string FormatMode(ProxyWardMode mode) =>
        mode == ProxyWardMode.Enforce ? "enforce" : "audit";

    private static string FormatToolDefaultMode(ToolDefaultMode mode) =>
        mode == ToolDefaultMode.Allow ? "allow" : "deny";

    private static string FormatUnsupportedInspectionBehavior(UnsupportedInspectionBehavior behavior) =>
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
}

public sealed record ManagementPolicyResponse(
    string Yaml,
    string PolicyHash,
    ManagementPolicySource Source,
    ManagementPolicyModel Model,
    ManagementPolicyReadOnlyFields ReadOnly);

public sealed record ManagementPolicySource(
    string Path,
    string Format,
    bool Exists,
    DateTimeOffset? LastModifiedUtc,
    long? SizeBytes);

public sealed record ManagementPolicyReadOnlyFields(
    string PolicyHash,
    string SourcePath,
    int ServerCount,
    DateTimeOffset LoadedAtUtc);

public sealed record ManagementPolicyModel(
    string Mode,
    ManagementInspectionPolicyModel Inspection,
    ManagementAuditPolicyModel Audit,
    ManagementObservabilityPolicyModel Observability,
    IReadOnlyDictionary<string, ManagementServerPolicyModel> Servers);

public sealed record ManagementInspectionPolicyModel(
    int MaxBodyBytes,
    string UnsupportedStreaming,
    string BatchToolCalls);

public sealed record ManagementAuditPolicyModel(
    string Sink,
    string? SqlitePath);

public sealed record ManagementObservabilityPolicyModel(
    string ServiceName,
    ManagementConsoleExporterPolicyModel Console,
    ManagementOtlpExporterPolicyModel Otlp,
    ManagementApplicationInsightsPolicyModel ApplicationInsights,
    ManagementSamplingPolicyModel Sampling);

public sealed record ManagementConsoleExporterPolicyModel(bool Enabled);

public sealed record ManagementOtlpExporterPolicyModel(
    bool Enabled,
    string? Endpoint);

public sealed record ManagementApplicationInsightsPolicyModel(
    bool Enabled,
    string ConnectionStringEnv);

public sealed record ManagementSamplingPolicyModel(double TracesRatio);

public sealed record ManagementServerPolicyModel(
    string Id,
    string Route,
    string? Upstream,
    bool Allowed,
    ManagementSecretsPolicyModel? Secrets,
    ManagementToolPolicyModel Tools,
    ManagementArgumentPolicyModel Arguments);

public sealed record ManagementSecretsPolicyModel(
    bool RedactInLogs,
    bool BlockReturn,
    IReadOnlyCollection<string> Patterns);

public sealed record ManagementToolPolicyModel(
    string Default,
    IReadOnlyCollection<string> Allow,
    IReadOnlyCollection<string> Block);

public sealed record ManagementArgumentPolicyModel(
    ManagementPathArgumentPolicyModel Paths,
    ManagementHostArgumentPolicyModel Hosts,
    ManagementCommandArgumentPolicyModel Commands,
    IReadOnlyDictionary<string, ManagementToolArgumentPolicyOverrideModel> Overrides);

public sealed record ManagementPathArgumentPolicyModel(
    IReadOnlyCollection<string> AllowedRoots,
    bool BlockTraversal);

public sealed record ManagementHostArgumentPolicyModel(
    IReadOnlyCollection<string> Allow,
    bool BlockPrivateNetworks);

public sealed record ManagementCommandArgumentPolicyModel(
    bool BlockShell,
    IReadOnlyCollection<string> Dangerous);

public sealed record ManagementToolArgumentPolicyOverrideModel(
    string ToolName,
    ManagementPathArgumentPolicyOverrideModel? Paths,
    ManagementHostArgumentPolicyOverrideModel? Hosts,
    ManagementCommandArgumentPolicyOverrideModel? Commands);

public sealed record ManagementPathArgumentPolicyOverrideModel(
    IReadOnlyCollection<string>? AllowedRoots,
    bool? BlockTraversal);

public sealed record ManagementHostArgumentPolicyOverrideModel(
    IReadOnlyCollection<string>? Allow,
    bool? BlockPrivateNetworks);

public sealed record ManagementCommandArgumentPolicyOverrideModel(
    bool? BlockShell,
    IReadOnlyCollection<string>? Dangerous);

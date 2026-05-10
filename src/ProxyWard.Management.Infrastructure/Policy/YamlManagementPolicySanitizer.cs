using System.Text;
using ProxyWard.Management.Application.Policy;
using YamlDotNet.RepresentationModel;

namespace ProxyWard.Management.Infrastructure.Policy;

public sealed class YamlManagementPolicySanitizer : IManagementPolicyYamlSanitizer
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

    public string MaskSensitiveValues(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        var changed = false;
        foreach (var document in stream.Documents)
        {
            changed |= MaskNode(document.RootNode);
        }

        if (!changed)
        {
            return yaml;
        }

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static bool MaskNode(YamlNode node)
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

                    changed |= MaskNode(valueNode);
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    changed |= MaskNode(child);
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
}

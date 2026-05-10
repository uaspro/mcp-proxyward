using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;

namespace ProxyWard.Audit.Redaction;

public sealed class Redactor : IRedactor
{
    private const string RedactedPlaceholder = "[redacted]";
    private const string RedactedPathPlaceholder = "[redacted-path]";
    private const string RedactedHostPlaceholder = "[redacted-host]";
    private const string RedactedCommandPlaceholder = "[redacted-command]";
    private const string RedactedSecretLiteralPlaceholder = "[redacted-secret:literal]";
    private const string RedactedSecretRegexPlaceholder = "[redacted-secret:regex]";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal)
    {
        "token",
        "secret",
        "password",
        "passwd",
        "apikey",
        "authorization",
        "bearer",
        "accesstoken",
        "refreshtoken",
        "bearertoken",
        "authtoken",
        "secretkey",
        "clientsecret",
        "xapikey"
    };

    private static readonly HashSet<string> CommandKeys = new(StringComparer.Ordinal)
    {
        "command",
        "cmd",
        "shell",
        "script",
        "exec",
        "executable",
        "process",
        "commandline",
        "commandtext",
        "run"
    };

    private static readonly HashSet<string> HostKeys = new(StringComparer.Ordinal)
    {
        "host",
        "hosts",
        "hostname",
        "hostnames",
        "target",
        "targets",
        "targethost",
        "remotehost",
        "serverhost",
        "endpoint",
        "endpoints",
        "address",
        "addresses"
    };

    private static readonly HashSet<string> KnownCommandTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm",
        "curl",
        "wget",
        "nc",
        "sh",
        "bash",
        "zsh",
        "fish",
        "ksh",
        "cmd",
        "powershell",
        "pwsh"
    };

    public RedactedValue Redact(string path, object? value)
    {
        var node = ToNode(value);
        return RedactNode(path, node, SecretPatternSet.Empty);
    }

    public RedactedValue Redact(string path, object? value, SecretRedactionOptions? secretOptions)
    {
        var node = ToNode(value);
        return RedactNode(path, node, SecretPatternSet.Create(secretOptions));
    }

    private static RedactedValue RedactNode(
        string path,
        JsonNode? node,
        SecretPatternSet configuredSecretPatterns)
    {
        if (node is null)
        {
            return new RedactedValue(Value: null, WasRedacted: false);
        }

        if (IsSensitiveKey(path))
        {
            return new RedactedValue(JsonValue.Create(RedactedPlaceholder), WasRedacted: true);
        }

        return node switch
        {
            JsonObject jsonObject => RedactObject(path, jsonObject, configuredSecretPatterns),
            JsonArray jsonArray => RedactArray(path, jsonArray, configuredSecretPatterns),
            JsonValue jsonValue => RedactScalar(path, jsonValue, configuredSecretPatterns),
            _ => new RedactedValue(node.DeepClone(), WasRedacted: false)
        };
    }

    private static RedactedValue RedactObject(
        string path,
        JsonObject source,
        SecretPatternSet configuredSecretPatterns)
    {
        var redactedAny = false;
        var result = new JsonObject();

        foreach (var (key, value) in source)
        {
            var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
            var redacted = RedactNode(childPath, value, configuredSecretPatterns);
            result[key] = redacted.Value?.DeepClone();
            redactedAny |= redacted.WasRedacted;
        }

        return new RedactedValue(result, redactedAny);
    }

    private static RedactedValue RedactArray(
        string path,
        JsonArray source,
        SecretPatternSet configuredSecretPatterns)
    {
        var redactedAny = false;
        var result = new JsonArray();

        for (var index = 0; index < source.Count; index++)
        {
            var childPath = $"{path}[{index}]";
            var redacted = RedactNode(childPath, source[index], configuredSecretPatterns);
            result.Add(redacted.Value?.DeepClone());
            redactedAny |= redacted.WasRedacted;
        }

        return new RedactedValue(result, redactedAny);
    }

    private static RedactedValue RedactScalar(
        string path,
        JsonValue value,
        SecretPatternSet configuredSecretPatterns)
    {
        if (!value.TryGetValue<string>(out var raw) || raw is null)
        {
            return new RedactedValue(value.DeepClone(), WasRedacted: false);
        }

        if (configuredSecretPatterns.TryGetFirstMatch(raw, out var matchType))
        {
            var placeholder = matchType == SecretPatternSet.RegexMatchType
                ? RedactedSecretRegexPlaceholder
                : RedactedSecretLiteralPlaceholder;
            return new RedactedValue(JsonValue.Create(placeholder), WasRedacted: true);
        }

        if (!string.IsNullOrWhiteSpace(raw)
            && (IsCommandKey(path) || LooksLikeShellCommand(raw)))
        {
            return new RedactedValue(JsonValue.Create(RedactedCommandPlaceholder), WasRedacted: true);
        }

        if (LooksLikePath(raw))
        {
            return new RedactedValue(JsonValue.Create(RedactedPathPlaceholder), WasRedacted: true);
        }

        if (TryRedactUrl(raw, out var redactedUrl))
        {
            return new RedactedValue(JsonValue.Create(redactedUrl), WasRedacted: true);
        }

        if (IsHostKey(path) || LooksLikeIpHost(raw))
        {
            return new RedactedValue(JsonValue.Create(RedactedHostPlaceholder), WasRedacted: true);
        }

        return new RedactedValue(JsonValue.Create(raw), WasRedacted: false);
    }

    private static bool IsSensitiveKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = NormalizeKey(GetLastPathSegmentName(path));
        return normalized.Length > 0 && SensitiveKeys.Contains(normalized);
    }

    private static bool IsCommandKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = NormalizeKey(GetLastPathSegmentName(path));
        return normalized.Length > 0 && CommandKeys.Contains(normalized);
    }

    private static bool IsHostKey(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = NormalizeKey(GetLastPathSegmentName(path));
        return normalized.Length > 0 && HostKeys.Contains(normalized);
    }

    private static string GetLastPathSegmentName(string path)
    {
        var lastSegment = path;
        var lastDot = path.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < path.Length - 1)
        {
            lastSegment = path[(lastDot + 1)..];
        }

        var arrayStart = lastSegment.IndexOf('[');
        if (arrayStart > 0)
        {
            lastSegment = lastSegment[..arrayStart];
        }

        return lastSegment;
    }

    private static string NormalizeKey(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var buffer = new char[segment.Length];
        var length = 0;

        foreach (var ch in segment)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer, 0, length);
    }

    private static bool LooksLikePath(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/'))
        {
            return true;
        }

        return false;
    }

    private static bool TryRedactUrl(string value, out string redacted)
    {
        redacted = string.Empty;

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? "/"
            : $"/{RedactedPathPlaceholder}";
        var querySuffix = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?[redacted-query]";
        redacted = $"{uri.Scheme}://{RedactedHostPlaceholder}{path}{querySuffix}";
        return true;
    }

    private static bool LooksLikeIpHost(string value)
    {
        if (IPAddress.TryParse(value, out var ip))
        {
            return ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        }

        if (value.Length >= 4 && value[0] == '[')
        {
            var closing = value.IndexOf(']');
            if (closing > 1 && IPAddress.TryParse(value[1..closing], out var bracketed))
            {
                return bracketed.AddressFamily == AddressFamily.InterNetworkV6;
            }
        }

        return false;
    }

    private static bool LooksLikeShellCommand(string value)
    {
        if (ContainsUnsafeShellPattern(value))
        {
            return true;
        }

        foreach (var token in Tokenize(value))
        {
            var executable = NormalizeExecutableToken(token);
            if (executable.Length > 0 && KnownCommandTokens.Contains(executable))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsUnsafeShellPattern(string value) =>
        value.Contains("&&", StringComparison.Ordinal)
        || value.Contains("||", StringComparison.Ordinal)
        || value.Contains(';')
        || value.Contains('|')
        || value.Contains('`')
        || value.Contains("$(", StringComparison.Ordinal)
        || value.Contains('>')
        || value.Contains('<')
        || value.Contains('\n')
        || value.Contains('\r');

    private static IEnumerable<string> Tokenize(string value)
    {
        var start = -1;

        for (var i = 0; i < value.Length; i++)
        {
            if (IsTokenSeparator(value[i]))
            {
                if (start >= 0)
                {
                    yield return value[start..i];
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            yield return value[start..];
        }
    }

    private static bool IsTokenSeparator(char ch) =>
        char.IsWhiteSpace(ch)
        || ch is '"' or '\'' or '&' or '|' or ';' or '(' or ')' or '<' or '>';

    private static string NormalizeExecutableToken(string token)
    {
        var normalized = token.Trim().Trim('"', '\'');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        normalized = normalized.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < normalized.Length - 1)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1", ".com" })
        {
            if (normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^extension.Length];
                break;
            }
        }

        return normalized;
    }

    private static JsonNode? ToNode(object? value) =>
        value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            decimal dec => JsonValue.Create(dec),
            _ => JsonValue.Create(value.ToString())
        };

}

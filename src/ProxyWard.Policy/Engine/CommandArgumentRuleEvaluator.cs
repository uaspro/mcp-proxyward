using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class CommandArgumentRuleEvaluator
{
    private static readonly HashSet<string> ShellWrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh",
        "bash",
        "zsh",
        "fish",
        "ksh",
        "cmd",
        "powershell",
        "pwsh"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".cmd",
        ".bat",
        ".ps1",
        ".com"
    };

    private static readonly HashSet<string> CommandKeys = new(StringComparer.Ordinal)
    {
        "command",
        "commands",
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

    public PolicyDecision Evaluate(
        ProxyWardMode mode,
        CommandArgumentPolicy commands,
        JsonNode? toolCallParams)
    {
        if (!commands.BlockShell && commands.Dangerous.Count == 0)
        {
            return PolicyDecision.Allow();
        }

        if (toolCallParams is null)
        {
            return PolicyDecision.Allow();
        }

        var dangerous = NormalizeDangerous(commands.Dangerous);
        var scanRoot = ResolveScanRoot(toolCallParams);

        return Scan("arguments", scanRoot, commands.BlockShell, dangerous)
            ? mode.AsBlockDecision(PolicyReasonCodes.DangerousCommand)
            : PolicyDecision.Allow();
    }

    private static HashSet<string> NormalizeDangerous(IReadOnlyCollection<string> values)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var token = NormalizeExecutableToken(value);
            if (token.Length > 0)
            {
                normalized.Add(token);
            }
        }
        return normalized;
    }

    private static JsonNode ResolveScanRoot(JsonNode toolCallParams)
    {
        if (toolCallParams is JsonObject obj
            && obj.TryGetPropertyValue("arguments", out var args)
            && args is not null)
        {
            return args;
        }

        return toolCallParams;
    }

    private static bool Scan(
        string path,
        JsonNode? node,
        bool blockShell,
        IReadOnlySet<string> dangerous)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    var childPath = $"{path}.{pair.Key}";
                    if (Scan(childPath, pair.Value, blockShell, dangerous))
                    {
                        return true;
                    }
                }
                return false;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    if (Scan(path, child, blockShell, dangerous))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValue val:
                return val.TryGetValue<string>(out var s)
                    && !string.IsNullOrWhiteSpace(s)
                    && IsDangerousCommandLikeValue(s, IsCommandKey(path), blockShell, dangerous);
            default:
                return false;
        }
    }

    private static bool IsDangerousCommandLikeValue(
        string value,
        bool isCommandArgument,
        bool blockShell,
        IReadOnlySet<string> dangerous)
    {
        if (!isCommandArgument)
        {
            return false;
        }

        if (blockShell && ContainsUnsafeShellPattern(value))
        {
            return true;
        }

        foreach (var token in Tokenize(value))
        {
            var executable = NormalizeExecutableToken(token);
            if (executable.Length == 0)
            {
                continue;
            }

            if (dangerous.Contains(executable))
            {
                return true;
            }

            if (blockShell && ShellWrappers.Contains(executable))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandKey(string path)
    {
        var normalized = NormalizeKey(GetLastPathSegmentName(path));
        return normalized.Length > 0 && CommandKeys.Contains(normalized);
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

        foreach (var extension in ExecutableExtensions)
        {
            if (normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^extension.Length];
                break;
            }
        }

        return normalized;
    }
}

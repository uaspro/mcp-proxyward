using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProxyWard.Audit.Redaction;

public sealed class SecretPatternSet
{
    public const string LiteralMatchType = "literal";
    public const string RegexMatchType = "regex";

    private readonly IReadOnlyList<ConfiguredSecretPattern> _patterns;

    private SecretPatternSet(IReadOnlyList<ConfiguredSecretPattern> patterns)
    {
        _patterns = patterns;
    }

    public bool IsEmpty => _patterns.Count == 0;

    public static SecretPatternSet Empty { get; } = new([]);

    public static SecretPatternSet Create(SecretRedactionOptions? options)
    {
        if (options is null || !options.RedactInLogs || options.Patterns.Count == 0)
        {
            return Empty;
        }

        var patterns = new List<ConfiguredSecretPattern>();
        foreach (var rawPattern in options.Patterns)
        {
            var pattern = rawPattern?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            if (IsRegexPattern(pattern))
            {
                try
                {
                    patterns.Add(new ConfiguredSecretPattern(
                        IsRegex: true,
                        Literal: null,
                        Regex: new Regex(pattern[1..^1], RegexOptions.None, TimeSpan.FromMilliseconds(100))));
                }
                catch (ArgumentException)
                {
                    // Policy validation rejects invalid regexes; ignore defensively if an invalid pattern reaches runtime.
                }

                continue;
            }

            patterns.Add(new ConfiguredSecretPattern(IsRegex: false, Literal: pattern, Regex: null));
        }

        return patterns.Count == 0 ? Empty : new SecretPatternSet(patterns);
    }

    public SecretPatternMatchResult MatchJson(JsonNode? node)
    {
        if (node is null || IsEmpty)
        {
            return SecretPatternMatchResult.None;
        }

        var matchTypes = new List<string>(capacity: 2);
        Visit(node, matchTypes);
        return matchTypes.Count == 0
            ? SecretPatternMatchResult.None
            : new SecretPatternMatchResult(matchTypes);
    }

    public SecretPatternMatchResult MatchText(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsEmpty)
        {
            return SecretPatternMatchResult.None;
        }

        var matchTypes = new List<string>(capacity: 2);
        AddMatchTypes(text, matchTypes);
        return matchTypes.Count == 0
            ? SecretPatternMatchResult.None
            : new SecretPatternMatchResult(matchTypes);
    }

    public bool TryGetFirstMatch(string raw, out string matchType)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.IsRegex)
            {
                if (pattern.Regex?.IsMatch(raw) == true)
                {
                    matchType = RegexMatchType;
                    return true;
                }

                continue;
            }

            if (!string.IsNullOrEmpty(pattern.Literal)
                && raw.Contains(pattern.Literal, StringComparison.Ordinal))
            {
                matchType = LiteralMatchType;
                return true;
            }
        }

        matchType = string.Empty;
        return false;
    }

    private void Visit(JsonNode node, List<string> matchTypes)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var (_, value) in jsonObject)
                {
                    if (value is not null)
                    {
                        Visit(value, matchTypes);
                    }
                }
                break;
            case JsonArray jsonArray:
                foreach (var value in jsonArray)
                {
                    if (value is not null)
                    {
                        Visit(value, matchTypes);
                    }
                }
                break;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var raw) && raw is not null:
                AddMatchTypes(raw, matchTypes);
                break;
        }
    }

    private void AddMatchTypes(string raw, List<string> matchTypes)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.IsRegex)
            {
                if (pattern.Regex?.IsMatch(raw) == true)
                {
                    AddDistinct(matchTypes, RegexMatchType);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(pattern.Literal)
                && raw.Contains(pattern.Literal, StringComparison.Ordinal))
            {
                AddDistinct(matchTypes, LiteralMatchType);
            }
        }
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    private static bool IsRegexPattern(string pattern) =>
        pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/';

    private sealed record ConfiguredSecretPattern(
        bool IsRegex,
        string? Literal,
        Regex? Regex);
}

public sealed record SecretPatternMatchResult(IReadOnlyCollection<string> MatchTypes)
{
    public static SecretPatternMatchResult None { get; } = new([]);

    public bool WasMatched => MatchTypes.Count > 0;
}

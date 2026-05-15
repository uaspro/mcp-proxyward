using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class PathArgumentRuleEvaluator
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    public PolicyDecision Evaluate(ProxyWardMode mode, PathArgumentPolicy paths, JsonNode? toolCallParams)
    {
        if (paths.AllowedRoots.Count == 0 && !paths.BlockTraversal)
        {
            return PolicyDecision.Allow();
        }

        if (toolCallParams is null)
        {
            return PolicyDecision.Allow();
        }

        var scanRoot = ResolveScanRoot(toolCallParams);
        var canonicalRoots = paths.AllowedRoots.Count == 0
            ? Array.Empty<string>()
            : paths.AllowedRoots.Select(NormalizeRoot).ToArray();

        var hasTraversal = false;
        var hasOutsideRoot = false;

        Scan(scanRoot, paths.BlockTraversal, canonicalRoots, ref hasTraversal, ref hasOutsideRoot);

        if (!hasTraversal && !hasOutsideRoot)
        {
            return PolicyDecision.Allow();
        }

        var reasons = new List<string>(2);
        if (hasTraversal)
        {
            reasons.Add(PolicyReasonCodes.PathTraversal);
        }
        if (hasOutsideRoot)
        {
            reasons.Add(PolicyReasonCodes.PathOutsideAllowedRoots);
        }

        return mode.AsBlockDecision(reasons);
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

    private static void Scan(
        JsonNode? node,
        bool blockTraversal,
        IReadOnlyList<string> canonicalRoots,
        ref bool hasTraversal,
        ref bool hasOutsideRoot)
    {
        if (node is null)
        {
            return;
        }

        if (Maximal(blockTraversal, canonicalRoots, hasTraversal, hasOutsideRoot))
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    Scan(pair.Value, blockTraversal, canonicalRoots, ref hasTraversal, ref hasOutsideRoot);
                    if (Maximal(blockTraversal, canonicalRoots, hasTraversal, hasOutsideRoot))
                    {
                        return;
                    }
                }
                break;

            case JsonArray arr:
                foreach (var child in arr)
                {
                    Scan(child, blockTraversal, canonicalRoots, ref hasTraversal, ref hasOutsideRoot);
                    if (Maximal(blockTraversal, canonicalRoots, hasTraversal, hasOutsideRoot))
                    {
                        return;
                    }
                }
                break;

            case JsonValue val:
                if (val.TryGetValue<string>(out var s) && IsCandidatePath(s))
                {
                    EvaluateCandidate(s, blockTraversal, canonicalRoots, ref hasTraversal, ref hasOutsideRoot);
                }
                break;
        }
    }

    private static bool Maximal(
        bool blockTraversal,
        IReadOnlyList<string> canonicalRoots,
        bool hasTraversal,
        bool hasOutsideRoot)
    {
        var traversalDone = !blockTraversal || hasTraversal;
        var rootsDone = canonicalRoots.Count == 0 || hasOutsideRoot;
        return traversalDone && rootsDone;
    }

    private static void EvaluateCandidate(
        string candidate,
        bool blockTraversal,
        IReadOnlyList<string> canonicalRoots,
        ref bool hasTraversal,
        ref bool hasOutsideRoot)
    {
        var segments = candidate.Split(PathSeparators, StringSplitOptions.None);

        if (blockTraversal && !hasTraversal && ContainsTraversalSegment(segments))
        {
            hasTraversal = true;
        }

        if (canonicalRoots.Count > 0 && !hasOutsideRoot)
        {
            var resolved = ResolvePosixPath(segments);
            if (resolved is null || !IsInsideAnyRoot(resolved, canonicalRoots))
            {
                hasOutsideRoot = true;
            }
        }
    }

    private static bool IsCandidatePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IsAbsoluteUrl(value))
        {
            return false;
        }

        if (value.IndexOfAny(PathSeparators) >= 0)
        {
            return true;
        }

        if (value[0] == '~')
        {
            return true;
        }

        if (value == "..")
        {
            return true;
        }

        if (value.Length >= 2 && IsLetter(value[0]) && value[1] == ':')
        {
            return true;
        }

        return false;
    }

    private static bool IsAbsoluteUrl(string value)
    {
        // Story 5.2 owns host/URL argument rules. file:// URLs intentionally bypass
        // path checks here — they will be handled by host rules in the next story.
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Scheme);
    }

    private static bool ContainsTraversalSegment(string[] segments)
    {
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return true;
            }
        }
        return false;
    }

    private static string? ResolvePosixPath(string[] segments)
    {
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var first = segments[0];
        var isAbsolute = false;
        var startIndex = 0;

        if (first.Length == 0)
        {
            isAbsolute = true;
            startIndex = 1;
        }
        else if (first.Length >= 2 && IsLetter(first[0]) && first[1] == ':')
        {
            return null;
        }
        else if (first.Length > 0 && first[0] == '~')
        {
            return null;
        }

        var stack = new List<string>();
        for (var i = startIndex; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!isAbsolute)
                {
                    stack.Add("..");
                }
                continue;
            }

            stack.Add(segment);
        }

        var joined = string.Join('/', stack);
        return isAbsolute ? "/" + joined : joined;
    }

    private static bool IsInsideAnyRoot(string resolvedPath, IReadOnlyList<string> canonicalRoots)
    {
        foreach (var root in canonicalRoots)
        {
            if (root == "/" && resolvedPath.Length > 0 && resolvedPath[0] == '/')
            {
                return true;
            }

            if (resolvedPath.Equals(root, StringComparison.Ordinal))
            {
                return true;
            }

            if (resolvedPath.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeRoot(string root)
    {
        var normalized = root.Replace('\\', '/');
        while (normalized.Length > 1 && normalized[^1] == '/')
        {
            normalized = normalized[..^1];
        }
        return normalized;
    }

    private static bool IsLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}

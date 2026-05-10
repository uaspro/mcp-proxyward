namespace ProxyWard.PerformanceTests;

internal sealed record PerformanceOptions(
    int RatePerSecond,
    TimeSpan Warmup,
    TimeSpan Duration,
    bool IncludeToolsList,
    string ArtifactsDirectory)
{
    public static PerformanceOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            values[key] = value;
        }

        return new PerformanceOptions(
            RatePerSecond: ReadInt(values, "rate", 50),
            Warmup: TimeSpan.FromSeconds(ReadInt(values, "warmup", 5)),
            Duration: TimeSpan.FromSeconds(ReadInt(values, "duration", 30)),
            IncludeToolsList: ReadBool(values, "include-tools-list", true),
            ArtifactsDirectory: ResolveArtifactsDirectory(
                values.TryGetValue("artifacts", out var artifacts)
                    ? artifacts
                    : Path.Combine("artifacts", "performance")));
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var raw) || !int.TryParse(raw, out var parsed) || parsed <= 0)
        {
            return fallback;
        }

        return parsed;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var raw)
            ? raw.Equals("true", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("1", StringComparison.OrdinalIgnoreCase)
              || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            : fallback;

    private static string ResolveArtifactsDirectory(string path) =>
        Path.IsPathFullyQualified(path)
            ? path
            : Path.GetFullPath(Path.Combine(FindRepoRoot(), path));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "McpProxyWard.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

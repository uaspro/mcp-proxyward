using ProxyWard.Policy.Persistence;

namespace ProxyWard.IntegrationTests;

internal static class TestFiles
{
    public static string NewSqlitePath(string prefix = "proxyward") =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");

    public static string NewYamlPath(string prefix = "proxyward") =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.yaml");

    public static string SavePolicy(string yaml)
    {
        var path = NewYamlPath();
        new SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    public static string YamlPath(string path) =>
        path.Replace('\\', '/');

    public static void DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void DeleteSqlite(string databasePath)
    {
        DeleteIfExists(databasePath);
        DeleteIfExists($"{databasePath}-shm");
        DeleteIfExists($"{databasePath}-wal");
    }
}

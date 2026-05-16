using Microsoft.Data.Sqlite;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.IntegrationTests;

internal static class TestFiles
{
    private static readonly HashSet<string> TrackedSqlitePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TrackedSqlitePathsLock = new();

    static TestFiles()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTrackedSqliteFiles();
    }

    public static string NewSqlitePath(string prefix = "proxyward")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");
        TrackTempSqlite(path);
        return path;
    }

    public static string SavePolicy(string yaml)
    {
        var path = NewSqlitePath();
        new SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    public static string YamlPath(string path) =>
        path.Replace('\\', '/');

    public static void DeleteIfExists(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(25);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    public static void TrackTempSqlite(string? databasePath)
    {
        if (!IsProxyWardTempSqlite(databasePath, out var fullPath))
        {
            return;
        }

        lock (TrackedSqlitePathsLock)
        {
            TrackedSqlitePaths.Add(fullPath);
        }
    }

    public static void DeleteTempSqlite(string? databasePath)
    {
        if (!IsProxyWardTempSqlite(databasePath, out var fullPath))
        {
            return;
        }

        DeleteSqlite(fullPath);
    }

    public static void DeleteSqlite(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(databasePath);
        SqliteConnection.ClearAllPools();
        DeleteIfExists(fullPath);
        DeleteIfExists($"{fullPath}-shm");
        DeleteIfExists($"{fullPath}-wal");

        lock (TrackedSqlitePathsLock)
        {
            TrackedSqlitePaths.Remove(fullPath);
        }
    }

    private static void DeleteTrackedSqliteFiles()
    {
        string[] paths;
        lock (TrackedSqlitePathsLock)
        {
            paths = TrackedSqlitePaths.ToArray();
            TrackedSqlitePaths.Clear();
        }

        foreach (var path in paths)
        {
            DeleteSqlite(path);
        }
    }

    private static bool IsProxyWardTempSqlite(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        fullPath = Path.GetFullPath(path);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetFileName(fullPath).StartsWith("proxyward-", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(fullPath), ".db", StringComparison.OrdinalIgnoreCase);
    }
}

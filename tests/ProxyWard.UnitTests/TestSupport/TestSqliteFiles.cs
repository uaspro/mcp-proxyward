using Microsoft.Data.Sqlite;

namespace ProxyWard.UnitTests;

internal static class TestSqliteFiles
{
    public static string NewPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");

    public static void Delete(string? databasePath)
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
    }

    private static void DeleteIfExists(string path)
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
}

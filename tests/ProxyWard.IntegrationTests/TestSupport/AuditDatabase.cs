using Microsoft.Data.Sqlite;

namespace ProxyWard.IntegrationTests;

internal static class AuditDatabase
{
    public static List<T> ReadEventually<T>(Func<List<T>> readOnce)
    {
        ArgumentNullException.ThrowIfNull(readOnce);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var rows = readOnce();
                if (rows.Count > 0)
                {
                    return rows;
                }
            }
            catch (Exception ex) when (ex is SqliteException or IOException)
            {
                lastException = ex;
            }

            Thread.Sleep(25);
        }

        if (lastException is not null)
        {
            throw new InvalidOperationException("Queued audit events were not persisted before the timeout.", lastException);
        }

        return readOnce();
    }
}

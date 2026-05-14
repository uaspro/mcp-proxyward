using Microsoft.Data.Sqlite;

namespace ProxyWard.IntegrationTests;

internal static class AuditDatabase
{
    public static async Task<List<T>> ReadEventuallyAsync<T>(
        Func<List<T>> readOnce,
        CancellationToken cancellationToken = default)
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

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        if (lastException is not null)
        {
            throw new InvalidOperationException("Queued audit events were not persisted before the timeout.", lastException);
        }

        return readOnce();
    }
}

using Microsoft.Data.Sqlite;

namespace PayPlanner.Api.Helpers;

public static class SqliteRetryHelper
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        ILogger? logger,
        CancellationToken cancellationToken,
        int maxRetries = 3,
        int initialDelayMs = 100,
        bool retrySharedCacheLock = false)
    {
        var attempt = 0;
        var delay = initialDelayMs;

        while (true)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (SqliteException ex) when (attempt < maxRetries &&
                (ex.SqliteErrorCode == 5 ||
                 retrySharedCacheLock && ex.SqliteErrorCode == 6 && ex.SqliteExtendedErrorCode == 262))
            {
                attempt += 1;
                logger?.LogWarning(ex, "SQLite busy/locked. Retry {Attempt}/{Max}", attempt, maxRetries);
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }
}

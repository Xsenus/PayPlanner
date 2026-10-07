using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Storage;

// Hold a connection for one operation, without adding a transaction or a read snapshot.
// EF's open count preserves a connection already owned by the caller.
public static class DatabaseConnectionOperation
{
    public static async Task<T> RunAsync<T>(DbContext db, Func<Task<T>> operation, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await operation();
        await db.Database.OpenConnectionAsync(ct);
        Exception? primaryFailure = null;
        try
        {
            return await operation();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                await db.Database.CloseConnectionAsync();
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                // Retain both failures; connection cleanup must not replace the operation's error.
                primaryFailure.Data["DatabaseConnectionOperation.CloseFailure"] = cleanupFailure;
            }
        }
    }
}

using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Tasks;

/// <summary>Serializes task changes with actor and role access without rewriting existing data.</summary>
internal sealed class TaskDeadlineRequestLease : IAsyncDisposable
{
    private readonly PaymentContext db;
    private readonly IDbContextTransaction transaction;
    private readonly SqliteTransaction? sqliteTransaction;
    private readonly bool closeConnection;
    private bool commitAttempted;
    private bool disposed;

    private TaskDeadlineRequestLease(PaymentContext db, IDbContextTransaction transaction, SqliteTransaction? sqliteTransaction = null, bool closeConnection = false)
        => (this.db, this.transaction, this.sqliteTransaction, this.closeConnection) = (db, transaction, sqliteTransaction, closeConnection);

    public static async Task<TaskDeadlineRequestLease?> TryAcquireAsync(PaymentContext db, int userId, int taskId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Deadline requests require a clean context and their own transaction.");
        TaskDeadlineRequestLease? lease = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                lease = new TaskDeadlineRequestLease(db, await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct));
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"Id\" = {taskId} FOR UPDATE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"TaskJournalItemId\" = {taskId} ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                var salesRequestId = await db.TaskJournalItems.AsNoTracking().Where(task => task.Id == taskId)
                    .Select(task => task.SalesRequestId).SingleOrDefaultAsync(ct);
                if (salesRequestId.HasValue)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} FOR SHARE NOWAIT", ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" IN (SELECT \"ResponsibleUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} UNION SELECT \"CreatedByUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                }
            }
            else if (db.Database.IsSqlite())
            {
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                var opened = connection.State != ConnectionState.Open;
                if (opened) await db.Database.OpenConnectionAsync(ct);
                var timeout = connection.DefaultTimeout;
                SqliteTransaction? native = null;
                try
                {
                    connection.DefaultTimeout = 1;
                    native = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                    var tx = await db.Database.UseTransactionAsync(native, ct) ?? throw new InvalidOperationException("SQLite deadline transaction was not enlisted.");
                    lease = new TaskDeadlineRequestLease(db, tx, native, opened);
                }
                catch
                {
                    if (native is not null) await native.DisposeAsync();
                    if (opened) await db.Database.CloseConnectionAsync();
                    throw;
                }
                finally { connection.DefaultTimeout = timeout; }
            }
            else throw new InvalidOperationException("Unsupported provider for deadline request protection.");
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == userId, ct))
            {
                await lease!.DisposeAsync();
                return null;
            }
            return lease;
        }
        catch (Exception error) when (IsLockConflict(error))
        {
            if (lease is not null) await lease.DisposeAsync();
            return null;
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    private static bool IsLockConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure }
                || current is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        return false;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        // Once commit is attempted its outcome can be unknown (e.g. the
        // acknowledgement was lost). Do not mask that error by rolling back an
        // already committed transaction; disposing an uncommitted one still rolls it back.
        commitAttempted = true;
        await transaction.CommitAsync(ct);
        await DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { if (!commitAttempted) await transaction.RollbackAsync(CancellationToken.None); }
        finally
        {
            await transaction.DisposeAsync();
            if (sqliteTransaction is not null) await sqliteTransaction.DisposeAsync();
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }
}

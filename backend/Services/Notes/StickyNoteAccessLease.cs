using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notes;

/// <summary>Serializes a note write with changes to its actor and board access.</summary>
internal sealed class StickyNoteAccessLease : IAsyncDisposable
{
    private readonly PaymentContext db;
    private readonly IDbContextTransaction transaction;
    private readonly SqliteTransaction? sqliteTransaction;
    private readonly bool closeConnection;
    private bool committed;

    private StickyNoteAccessLease(PaymentContext db, IDbContextTransaction transaction, SqliteTransaction? sqliteTransaction = null, bool closeConnection = false)
        => (this.db, this.transaction, this.sqliteTransaction, this.closeConnection) = (db, transaction, sqliteTransaction, closeConnection);

    public static async Task<StickyNoteAccessLease?> TryAcquireAsync(PaymentContext db, int userId, int noteId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Note writes require a clean context and their own transaction.");
        StickyNoteAccessLease? lease = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                lease = new StickyNoteAccessLease(db, await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct));
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                // Legacy roles without configured permissions use defaults. Prevent a concurrent
                // insertion from changing that decision until this operation finishes.
                var hasPermissions = await db.Users.AsNoTracking().Where(user => user.Id == userId)
                    .AnyAsync(user => db.RolePermissions.Any(permission => permission.RoleId == user.RoleId), ct);
                if (!hasPermissions)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR UPDATE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"StickyNoteBoards\" WHERE \"Id\" = (SELECT \"BoardId\" FROM \"StickyNotes\" WHERE \"Id\" = {noteId}) FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"StickyNoteBoardMembers\" WHERE \"BoardId\" = (SELECT \"BoardId\" FROM \"StickyNotes\" WHERE \"Id\" = {noteId}) AND \"UserId\" = {userId} FOR SHARE NOWAIT", ct);
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
                    var tx = await db.Database.UseTransactionAsync(native, ct) ?? throw new InvalidOperationException("SQLite note transaction was not enlisted.");
                    lease = new StickyNoteAccessLease(db, tx, native, opened);
                }
                catch
                {
                    if (native is not null) await native.DisposeAsync();
                    if (opened) await db.Database.CloseConnectionAsync();
                    throw;
                }
                finally { connection.DefaultTimeout = timeout; }
            }
            else throw new InvalidOperationException("Unsupported provider for note access protection.");
            // Do not apply a legacy fallback to a vanished employee while committing a write.
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

    public async Task CommitAsync(CancellationToken ct) { await transaction.CommitAsync(ct); committed = true; }
    public async ValueTask DisposeAsync()
    {
        try { if (!committed) await transaction.RollbackAsync(CancellationToken.None); }
        finally
        {
            await transaction.DisposeAsync();
            if (sqliteTransaction is not null) await sqliteTransaction.DisposeAsync();
            if (closeConnection) await db.Database.CloseConnectionAsync();
        }
    }
}

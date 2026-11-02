using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

/// <summary>One short owned request transaction. Never nests FinancialSourceLease or retries a write.</summary>
internal sealed class PaymentCreateAttemptLease : IAsyncDisposable
{
    private readonly PaymentContext db;
    private readonly IDbContextTransaction transaction;
    private readonly SqliteTransaction? sqliteTransaction;
    private readonly bool closeConnection;
    private bool committed;
    private bool disposed;
    public bool CommitStarted { get; private set; }

    private PaymentCreateAttemptLease(PaymentContext db, IDbContextTransaction transaction,
        SqliteTransaction? sqlite = null, bool close = false)
        => (this.db, this.transaction, sqliteTransaction, closeConnection) = (db, transaction, sqlite, close);

    public static async Task<PaymentCreateAttemptLease> BeginAsync(PaymentContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Payment attempt requires its own transaction.");
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new PaymentCreateAttemptConflictException();
        if (!db.Database.IsNpgsql() && !db.Database.IsSqlite())
            throw new NotSupportedException("Payment attempts support SQLite and PostgreSQL only.");
        // Only clean read caches are discarded. Never persist/detach unrelated pending writes.
        db.ChangeTracker.Clear();
        if (db.Database.IsNpgsql())
            return new(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct));
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        var timeout = connection.DefaultTimeout;
        SqliteTransaction? native = null;
        try
        {
            if (opened) await db.Database.OpenConnectionAsync(ct);
            ct.ThrowIfCancellationRequested();
            connection.DefaultTimeout = 1; // zero is unlimited; no application retry
            native = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            ct.ThrowIfCancellationRequested();
            var enlisted = await db.Database.UseTransactionAsync(native, ct)
                ?? throw new InvalidOperationException("Payment attempt transaction was not enlisted.");
            return new(db, enlisted, native, opened);
        }
        catch
        {
            if (native is not null) await native.DisposeAsync();
            if (opened) await db.Database.CloseConnectionAsync();
            throw;
        }
        finally { connection.DefaultTimeout = timeout; }
    }

    public async Task LockActorPermissionsAsync(int actorId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actorId} FOR SHARE NOWAIT", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) FOR SHARE NOWAIT", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {actorId} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);
    }

    public async Task<PaymentCreateAttempt?> LockAttemptAsync(Guid id, int actorId, CancellationToken ct)
        => await LockAttemptAsync(db, id, actorId, ct);

    internal static async Task<PaymentCreateAttempt?> LockAttemptAsync(PaymentContext db, Guid id, int actorId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM \"PaymentCreateAttempts\" WHERE \"Id\" = {id} AND \"ActorUserId\" = {actorId} FOR UPDATE NOWAIT").ToArrayAsync(ct);
        // BeginAsync discarded clean tracked copies; read authoritative row after the lease.
        return await db.PaymentCreateAttempts.SingleOrDefaultAsync(a => a.Id == id && a.ActorUserId == actorId, ct);
    }

    public Task LockSellerSelectionAsync(int? companyId, int? branchId, CancellationToken ct)
        => PaymentCreateInputRules.LockSellerSelectionAsync(db, companyId, branchId, ct);

    public async Task LockPrimaryAsync(int id, CancellationToken ct)
    {
        // Visibility is checked before and again after this raw identity-only lock.
        // DELETE/soft-delete of Payments needs this row lock; no dangling primary is admitted.
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<int>($"SELECT \"Id\" AS \"Value\" FROM \"Payments\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT").ToArrayAsync(ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        CommitStarted = true;
        await transaction.CommitAsync(ct);
        committed = true;
    }

    public static bool IsPrecommitConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PaymentCreateAttemptConflictException or DbUpdateConcurrencyException
                || current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }
                || current is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!committed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
                if (!CommitStarted) db.ChangeTracker.Clear();
            }
        }
        finally
        {
            try { await transaction.DisposeAsync(); }
            finally
            {
                try { if (sqliteTransaction is not null) await sqliteTransaction.DisposeAsync(); }
                finally { if (closeConnection) await db.Database.CloseConnectionAsync(); }
            }
        }
    }
}

internal sealed class PaymentCreateAttemptConflictException : Exception { }
internal sealed class PaymentCreateAttemptExpiredException : Exception { }

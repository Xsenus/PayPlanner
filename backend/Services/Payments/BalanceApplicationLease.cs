using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Payments;

internal sealed class BalanceApplicationLease : IAsyncDisposable
{
    private readonly PaymentContext db;
    private readonly IDbContextTransaction transaction;
    private readonly SqliteTransaction? native;
    private readonly bool closeConnection;
    private bool committed;
    public bool CommitStarted { get; private set; }

    private BalanceApplicationLease(PaymentContext db, IDbContextTransaction transaction, SqliteTransaction? native = null, bool closeConnection = false)
        => (this.db, this.transaction, this.native, this.closeConnection) = (db, transaction, native, closeConnection);

    public static Task<BalanceApplicationLease> BeginAsync(PaymentContext db, int actorId, CancellationToken ct)
        => BeginCoreAsync(db, actorId, ct);

    // A trusted background job has no interactive actor to impersonate. It still
    // owns the same clean transaction and payment locks as interactive writers.
    public static Task<BalanceApplicationLease> BeginSystemAsync(PaymentContext db, CancellationToken ct)
        => BeginCoreAsync(db, null, ct);

    // Banking owns a longer-lived context with unchanged account/user references.
    // Keep those identities, still reject pending writes and refresh payment graphs
    // explicitly under this lease before allocating money.
    public static Task<BalanceApplicationLease> BeginWithTrackedReadsAsync(PaymentContext db, int? actorId, CancellationToken ct)
        => BeginCoreAsync(db, actorId, ct, clearTracker: false);

    private static async Task<BalanceApplicationLease> BeginCoreAsync(PaymentContext db, int? actorId, CancellationToken ct, bool clearTracker = true)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new BalanceApplicationConflictException();
        BalanceApplicationLease? lease = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                lease = new(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct));
                if (actorId.HasValue)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actorId.Value} FOR SHARE NOWAIT", ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR SHARE NOWAIT", ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {actorId.Value} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);
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
                    ct.ThrowIfCancellationRequested();
                    connection.DefaultTimeout = 1;
                    native = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                    var transaction = await db.Database.UseTransactionAsync(native, ct)
                        ?? throw new InvalidOperationException("Balance application transaction was not enlisted.");
                    lease = new(db, transaction, native, opened);
                }
                catch
                {
                    if (native is not null) await native.DisposeAsync();
                    if (opened) await db.Database.CloseConnectionAsync();
                    throw;
                }
                finally { connection.DefaultTimeout = timeout; }
            }
            else throw new NotSupportedException("Balance receipts support SQLite and PostgreSQL only.");
            // Admit only clean request contexts; never discard unrelated pending work.
            if (clearTracker) db.ChangeTracker.Clear();
            return lease;
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    public async Task LockOwnedPaymentAsync(int id, int? clientId, int? legalEntityId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        if (clientId.HasValue)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} AND \"ClientId\" = {clientId.Value} AND \"LegalEntityId\" IS NULL FOR UPDATE NOWAIT", ct);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} AND \"LegalEntityId\" = {legalEntityId!.Value} AND \"ClientId\" IS NULL FOR UPDATE NOWAIT", ct);
    }

    public async Task LockPaymentAsync(int id, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
    }

    public async Task LockPaymentsAsync(int[] ids, CancellationToken ct)
    {
        if (ids.Length > 0 && db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        CommitStarted = true;
        await transaction.CommitAsync(ct);
        committed = true;
    }

    public static bool IsConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is BalanceApplicationConflictException or DbUpdateConcurrencyException
                || current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected }
                || current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_BalanceApplicationReceipts" or "IX_ClientBalanceTransactions_ExternalKey" }
                || current is SqliteException { SqliteErrorCode: 5 or 6 }
                || current is SqliteException { SqliteErrorCode: 19 } sqlite
                && (sqlite.Message.Contains("BalanceApplicationReceipts", StringComparison.Ordinal)
                    || sqlite.Message.Contains("ClientBalanceTransactions.ExternalKey", StringComparison.Ordinal))) return true;
        return false;
    }

    public static bool IsDefiniteDatabaseAbort(Exception error)
    {
        // A server-reported abort is retryable even at COMMIT. Transport/cancellation
        // failures have an unknown outcome and must retain the original operation key.
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!committed && !CommitStarted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            try { await transaction.DisposeAsync(); }
            finally
            {
                if (native is not null) await native.DisposeAsync();
                if (closeConnection) await db.Database.CloseConnectionAsync();
            }
        }
    }
}

internal sealed class BalanceApplicationConflictException : Exception;

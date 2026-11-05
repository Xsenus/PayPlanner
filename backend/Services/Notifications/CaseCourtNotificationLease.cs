using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notifications;

internal sealed class CaseCourtNotificationLease(PaymentContext db, IDbContextTransaction transaction,
    HashSet<object> initialEntities, SqliteTransaction? native = null, bool closeConnection = false) : IAsyncDisposable
{
    private bool commitAttempted;
    private bool committed;
    private bool disposed;

    public static async Task<CaseCourtNotificationLease> BeginAsync(PaymentContext db, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Court inbox persistence requires a clean context.");
        var initial = db.ChangeTracker.Entries().Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        if (db.Database.IsNpgsql())
            return new(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct), initial);
        if (!db.Database.IsSqlite()) throw new InvalidOperationException("Unsupported court inbox database provider.");
        var close = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (close) await db.Database.OpenConnectionAsync(ct);
        SqliteTransaction? sqlite = null;
        try
        {
            sqlite = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: false);
            var wrapped = await db.Database.UseTransactionAsync(sqlite, ct)
                ?? throw new InvalidOperationException("Court inbox transaction could not be attached.");
            return new(db, wrapped, initial, sqlite, close);
        }
        catch
        {
            if (sqlite is not null) await sqlite.DisposeAsync();
            if (close) await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        commitAttempted = true;
        await transaction.CommitAsync(ct);
        committed = true;
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!commitAttempted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
            }
        }
        finally
        {
            try { await transaction.DisposeAsync(); }
            finally
            {
                if (native is not null) await native.DisposeAsync();
                if (closeConnection) await db.Database.CloseConnectionAsync();
                if (!committed)
                    foreach (var entry in db.ChangeTracker.Entries().Where(entry => !initialEntities.Contains(entry.Entity)).ToArray())
                        entry.State = EntityState.Detached;
            }
        }
    }
}

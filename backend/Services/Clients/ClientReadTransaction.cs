using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientReadTransaction
{
    // Keep approval, permissions, visibility and the returned data in one snapshot.
    // Unlike a write lease, this must not reserve SQLite's database-wide writer lock.
    public static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal? actor,
        Func<Task<IActionResult>> read, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        // A reader never discards, saves or rolls back another operation's work.
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) return Conflict();

        IDbContextTransaction? transaction = null;
        SqliteTransaction? native = null;
        SqliteConnection? sqlite = null;
        var closeConnection = false;
        var previousTimeout = 0;
        try
        {
            if (db.Database.IsNpgsql())
            {
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {actorId.Value} FOR SHARE NOWAIT", ct);
            }
            else if (db.Database.IsSqlite())
            {
                sqlite = (SqliteConnection)db.Database.GetDbConnection();
                closeConnection = sqlite.State != ConnectionState.Open;
                previousTimeout = sqlite.DefaultTimeout;
                if (closeConnection) await db.Database.OpenConnectionAsync(ct);
                ct.ThrowIfCancellationRequested();
                sqlite.DefaultTimeout = 1;
                native = sqlite.BeginTransaction(IsolationLevel.Serializable, deferred: true);
                transaction = await db.Database.UseTransactionAsync(native, ct)
                    ?? throw new InvalidOperationException("Client read transaction was not enlisted.");
            }
            else throw new NotSupportedException("Client snapshots support SQLite and PostgreSQL only.");

            db.ChangeTracker.Clear();
            if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == actorId.Value && row.IsActive && row.IsApproved, ct))
                return new UnauthorizedResult();

            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            // Existing section rules still decide CanView and row visibility. The
            // generic trusted/background visibility resolver keeps its own contract.
            return await read();
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error))
        {
            return Conflict();
        }
        finally
        {
            try
            {
                if (transaction is not null)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await transaction.RollbackAsync(timeout.Token);
                    }
                    finally
                    {
                        db.ChangeTracker.Clear();
                        await transaction.DisposeAsync();
                    }
                }
            }
            finally
            {
                try { if (native is not null) await native.DisposeAsync(); }
                finally
                {
                    if (sqlite is not null) sqlite.DefaultTimeout = previousTimeout;
                    if (closeConnection) await db.Database.CloseConnectionAsync();
                }
            }
        }
    }

    private static ConflictObjectResult Conflict() => new(new
    {
        message = "Права доступа сейчас изменяются или данные заняты другой операцией. Обновите карточку или список клиентов.",
    });
}

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientPassportAccess
{
    public static Task<(SectionAccessContext? Access, IActionResult? Failure)> ReadAsync(PaymentContext db, ClaimsPrincipal? actor, CancellationToken ct)
        => ResolveAsync(db, actor, false, ct);

    private static async Task<(SectionAccessContext? Access, IActionResult? Failure)> ResolveAsync(PaymentContext db, ClaimsPrincipal? actor, bool write, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return (null, new UnauthorizedResult());
        var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
        if (access is null)
        {
            if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == actorId.Value && row.IsActive && row.IsApproved, ct)) return (null, new UnauthorizedResult());
            return (null, new ForbidResult());
        }
        bool Allowed(RolePermissionValues permission) => permission.CanView && (!write || permission.CanEdit);
        if (access.Permission is null)
        {
            // Keep role defaults, but pin the first configuration during a write.
            if (write && db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
            if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Clients, Allowed, ct)) return (null, new ForbidResult());
        }
        else if (!Allowed(access.Permission)) return (null, new ForbidResult());
        return (access, null);
    }

    public static async Task<IActionResult> WriteAsync(PaymentContext db, ClaimsPrincipal? actor, int clientId,
        Func<ClientPassport?, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var (access, failure) = await ResolveAsync(db, actor, true, ct);
            if (failure is not null) return failure;
            // A single payload lookup keeps the old primary-passport selection.
            var lookup = await db.Clients.ApplyClientVisibility(access!.Visibility).Where(client => client.Id == clientId)
                .Select(client => new
                {
                    client.ResponsibleUserId,
                    Passport = client.Passports.OrderByDescending(passport => passport.IsPrimary)
                        .ThenByDescending(passport => passport.UpdatedAt).ThenByDescending(passport => passport.CreatedAt).FirstOrDefault(),
                }).SingleOrDefaultAsync(ct);
            if (lookup is null) return new NotFoundResult();
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {clientId} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll && lookup.ResponsibleUserId is int owner)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {owner} FOR SHARE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientPassports\" WHERE \"ClientId\" = {clientId} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            }
            var result = await write(lookup.Passport);
            if (result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Паспорт, карточка клиента или права доступа изменены либо сейчас заняты. Обновите данные перед сохранением." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

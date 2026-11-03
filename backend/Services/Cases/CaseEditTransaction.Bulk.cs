using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Cases;

internal static partial class CaseEditTransaction
{
    public static async Task<IActionResult> RunManyAsync(PaymentContext db, ClaimsPrincipal actor, int[] ids,
        Func<List<ClientCase>, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor.GetUserId();
        if (actor.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct)) return new UnauthorizedResult();
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
            if (access?.Permission is not { CanView: true, CanEdit: true }) return new ForbidResult();
            if (ids.Length == 0) return new BadRequestObjectResult("Выберите дела для группового изменения.");
            if (ids.Length > 500) return new BadRequestObjectResult("За один раз можно изменить не более 500 дел. Уменьшите выборку.");
            var cases = await db.ClientCases.ApplyClientCaseVisibility(access.Visibility).Include(row => row.ResponsibleUser)
                .Where(row => ids.Contains(row.Id)).OrderBy(row => row.Id).ToListAsync(ct);
            if (cases.Count == 0) return new NotFoundResult();
            if (db.Database.IsNpgsql())
            {
                var visibleIds = cases.Select(row => row.Id).ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = ANY ({visibleIds}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll) await LockVisibleOwnersAsync(db, cases, ct);
            }
            var result = await write(cases, access);
            if (result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Дела или права доступа изменены либо сейчас заняты. Обновите список перед повторной попыткой." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static async Task LockVisibleOwnersAsync(PaymentContext db, IReadOnlyCollection<ClientCase> cases, CancellationToken ct)
    {
        var clients = cases.Where(row => row.ClientId.HasValue).Select(row => row.ClientId!.Value).Distinct().Order().ToArray();
        var legal = cases.Where(row => row.LegalEntityId.HasValue).Select(row => row.LegalEntityId!.Value).Distinct().Order().ToArray();
        var users = cases.Where(row => row.ResponsibleUserId.HasValue).Select(row => row.ResponsibleUserId!.Value).ToHashSet();
        if (clients.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = ANY ({clients}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            users.UnionWith(await db.Clients.AsNoTracking().Where(row => clients.Contains(row.Id) && row.ResponsibleUserId.HasValue).Select(row => row.ResponsibleUserId!.Value).ToArrayAsync(ct));
        }
        if (legal.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = ANY ({legal}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            users.UnionWith(await db.LegalEntities.AsNoTracking().Where(row => legal.Contains(row.Id) && row.ResponsibleUserId.HasValue).Select(row => row.ResponsibleUserId!.Value).ToArrayAsync(ct));
        }
        await LockUsersAsync(db, users.Order().ToArray(), ct);
    }

    public static async Task LockUsersAsync(PaymentContext db, int[] ids, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && ids.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
    }

    public static async Task LockUserAccessAsync(PaymentContext db, int id, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        await LockUsersAsync(db, [id], ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {id}) FOR SHARE NOWAIT", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {id}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserCompanyAccesses\" WHERE \"UserId\" = {id} ORDER BY \"LegalEntityId\" FOR SHARE NOWAIT", ct);
    }

    public static async Task LockLinkedTasksAsync(PaymentContext db, int[] ids, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && ids.Length > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalItems\" WHERE \"ClientCaseId\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
    }
}

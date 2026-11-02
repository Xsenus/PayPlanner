using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Cases;

internal static class CaseEditTransaction
{
    public static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal actor, int id,
        Func<ClientCase, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor.GetUserId();
        if (actor.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
            if (access?.Permission is not { CanView: true, CanEdit: true }) return new ForbidResult();
            var clientCase = await db.ClientCases.ApplyClientCaseVisibility(access.Visibility).FirstOrDefaultAsync(row => row.Id == id, ct);
            if (clientCase is null) return new NotFoundResult();
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll) await LockVisibleOwnersAsync(db, clientCase, ct);
            }
            var result = await write(clientCase, access);
            if (result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Дело или права доступа изменены либо сейчас заняты. Обновите карточку перед сохранением." });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    public static async Task LockVisibleOwnersAsync(PaymentContext db, ClientCase clientCase, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        var userIds = new HashSet<int>();
        if (clientCase.ResponsibleUserId is int assignee) userIds.Add(assignee);
        if (clientCase.ClientId is int clientId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {clientId} FOR SHARE NOWAIT", ct);
            var owner = await db.Clients.AsNoTracking().Where(row => row.Id == clientId).Select(row => row.ResponsibleUserId).FirstOrDefaultAsync(ct);
            if (owner is int userId) userIds.Add(userId);
        }
        if (clientCase.LegalEntityId is int legalId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legalId} FOR SHARE NOWAIT", ct);
            var owner = await db.LegalEntities.AsNoTracking().Where(row => row.Id == legalId).Select(row => row.ResponsibleUserId).FirstOrDefaultAsync(ct);
            if (owner is int userId) userIds.Add(userId);
        }
        if (userIds.Count > 0)
        {
            var ids = userIds.Order().ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
    }
}

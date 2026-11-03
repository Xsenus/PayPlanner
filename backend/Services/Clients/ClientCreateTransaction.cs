using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientCreateTransaction
{
    public static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal? actor, Client requested,
        Func<int, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == actorId.Value && row.IsActive && row.IsApproved, ct))
                    return new UnauthorizedResult();
                return new ForbidResult();
            }
            if (access.Permission is not { CanView: true, CanCreate: true })
            {
                if (access.Permission is not null) return new ForbidResult();
                // Existing roles with no configured permissions use the same
                // defaults as the original Create actions. Keep the approved-user
                // check and pin this role while a first permission is configured.
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Clients,
                    permission => permission.CanView && permission.CanCreate, ct)) return new ForbidResult();
            }
            var responsible = access.Visibility.IsAdmin ? requested.ResponsibleUserId ?? actorId.Value : actorId.Value;
            if (responsible <= 0) return new BadRequestObjectResult($"Ответственный #{responsible} не найден");
            var usersToLock = new HashSet<int> { responsible };
            if (requested.LegalEntityId is int legalId)
            {
                var legal = legalId > 0 ? await db.LegalEntities.AsNoTracking().ApplyLegalEntityVisibility(access.Visibility)
                    .Where(row => row.Id == legalId).Select(row => new { row.Id, row.ResponsibleUserId }).SingleOrDefaultAsync(ct) : null;
                if (legal is null) return new BadRequestObjectResult($"Юридическое лицо #{legalId} не найдено или недоступно");
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legalId} FOR SHARE NOWAIT", ct);
                if (!access.Visibility.SeesAll && legal.ResponsibleUserId is int ownerId) usersToLock.Add(ownerId);
            }
            if (requested.ClientStatusId is int statusId)
            {
                // Preserve the existing root rule: an existing inactive status is
                // still a valid explicit choice. Do not rewrite historical statuses.
                if (statusId <= 0 || !await db.ClientStatuses.AsNoTracking().AnyAsync(row => row.Id == statusId, ct))
                    return new BadRequestObjectResult($"Статус клиента #{statusId} не найден");
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientStatuses\" WHERE \"Id\" = {statusId} FOR SHARE NOWAIT", ct);
            }
            if (db.Database.IsNpgsql())
            {
                var ids = usersToLock.Order().ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            if (responsible != actorId.Value && !await db.Users.AsNoTracking().AnyAsync(row => row.Id == responsible && row.IsActive, ct))
                return new BadRequestObjectResult($"Ответственный #{responsible} не найден");
            var result = await write(responsible);
            if (result is CreatedResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Права доступа или связанные записи изменены либо сейчас заняты. Обновите данные перед созданием клиента." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

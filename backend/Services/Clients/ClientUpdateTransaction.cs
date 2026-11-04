using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientUpdateTransaction
{
    public static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal? actor, int id, Client requested,
        Func<Client, int?, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = requested.LegalEntityId is > 0
                ? await BalanceApplicationLease.BeginWithSettingsSnapshotAsync(db, actorId.Value, ct)
                : await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == actorId.Value && row.IsActive && row.IsApproved, ct))
                    return new UnauthorizedResult();
                return new ForbidResult();
            }
            if (access.Permission is not { CanView: true, CanEdit: true })
            {
                if (access.Permission is not null) return new ForbidResult();
                // Keep existing defaults for a role with no configured permissions,
                // while preventing its first permission from changing during this write.
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Clients,
                    permission => permission.CanView && permission.CanEdit, ct)) return new ForbidResult();
            }
            var client = await db.Clients.ApplyClientVisibility(access.Visibility).SingleOrDefaultAsync(row => row.Id == id, ct);
            if (client is null) return new NotFoundResult();
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);

            var responsible = access.Visibility.IsAdmin ? requested.ResponsibleUserId : client.ResponsibleUserId;
            var usersToLock = new HashSet<int>();
            if (responsible is int assignee) usersToLock.Add(assignee);
            if (!access.Visibility.SeesAll && client.ResponsibleUserId is int currentOwner) usersToLock.Add(currentOwner);
            // A known historical link can be retained without disclosing its
            // profile. Only a newly selected target needs separate view access.
            if (requested.LegalEntityId is int legalId && legalId != client.LegalEntityId)
            {
                var legal = await ClientLegalReferences.FindSelectionAsync(db, actor, legalId, ct);
                if (legal is null) return new BadRequestObjectResult($"Юридическое лицо #{legalId} не найдено или недоступно");
                if (legal.OwnerToPin is int legalOwner) usersToLock.Add(legalOwner);
            }
            if (requested.ClientStatusId is int statusId)
            {
                // Existing inactive statuses remain valid, as in the original root action.
                if (statusId <= 0 || !await db.ClientStatuses.AsNoTracking().AnyAsync(row => row.Id == statusId, ct))
                    return new BadRequestObjectResult($"Статус клиента #{statusId} не найден");
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientStatuses\" WHERE \"Id\" = {statusId} FOR SHARE NOWAIT", ct);
            }
            if (db.Database.IsNpgsql() && usersToLock.Count > 0)
            {
                var ids = usersToLock.Order().ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            if (responsible.HasValue && (responsible <= 0 || !await db.Users.AsNoTracking().AnyAsync(row => row.Id == responsible && row.IsActive, ct)))
                return new BadRequestObjectResult($"Ответственный #{responsible} не найден");
            var result = await write(client, responsible);
            if (result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Клиент, связанные записи или права доступа изменены либо сейчас заняты. Обновите карточку перед сохранением." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

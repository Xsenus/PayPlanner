using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal static class ClientBatchTransaction
{
    public static Task<IActionResult> DeleteAsync(PaymentContext db, ClaimsPrincipal? actor, int[] ids, bool single, CancellationToken ct)
        => RunAsync(db, actor, ids, null, single, ct);

    public static Task<IActionResult> AssignAsync(PaymentContext db, ClaimsPrincipal? actor, int[] ids, int assignee, CancellationToken ct)
        => RunAsync(db, actor, ids, assignee, false, ct);

    private static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal? actor, int[] ids, int? assignee, bool single, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        var deleting = !assignee.HasValue;
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await db.Users.AsNoTracking().AnyAsync(row => row.Id == actorId.Value && row.IsActive && row.IsApproved, ct)) return new UnauthorizedResult();
                return new ForbidResult();
            }
            bool Allowed(RolePermissionValues permission) => permission.CanView && (deleting ? permission.CanDelete : permission.CanEdit);
            if (access.Permission is null)
            {
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Clients, Allowed, ct)) return new ForbidResult();
            }
            else if (!Allowed(access.Permission)) return new ForbidResult();
            if (ids.Length == 0) return new BadRequestObjectResult(new { message = deleting ? "Не выбраны клиенты для удаления." : "Не выбраны клиенты для смены ответственного." });
            var clients = await db.Clients.ApplyClientVisibility(access.Visibility).Where(row => ids.Contains(row.Id)).OrderBy(row => row.Id).ToListAsync(ct);
            var found = clients.Select(row => row.Id).ToHashSet();
            var missingIds = ids.Where(id => !found.Contains(id)).ToList();
            if (missingIds.Count > 0)
            {
                if (single) return new NotFoundResult();
                return new NotFoundObjectResult(new { message = deleting ? "Не удалось найти часть клиентов для удаления." : "Не удалось найти часть клиентов для смены ответственного.", missingIds });
            }
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            var users = new HashSet<int>();
            if (!access.Visibility.SeesAll) foreach (var client in clients) if (client.ResponsibleUserId is int owner) users.Add(owner);
            if (assignee.HasValue) users.Add(assignee.Value);
            if (db.Database.IsNpgsql() && users.Count > 0)
            {
                var userIds = users.Order().ToArray();
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({userIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            if (deleting)
            {
                var usageById = await ClientDeletionRules.UsageAsync(db, ids, ct);
                if (single)
                {
                    var usage = usageById.GetValueOrDefault(ids[0]) ?? [];
                    if (usage.Count > 0) return new ConflictObjectResult(new { message = "Клиент используется в связанных данных и не может быть удалён.", usage });
                }
                else
                {
                    var blockedClients = clients.Where(client => usageById.GetValueOrDefault(client.Id)?.Count > 0)
                        .Select(client => new { id = client.Id, name = client.Name, usage = usageById[client.Id] }).ToList();
                    if (blockedClients.Count > 0) return new ConflictObjectResult(new { message = "Один или несколько клиентов используются в связанных данных и не могут быть удалены.", blockedClients });
                }
                await ClientDeletionRules.RemoveProfileAsync(db, ids, ct);
                db.Clients.RemoveRange(clients);
            }
            else
            {
                if (assignee <= 0 || !await db.Users.AsNoTracking().AnyAsync(row => row.Id == assignee && row.IsActive, ct))
                    return new BadRequestObjectResult(new { message = $"Ответственный #{assignee} не найден." });
                foreach (var client in clients) client.ResponsibleUserId = assignee;
            }
            await db.SaveChangesAsync(ct);
            await lease.CommitAsync(ct);
            if (deleting) return single ? new NoContentResult() : new OkObjectResult(new { deleted = clients.Count });
            return new OkObjectResult(new { updated = clients.Count, responsibleUserId = assignee });
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Клиенты, связанные записи или права доступа изменены либо сейчас заняты. Обновите список перед повторной операцией." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

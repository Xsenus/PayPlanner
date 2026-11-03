using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Clients;

internal enum ClientPowerOperation { View, Create, Edit, Delete }

internal static class ClientPowerOfAttorneyTransaction
{
    public static async Task<(SectionAccessContext? Access, IActionResult? Failure)> ResolveAsync(
        PaymentContext db, ClaimsPrincipal? actor, ClientPowerOperation operation, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return (null, new UnauthorizedResult());
        var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
        if (access is null) return (null, new ForbidResult());
        bool Allowed(RolePermissionValues permission) => permission.CanView && (operation switch
        {
            ClientPowerOperation.Create => permission.CanCreate,
            ClientPowerOperation.Edit => permission.CanEdit,
            ClientPowerOperation.Delete => permission.CanDelete,
            _ => true,
        });
        if (access.Permission is null)
        {
            // An initial permission configuration must not race a legacy-default write.
            if (operation != ClientPowerOperation.View && db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
            if (!await db.HasApprovedUserSectionPermissionAsync(actor, RolePermissionCatalog.Clients, Allowed, ct)) return (null, new ForbidResult());
        }
        else if (!Allowed(access.Permission)) return (null, new ForbidResult());
        return (access, null);
    }

    public static async Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal? actor, int clientId, int? id,
        ClientPowerOperation operation, Func<PowerOfAttorney?, Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor?.GetUserId();
        if (actor?.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            var (access, failure) = await ResolveAsync(db, actor, operation, ct);
            if (failure is not null) return failure;
            var clients = db.Clients.ApplyClientVisibility(access!.Visibility);
            int? owner;
            PowerOfAttorney? entity = null;
            if (operation == ClientPowerOperation.Create)
            {
                var parent = await clients.Where(client => client.Id == clientId).Select(client => new { client.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (parent is null) return new NotFoundResult();
                owner = parent.ResponsibleUserId;
            }
            else
            {
                // Resolve the child and its visible parent in one bounded lookup.
                var lookup = await (from power in db.PowersOfAttorney
                                    join client in clients on power.ClientId equals client.Id
                                    where power.ClientId == clientId && power.Id == id
                                    select new { Entity = power, client.ResponsibleUserId }).SingleOrDefaultAsync(ct);
                if (lookup is null) return new NotFoundResult();
                owner = lookup.ResponsibleUserId;
                entity = lookup.Entity;
            }
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {clientId} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll && owner.HasValue)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {owner.Value} FOR SHARE NOWAIT", ct);
                if (entity is not null)
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"PowersOfAttorney\" WHERE \"Id\" = {entity.Id} FOR UPDATE NOWAIT", ct);
            }
            var result = await write(entity);
            if (result is CreatedResult or OkObjectResult or NoContentResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Доверенность, карточка клиента или права доступа изменены либо сейчас заняты. Обновите данные перед повторной операцией." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

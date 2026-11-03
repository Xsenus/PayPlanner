using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private async Task<ActionResult<ContactDirectoryItemDto>> UpdateClientContactAtomicallyAsync(int id, int? legalId,
        Func<Client, LegalEntity?, Task<ActionResult<ContactDirectoryItemDto>>> write, CancellationToken ct)
    {
        var actorId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || actorId is not > 0) return Unauthorized();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(_db, actorId.Value, ct);
            var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
            if (access is null)
            {
                if (!await _db.Users.AsNoTracking().AnyAsync(x => x.Id == actorId.Value && x.IsActive && x.IsApproved, ct)) return Unauthorized();
                return Forbid();
            }
            if (access.Permission is null)
            {
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, p => p.CanView && p.CanEdit, ct)) return Forbid();
            }
            else if (!access.Permission.CanView || !access.Permission.CanEdit) return Forbid();

            var client = await _db.Clients.ApplyClientVisibility(access.Visibility)
                .Include(x => x.LegalEntity).Include(x => x.ResponsibleUser).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (client is null) return NotFound();
            if (_db.Database.IsNpgsql())
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
            var owners = new HashSet<int>();
            if (!access.Visibility.SeesAll && client.ResponsibleUserId is int owner) owners.Add(owner);

            LegalEntity? legal = null;
            if (legalId.HasValue)
            {
                // Organization visibility belongs to its own section, not Clients.
                var legalAccess = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.LegalEntities, ct, requireApprovedUser: true);
                if (legalAccess is not null && (legalAccess.Permission is not null
                    || await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.LegalEntities, p => p.CanView, ct)))
                    legal = await _db.LegalEntities.ApplyLegalEntityVisibility(legalAccess.Visibility).SingleOrDefaultAsync(x => x.Id == legalId.Value, ct);
                if (legal is null) return BadRequest("Указанное юридическое лицо не найдено.");
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legal.Id} FOR SHARE NOWAIT", ct);
                if (!legalAccess!.Visibility.SeesAll && legal.ResponsibleUserId is int legalOwner) owners.Add(legalOwner);
            }
            if (_db.Database.IsNpgsql() && owners.Count > 0)
            {
                var ids = owners.Order().ToArray();
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            var result = await write(client, legal);
            if (result.Result is OkObjectResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Карточка клиента, организация или права доступа изменены либо сейчас заняты. Обновите контакт перед сохранением." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Clients;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private async Task<ActionResult<ContactDirectoryItemDto>> PromoteClientContactAsync(int id, PromoteContactRequest request, CancellationToken ct)
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
                if (!await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, p => p.CanView && p.CanCreate, ct)) return Forbid();
            }
            else if (!access.Permission.CanView || !access.Permission.CanCreate) return Forbid();

            var entity = await BuildVisibleExternalContactsQuery(access.Visibility).Include(x => x.ResponsibleUser).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (entity is null) return NotFound();
            if (_db.Database.IsNpgsql())
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
            if (IsOrganizationContact(entity.ContactType)) return BadRequest("Организацию нужно оформить как юридическое лицо.");
            if (entity.LinkedClientId.HasValue) return Conflict("Для контакта уже создана полноценная карточка физического лица.");
            if (entity.LinkedLegalEntityId.HasValue) return Conflict("Для контакта уже создана полноценная карточка организации.");
            var owners = new HashSet<int>();
            if (entity.ResponsibleUserId is int owner)
            {
                if (entity.ResponsibleUser?.IsActive != true) return BadRequest("Ответственный контакта недоступен. Назначьте активного сотрудника перед созданием клиента.");
                owners.Add(owner);
            }
            if (entity.ParentContactId is int parentId)
            {
                var parent = await BuildVisibleExternalContactsQuery(access.Visibility).SingleOrDefaultAsync(x => x.Id == parentId, ct);
                if (parent is null) return BadRequest("Родительский контакт не найден или недоступен.");
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {parentId} FOR SHARE NOWAIT", ct);
                if (!access.Visibility.SeesAll && parent.ResponsibleUserId is int parentOwner) owners.Add(parentOwner);
                entity.ParentContact = parent;
            }
            if (entity.LegalEntityId is int legalId)
            {
                var legalAccess = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.LegalEntities, ct, requireApprovedUser: true);
                LegalEntity? legal = null;
                if (legalAccess is not null && (legalAccess.Permission is not null
                    || await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.LegalEntities, p => p.CanView, ct)))
                    legal = await _db.LegalEntities.ApplyLegalEntityVisibility(legalAccess.Visibility).SingleOrDefaultAsync(x => x.Id == legalId, ct);
                if (legal is null) return BadRequest("Организация контакта не найдена или недоступна.");
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legalId} FOR SHARE NOWAIT", ct);
                if (!legalAccess!.Visibility.SeesAll && legal.ResponsibleUserId is int legalOwner) owners.Add(legalOwner);
                entity.LegalEntity = legal;
            }
            var role = NormalizeRole(request.Role);
            var statusKey = string.Equals(role, "Supplier", StringComparison.Ordinal)
                ? ClientStatusSystemKeys.Supplier : ClientStatusSystemKeys.Client;
            var statusId = await _db.ClientStatuses.AsNoTracking().Where(x => x.SystemKey == statusKey).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
            if (_db.Database.IsNpgsql())
            {
                if (statusId.HasValue)
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientStatuses\" WHERE \"Id\" = {statusId.Value} FOR SHARE NOWAIT", ct);
                if (owners.Count > 0)
                {
                    var ids = owners.Order().ToArray();
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                }
            }
            var phones = DeserializeStringList(entity.PhonesJson, entity.Phone);
            var emails = DeserializeStringList(entity.EmailsJson, entity.Email);
            var client = new Client
            {
                Name = entity.FullName,
                Phone = phones.FirstOrDefault() ?? string.Empty,
                Email = emails.FirstOrDefault() ?? string.Empty,
                Company = entity.LegalEntity?.ShortName ?? entity.ParentContact?.FullName ?? string.Empty,
                LegalEntityId = entity.LegalEntityId,
                ResponsibleUserId = entity.ResponsibleUserId,
                ClientStatusId = statusId,
                IsClientAndSupplier = HasRole(entity.RolesJson, "Client") && string.Equals(role, "Supplier", StringComparison.Ordinal),
                IsActive = entity.IsActive,
                Notes = entity.Notes ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
            };
            var invalidDate = ClientBirthDateInput.Resolve(client, null, out var birthday);
            if (invalidDate is not null) return (ActionResult)invalidDate;
            client.BirthDate = birthday;
            _db.Clients.Add(client);
            entity.LinkedClient = client;
            entity.RolesJson = SerializeRoles(DeserializeRoles(entity.RolesJson).Append(role));
            entity.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            var reply = MapExternalContact(entity);
            await lease.CommitAsync(ct);
            return Ok(reply);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Контакт, связанные записи или права доступа изменены либо сейчас заняты. Обновите данные перед созданием клиента." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

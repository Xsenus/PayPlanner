using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private enum ExternalWrite { Create, Edit, Delete }

    private async Task<ActionResult> WriteExternalContactAsync(int? id, UpsertExternalContactRequest? model, ExternalWrite operation,
        Func<ExternalContact?, int?, Task<ActionResult>> write, CancellationToken ct)
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
            bool Allowed(RolePermissionValues p) => p.CanView && (operation switch { ExternalWrite.Create => p.CanCreate, ExternalWrite.Edit => p.CanEdit, _ => p.CanDelete });
            if (access.Permission is null)
            {
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {actorId.Value}) FOR UPDATE NOWAIT", ct);
                if (!await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, Allowed, ct)) return Forbid();
            }
            else if (!Allowed(access.Permission)) return Forbid();
            ExternalContact? entity = null;
            var owners = new HashSet<int>();
            if (id.HasValue)
            {
                var query = BuildVisibleExternalContactsQuery(access.Visibility);
                if (operation == ExternalWrite.Edit) query = query.Include(x => x.LegalEntity).Include(x => x.ParentContact).Include(x => x.ResponsibleUser);
                entity = await query.SingleOrDefaultAsync(x => x.Id == id.Value, ct);
                if (entity is null) return NotFound();
                if (_db.Database.IsNpgsql())
                    await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id.Value} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll && entity.ResponsibleUserId is int oldOwner) owners.Add(oldOwner);
            }
            int? responsible = null;
            if (operation == ExternalWrite.Delete)
            {
                // Even archived references retain their original contact identity.
                if (entity!.LinkedClientId.HasValue || entity.LinkedLegalEntityId.HasValue
                    || await _db.ExternalContacts.IgnoreQueryFilters().AnyAsync(x => x.ParentContactId == id, ct)
                    || await _db.ClientCaseRelatedParties.IgnoreQueryFilters().AnyAsync(x => x.ExternalContactId == id, ct))
                    return Conflict("Контакт используется в связанных данных. Вместо удаления отключите его активность.");
            }
            else
            {
                if (model!.LegalEntityId is int legalId)
                {
                    var legalAccess = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.LegalEntities, ct, requireApprovedUser: true);
                    LegalEntity? legal = null;
                    if (legalAccess is not null && (legalAccess.Permission is not null
                        || await _db.HasApprovedUserSectionPermissionAsync(User, RolePermissionCatalog.LegalEntities, p => p.CanView, ct)))
                        legal = await _db.LegalEntities.ApplyLegalEntityVisibility(legalAccess.Visibility).SingleOrDefaultAsync(x => x.Id == legalId, ct);
                    if (legal is null) return BadRequest("Указанное юридическое лицо не найдено.");
                    if (_db.Database.IsNpgsql())
                        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legalId} FOR SHARE NOWAIT", ct);
                    if (!legalAccess!.Visibility.SeesAll && legal.ResponsibleUserId is int legalOwner) owners.Add(legalOwner);
                }
                var contactType = NormalizeContactType(model.ContactType);
                if (string.IsNullOrWhiteSpace(model.FullName)) return BadRequest(IsOrganizationContact(contactType) ? "Укажите название организации." : "Укажите ФИО контакта.");
                if (!IsValidInn(contactType, model.Inn)) return BadRequest("ИНН организации должен содержать 10 или 12 цифр.");
                // Serializable predicate reads protect concurrent organization inserts/updates.
                if (IsOrganizationContact(contactType) && !string.IsNullOrWhiteSpace(model.Inn)
                    && await _db.ExternalContacts.AsNoTracking().AnyAsync(x => x.Id != id && x.Inn == model.Inn.Trim(), ct))
                    return Conflict("Контакт организации с таким ИНН уже существует.");
                var parent = await ResolveParentContactAsync(model.ParentContactId, access.Visibility, id, ct);
                if (model.ParentContactId.HasValue && parent is null) return BadRequest("Указанная организация-контакт не найдена.");
                if (IsOrganizationContact(contactType) && model.ParentContactId.HasValue) return BadRequest("Организацию нельзя привязать как сотрудника другой организации.");
                if (parent is not null)
                {
                    if (_db.Database.IsNpgsql())
                        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {parent.Id} FOR SHARE NOWAIT", ct);
                    if (!access.Visibility.SeesAll && parent.ResponsibleUserId is int parentOwner) owners.Add(parentOwner);
                }
                if (entity is not null && ((entity.LinkedClientId.HasValue && IsOrganizationContact(contactType))
                    || (entity.LinkedLegalEntityId.HasValue && !IsOrganizationContact(contactType))
                    || (!IsOrganizationContact(contactType) && IsOrganizationContact(entity.ContactType)
                        && await _db.ExternalContacts.IgnoreQueryFilters().AnyAsync(x => x.ParentContactId == id, ct))))
                    return Conflict("Тип контакта связан с полной карточкой или сотрудниками и не может быть изменён.");
                responsible = operation == ExternalWrite.Create
                    ? access.Visibility.IsAdmin ? model.ResponsibleUserId ?? actorId : actorId
                    : access.Visibility.IsAdmin ? model.ResponsibleUserId ?? entity!.ResponsibleUserId : entity!.ResponsibleUserId;
                if (responsible.HasValue) owners.Add(responsible.Value);
            }
            if (_db.Database.IsNpgsql() && owners.Count > 0)
            {
                var ids = owners.Order().ToArray();
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            }
            if (operation != ExternalWrite.Delete && !await ResponsibleUserExistsAsync(responsible, ct)) return BadRequest("Указанный ответственный не найден.");
            var result = await write(entity, responsible);
            if (result is CreatedResult or OkObjectResult or NoContentResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return Conflict(new { message = "Контакт, связанные записи или права доступа изменены либо сейчас заняты. Обновите данные перед повторной операцией." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}

using System.Data;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

public partial class ContactsController
{
    private sealed record MergePair(string SourceType, object Primary, object Duplicate);
    private sealed record MergeReference(string Table, string? Schema, string Column)
    {
        public string SqlTable => Schema is null
            ? QuoteIdentifier(Table)
            : $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Table)}";
        public string SqlColumn => QuoteIdentifier(Column);
    }

    public sealed class ContactMergePreview
    {
        public bool CanMerge { get; init; }
        public List<string> Conflicts { get; init; } = [];
        public int AffectedLinks { get; init; }
        public string PrimaryName { get; init; } = string.Empty;
        public string DuplicateName { get; init; } = string.Empty;
        public List<string> Phones { get; init; } = [];
        public List<string> Emails { get; init; } = [];
    }

    [HttpPost("merge/preview")]
    public async Task<ActionResult<ContactMergePreview>> PreviewMerge(
        [FromBody] ContactMergeRequest request,
        CancellationToken ct)
    {
        var (pair, error) = await ResolveMergePairAsync(request, ct);
        if (error is not null) return error;

        var conflicts = await GetMergeConflictsAsync(pair!, ct);
        var links = await CountMergeReferencesAsync(pair!, ct);
        return Ok(BuildMergePreview(pair!, conflicts, links));
    }

    [HttpPost("merge")]
    public async Task<ActionResult<ContactMergePreview>> MergeContacts(
        [FromBody] ContactMergeRequest request,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (pair, error) = await ResolveMergePairAsync(request, ct);
        if (error is not null) return error;

        var conflicts = await GetMergeConflictsAsync(pair!, ct);
        if (conflicts.Count > 0)
        {
            return Conflict(BuildMergePreview(pair!, conflicts, 0));
        }

        var links = await CountMergeReferencesAsync(pair!, ct);
        try
        {
            int? replacementFullProfileId = null;
            if (pair!.Primary is Client or LegalEntity && pair.Duplicate is ExternalContact externalDuplicate)
            {
                await MergeExternalIntoFullProfileAsync(pair, externalDuplicate, ct);
            }
            else if (pair.Primary is ExternalContact primaryQuick && pair.Duplicate is Client or LegalEntity)
            {
                replacementFullProfileId = await CreateReplacementFullProfileAsync(primaryQuick, pair.Duplicate, ct);
            }
            else if (pair.Primary is ExternalContact primaryExternal && pair.Duplicate is ExternalContact duplicateExternal)
            {
                var linkedClientId = !primaryExternal.LinkedClientId.HasValue ? duplicateExternal.LinkedClientId : null;
                var linkedLegalEntityId = !primaryExternal.LinkedLegalEntityId.HasValue ? duplicateExternal.LinkedLegalEntityId : null;
                if (linkedClientId.HasValue || linkedLegalEntityId.HasValue)
                {
                    if (linkedClientId.HasValue) duplicateExternal.LinkedClientId = null;
                    if (linkedLegalEntityId.HasValue) duplicateExternal.LinkedLegalEntityId = null;
                    await _db.SaveChangesAsync(ct);
                    primaryExternal.LinkedClientId = linkedClientId ?? primaryExternal.LinkedClientId;
                    primaryExternal.LinkedLegalEntityId = linkedLegalEntityId ?? primaryExternal.LinkedLegalEntityId;
                }
            }
            MergeContactFields(pair!);
            await _db.SaveChangesAsync(ct);

            // Keep one primary passport/address after the dependent rows move.
            // The selected card's primary value wins; all other rows are retained.
            if (pair.Primary is Client primaryClient && pair.Duplicate is Client duplicateClient)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ClientPassports\" SET \"IsPrimary\" = 0 WHERE \"ClientId\" = {duplicateClient.Id} AND EXISTS (SELECT 1 FROM \"ClientPassports\" WHERE \"ClientId\" = {primaryClient.Id} AND \"IsPrimary\" = 1)", ct);
                await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AddressRecords\" SET \"IsPrimary\" = 0 WHERE \"ClientId\" = {duplicateClient.Id} AND EXISTS (SELECT 1 FROM \"AddressRecords\" WHERE \"ClientId\" = {primaryClient.Id} AND \"IsPrimary\" = 1)", ct);
            }
            else if (pair.Primary is LegalEntity primaryLegal && pair.Duplicate is LegalEntity duplicateLegal)
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AddressRecords\" SET \"IsPrimary\" = 0 WHERE \"LegalEntityId\" = {duplicateLegal.Id} AND EXISTS (SELECT 1 FROM \"AddressRecords\" AS \"main\" WHERE \"main\".\"LegalEntityId\" = {primaryLegal.Id} AND \"main\".\"Type\" = \"AddressRecords\".\"Type\" AND \"main\".\"IsPrimary\" = 1)", ct);
            }

            var destinationId = replacementFullProfileId
                ?? (pair.Primary is Client or LegalEntity && pair.Duplicate is ExternalContact
                    ? await GetCanonicalExternalIdAsync(pair, ct)
                    : GetMergeId(pair.Primary));
            foreach (var reference in GetMergeReferences(pair!))
            {
                var sql = $"UPDATE {reference.SqlTable} SET {reference.SqlColumn} = {{0}} WHERE {reference.SqlColumn} = {{1}}";
                await _db.Database.ExecuteSqlRawAsync(sql, [destinationId, GetMergeId(pair.Duplicate)], ct);
            }
            if (replacementFullProfileId.HasValue && pair.Primary is ExternalContact selectedQuick)
            {
                if (pair.Duplicate is Client) selectedQuick.LinkedClientId = replacementFullProfileId;
                else selectedQuick.LinkedLegalEntityId = replacementFullProfileId;
            }

            // Mark the duplicate directly: Remove() would apply EF's client-side
            // SetNull/Cascade rules to any already tracked dependents, undoing the
            // foreign-key transfer that was just committed in this transaction.
            var archived = _db.Entry(pair!.Duplicate);
            archived.Property("IsDeleted").CurrentValue = true;
            archived.Property("DeletedAt").CurrentValue = DateTime.UtcNow;
            archived.Property("DeletedByUserId").CurrentValue = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var deletedById) ? deletedById : null;
            archived.Property("DeletedByUserName").CurrentValue = User.Identity?.Name;
            _db.ObjectAuditEvents.Add(new ObjectAuditEvent
            {
                ObjectType = "contact-merge",
                ObjectId = GetMergeId(pair.Primary),
                Action = "merged",
                Description = $"Объединены карточки #{GetMergeId(pair.Duplicate)} ({GetMergeSourceType(pair.Duplicate)}) и #{GetMergeId(pair.Primary)} ({GetMergeSourceType(pair.Primary)}); дубль помещён в архив.",
                ActorUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                ActorUserName = User.Identity?.Name,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    primarySourceType = GetMergeSourceType(pair.Primary),
                    primarySourceId = GetMergeId(pair.Primary),
                    duplicateSourceType = GetMergeSourceType(pair.Duplicate),
                    duplicateSourceId = GetMergeId(pair.Duplicate),
                    replacementFullProfileId,
                    affectedLinks = links,
                }),
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Ok(BuildMergePreview(pair, [], links));
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(ct);
            return Conflict(new { message = "Связи карточек конфликтуют. Данные не изменены; проверьте одинаковые договоры, профильные записи и связанные контакты." });
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return Conflict(new { message = "Не удалось безопасно перенести все связи. Данные не изменены; проверьте связанные карточки и повторите попытку." });
        }
        catch (InvalidOperationException mergeError)
        {
            await transaction.RollbackAsync(ct);
            return Conflict(new { message = mergeError.Message });
        }
    }

    private async Task<(MergePair? Pair, ActionResult? Error)> ResolveMergePairAsync(ContactMergeRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PrimarySourceType)
            || string.IsNullOrWhiteSpace(request.DuplicateSourceType)
            || request.PrimarySourceId <= 0 || request.DuplicateSourceId <= 0
            || (request.PrimarySourceId == request.DuplicateSourceId
                && string.Equals(request.PrimarySourceType, request.DuplicateSourceType, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, BadRequest(new { message = "Выберите две разные карточки контактов." }));
        }

        var sourceType = request.PrimarySourceType.Trim().ToLowerInvariant();
        var duplicateType = request.DuplicateSourceType.Trim().ToLowerInvariant();
        if (sourceType is not ("client" or "legal" or "external") || duplicateType is not ("client" or "legal" or "external"))
        {
            return (null, BadRequest(new { message = "Неизвестный вид контакта." }));
        }
        if (sourceType != duplicateType)
        {
            var fullType = sourceType == "external" ? duplicateType : sourceType;
            var quickId = sourceType == "external" ? request.PrimarySourceId : request.DuplicateSourceId;
            var fullId = sourceType == "external" ? request.DuplicateSourceId : request.PrimarySourceId;
            if (fullType is not ("client" or "legal") || sourceType != "external" && duplicateType != "external")
                return (null, BadRequest(new { message = "Человека нельзя объединить с организацией." }));
            var createsFullProfile = sourceType == "external";
            if (!await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Clients,
                    permission => permission.CanView && permission.CanEdit && permission.CanDelete
                        && (!createsFullProfile || permission.CanCreate), ct)
                || fullType == "legal" && !await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.LegalEntities,
                    permission => permission.CanView && permission.CanEdit && permission.CanDelete
                        && (!createsFullProfile || permission.CanCreate), ct))
                return (null, Forbid());
            var mixedAccess = await GetCurrentAccessAsync(ct);
            var external = await BuildVisibleExternalContactsQuery(mixedAccess)
                .FirstOrDefaultAsync(x => x.Id == quickId && x.IsActive, ct);
            if (external is null) return (null, NotFound());
            if (fullType == "client")
            {
                var client = await _db.Clients.ApplyClientVisibility(mixedAccess)
                    .FirstOrDefaultAsync(x => x.Id == fullId && x.IsActive, ct);
                if (client is null) return (null, NotFound());
                if (IsOrganizationContact(external.ContactType))
                    return (null, BadRequest(new { message = "Человека нельзя объединить с организацией." }));
                return (sourceType == "external"
                    ? new MergePair("external+client", external, client)
                    : new MergePair("client+external", client, external), null);
            }
            var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.LegalEntities, ct);
            var legal = await _db.LegalEntities.ApplyLegalEntityVisibility(visibility)
                .FirstOrDefaultAsync(x => x.Id == fullId, ct);
            if (legal is null) return (null, NotFound());
            if (!IsOrganizationContact(external.ContactType))
                return (null, BadRequest(new { message = "Человека нельзя объединить с организацией." }));
            return (sourceType == "external"
                ? new MergePair("external+legal", external, legal)
                : new MergePair("legal+external", legal, external), null);
        }

        var section = sourceType == "legal" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients;
        if (!await _db.HasUserSectionPermissionAsync(User, section,
                static permission => permission.CanView && permission.CanEdit && permission.CanDelete, ct))
        {
            return (null, Forbid());
        }

        if (sourceType == "legal")
        {
            var visibility = await _db.ResolveDataVisibilityAsync(User, section, ct);
            var records = await _db.LegalEntities.ApplyLegalEntityVisibility(visibility)
                .Where(x => x.Id == request.PrimarySourceId || x.Id == request.DuplicateSourceId)
                .ToListAsync(ct);
            if (records.Count != 2) return (null, NotFound());
            return (new MergePair(sourceType,
                records.Single(x => x.Id == request.PrimarySourceId),
                records.Single(x => x.Id == request.DuplicateSourceId)), null);
        }

        var access = await GetCurrentAccessAsync(ct);
        if (sourceType == "client")
        {
            var records = await _db.Clients.ApplyClientVisibility(access)
                .Where(x => (x.Id == request.PrimarySourceId || x.Id == request.DuplicateSourceId) && x.IsActive)
                .ToListAsync(ct);
            if (records.Count != 2) return (null, NotFound());
            return (new MergePair(sourceType,
                records.Single(x => x.Id == request.PrimarySourceId),
                records.Single(x => x.Id == request.DuplicateSourceId)), null);
        }

        var contacts = await BuildVisibleExternalContactsQuery(access)
            .Where(x => (x.Id == request.PrimarySourceId || x.Id == request.DuplicateSourceId) && x.IsActive)
            .ToListAsync(ct);
        if (contacts.Count != 2) return (null, NotFound());
        var primary = contacts.Single(x => x.Id == request.PrimarySourceId);
        var duplicate = contacts.Single(x => x.Id == request.DuplicateSourceId);
        if (IsOrganizationContact(primary.ContactType) != IsOrganizationContact(duplicate.ContactType))
        {
            return (null, BadRequest(new { message = "Человека нельзя объединить с организацией." }));
        }
        return (new MergePair(sourceType, primary, duplicate), null);
    }

    private async Task<List<string>> GetMergeConflictsAsync(MergePair pair, CancellationToken ct)
    {
        var conflicts = new List<string>();
        if (pair.Primary is Client primaryClient && pair.Duplicate is Client duplicateClient)
        {
            var primaryContractIds = _db.ClientContracts.Where(x => x.ClientId == primaryClient.Id).Select(x => x.ContractId);
            if (await _db.ClientContracts.AnyAsync(x => x.ClientId == duplicateClient.Id && primaryContractIds.Contains(x.ContractId), ct))
                conflicts.Add("У обеих карточек есть общий договор. Проверьте комментарии к договору перед объединением.");
            if (primaryClient.BirthDate.HasValue && duplicateClient.BirthDate.HasValue
                && primaryClient.BirthDate.Value.Date != duplicateClient.BirthDate.Value.Date)
                conflicts.Add("У клиентов различаются даты рождения. Проверьте, что это один человек.");
            if (primaryClient.LegalEntityId.HasValue && duplicateClient.LegalEntityId.HasValue
                && primaryClient.LegalEntityId != duplicateClient.LegalEntityId)
                conflicts.Add("Клиенты привязаны к разным организациям. Сначала проверьте эти связи.");
            if (await _db.ClientPortalAccounts.AnyAsync(x => x.ClientId == duplicateClient.Id, ct))
                conflicts.Add("У дубля есть доступ к клиентскому порталу. Сначала вручную проверьте и перенесите этот доступ.");
            if (await _db.ExternalContacts.CountAsync(x => x.LinkedClientId == primaryClient.Id || x.LinkedClientId == duplicateClient.Id, ct) > 1)
                conflicts.Add("Обе полные карточки связаны с разными быстрыми контактами. Сначала объедините эти контакты.");
        }
        else if (pair.Primary is LegalEntity primaryLegal && pair.Duplicate is LegalEntity duplicateLegal)
        {
            var primaryContractIds = _db.LegalEntityContracts.Where(x => x.LegalEntityId == primaryLegal.Id).Select(x => x.ContractId);
            if (await _db.LegalEntityContracts.AnyAsync(x => x.LegalEntityId == duplicateLegal.Id && primaryContractIds.Contains(x.ContractId), ct))
                conflicts.Add("У обеих организаций есть общий договор. Проверьте комментарии к договору перед объединением.");
            if (WouldOverflowMergedNotes(primaryLegal.Notes, duplicateLegal.Notes, duplicateLegal.Id, 1000))
                conflicts.Add("Заметки двух организаций не помещаются в основную карточку.");
            if (!string.IsNullOrWhiteSpace(primaryLegal.Inn) && !string.IsNullOrWhiteSpace(duplicateLegal.Inn)
                && !string.Equals(primaryLegal.Inn.Trim(), duplicateLegal.Inn.Trim(), StringComparison.Ordinal))
                conflicts.Add("У организаций различаются ИНН. Автоматическое объединение запрещено.");
            if (!string.IsNullOrWhiteSpace(primaryLegal.Kpp) && !string.IsNullOrWhiteSpace(duplicateLegal.Kpp)
                && !string.Equals(primaryLegal.Kpp.Trim(), duplicateLegal.Kpp.Trim(), StringComparison.Ordinal))
                conflicts.Add("У организаций различаются КПП. Проверьте, что это одно юридическое лицо.");
            if (await _db.ClientPortalAccounts.AnyAsync(x => x.LegalEntityId == duplicateLegal.Id, ct))
                conflicts.Add("У дубля есть доступ к клиентскому порталу. Сначала вручную проверьте и перенесите этот доступ.");
            if (await _db.UserCompanyAccesses.AnyAsync(x => x.LegalEntityId == duplicateLegal.Id, ct))
                conflicts.Add("Организация задаёт права доступа сотрудников. Её нельзя объединять без отдельной проверки прав.");
            if (await _db.ExternalContacts.CountAsync(x => x.LinkedLegalEntityId == primaryLegal.Id || x.LinkedLegalEntityId == duplicateLegal.Id, ct) > 1)
                conflicts.Add("Обе организации связаны с разными быстрыми контактами. Сначала объедините эти контакты.");
            var settings = await _db.SystemSettings.AsNoTracking()
                .Where(x => x.Key == "legalEntities.mainCompanyId" || x.Key == "legalEntities.mainCompanyIds" || x.Key == "legalEntities.defaultMainCompanyId")
                .Select(x => x.Value)
                .ToListAsync(ct);
            if (settings.Any(value => Regex.Matches(value ?? string.Empty, @"\d+")
                .Any(match => int.TryParse(match.Value, out var id) && id == duplicateLegal.Id)))
                conflicts.Add("Организация указана как основная компания CRM. Сначала измените настройку основной компании.");
        }
        else if (pair.Primary is ExternalContact primaryExternal && pair.Duplicate is ExternalContact duplicateExternal)
        {
            if (WouldOverflowMergedNotes(primaryExternal.Notes, duplicateExternal.Notes, duplicateExternal.Id, 2000))
                conflicts.Add("Заметки двух контактов не помещаются в основную карточку.");
            if (!string.IsNullOrWhiteSpace(primaryExternal.Inn) && !string.IsNullOrWhiteSpace(duplicateExternal.Inn)
                && !string.Equals(primaryExternal.Inn.Trim(), duplicateExternal.Inn.Trim(), StringComparison.Ordinal))
                conflicts.Add("У быстрых контактов различаются ИНН.");
            if (primaryExternal.LinkedClientId.HasValue && duplicateExternal.LinkedClientId.HasValue
                && primaryExternal.LinkedClientId != duplicateExternal.LinkedClientId)
                conflicts.Add("Каждый контакт связан со своей полной карточкой клиента. Сначала объедините полные карточки.");
            if (primaryExternal.LinkedLegalEntityId.HasValue && duplicateExternal.LinkedLegalEntityId.HasValue
                && primaryExternal.LinkedLegalEntityId != duplicateExternal.LinkedLegalEntityId)
                conflicts.Add("Каждый контакт связан со своей полной карточкой организации. Сначала объедините полные карточки.");
            if (primaryExternal.ParentContactId == duplicateExternal.Id)
                conflicts.Add("Основной контакт подчинён дублю. Сначала измените родительскую организацию.");
        }
        else if (pair.Duplicate is ExternalContact quick && pair.Primary is Client or LegalEntity)
        {
            if (pair.Primary is Client client && quick.LinkedClientId.HasValue && quick.LinkedClientId != client.Id
                || pair.Primary is LegalEntity legal && quick.LinkedLegalEntityId.HasValue && quick.LinkedLegalEntityId != legal.Id
                || pair.Primary is Client && quick.LinkedLegalEntityId.HasValue
                || pair.Primary is LegalEntity && quick.LinkedClientId.HasValue)
                conflicts.Add("Быстрый контакт уже связан с другой полной карточкой. Сначала проверьте и объедините полные карточки.");
            if (pair.Primary is Client mixedClient && mixedClient.LegalEntityId.HasValue
                && quick.LegalEntityId.HasValue && mixedClient.LegalEntityId != quick.LegalEntityId)
                conflicts.Add("Контакты привязаны к разным организациям. Сначала проверьте эти связи.");
            if (pair.Primary is LegalEntity mixedLegal)
            {
                if (!string.IsNullOrWhiteSpace(mixedLegal.Inn) && !string.IsNullOrWhiteSpace(quick.Inn)
                    && !string.Equals(mixedLegal.Inn.Trim(), quick.Inn.Trim(), StringComparison.Ordinal))
                    conflicts.Add("У организации и быстрого контакта различаются ИНН.");
                if (WouldOverflowMergedNotes(mixedLegal.Notes, quick.Notes, quick.Id, 1000))
                    conflicts.Add("Заметки не помещаются в основную карточку организации.");
            }
            var primaryId = GetMergeId(pair.Primary);
            var clientPrimary = pair.Primary is Client;
            var aliases = await _db.ExternalContacts.Where(x => x.Id != quick.Id
                && (clientPrimary ? x.LinkedClientId == primaryId : x.LinkedLegalEntityId == primaryId))
                .Take(2).ToListAsync(ct);
            if (aliases.Count > 1)
                conflicts.Add("Полная карточка связана с несколькими быстрыми контактами. Сначала разберите эти связи.");
            else if (aliases.Count == 1)
                conflicts.AddRange(await GetMergeConflictsAsync(new MergePair("external", aliases[0], quick), ct));
            if (aliases.Count == 0 && await _db.ExternalContacts.IgnoreQueryFilters()
                .AnyAsync(x => x.Id != quick.Id
                    && (clientPrimary ? x.LinkedClientId == primaryId : x.LinkedLegalEntityId == primaryId), ct))
                conflicts.Add("Полная карточка уже связана с архивным быстрым контактом. Сначала проверьте архивную запись.");
        }
        else if (pair.Primary is ExternalContact selectedQuick && pair.Duplicate is Client or LegalEntity)
        {
            var duplicateId = GetMergeId(pair.Duplicate);
            var clientDuplicate = pair.Duplicate is Client;
            if (selectedQuick.LinkedClientId.HasValue && (!clientDuplicate || selectedQuick.LinkedClientId != duplicateId)
                || selectedQuick.LinkedLegalEntityId.HasValue && (clientDuplicate || selectedQuick.LinkedLegalEntityId != duplicateId))
                conflicts.Add("Выбранный быстрый контакт уже связан с другой полной карточкой. Сначала проверьте эту связь.");
            if (await _db.ExternalContacts.IgnoreQueryFilters().AnyAsync(x => x.Id != selectedQuick.Id
                && (clientDuplicate ? x.LinkedClientId == duplicateId : x.LinkedLegalEntityId == duplicateId), ct))
                conflicts.Add("Архивируемая полная карточка связана с другим быстрым контактом. Сначала объедините или отвяжите его.");
            if (pair.Duplicate is Client oldClient)
            {
                if (oldClient.LegalEntityId.HasValue && selectedQuick.LegalEntityId.HasValue
                    && oldClient.LegalEntityId != selectedQuick.LegalEntityId)
                    conflicts.Add("Контакты привязаны к разным организациям. Сначала проверьте эти связи.");
                if (await _db.ClientPortalAccounts.AnyAsync(x => x.ClientId == oldClient.Id, ct))
                    conflicts.Add("У полной карточки есть доступ к клиентскому порталу. Сначала вручную проверьте и перенесите этот доступ.");
            }
            else if (pair.Duplicate is LegalEntity oldLegal)
            {
                if (!string.IsNullOrWhiteSpace(selectedQuick.Inn) && !string.IsNullOrWhiteSpace(oldLegal.Inn)
                    && !string.Equals(selectedQuick.Inn.Trim(), oldLegal.Inn.Trim(), StringComparison.Ordinal))
                    conflicts.Add("У организации и быстрого контакта различаются ИНН.");
                if (WouldOverflowMergedNotes(selectedQuick.Notes, oldLegal.Notes, oldLegal.Id, 1000))
                    conflicts.Add("Заметки не помещаются в новую полную карточку организации.");
                if (await _db.ClientPortalAccounts.AnyAsync(x => x.LegalEntityId == oldLegal.Id, ct))
                    conflicts.Add("У полной карточки есть доступ к клиентскому порталу. Сначала вручную проверьте и перенесите этот доступ.");
                if (await _db.UserCompanyAccesses.AnyAsync(x => x.LegalEntityId == oldLegal.Id, ct))
                    conflicts.Add("Организация задаёт права доступа сотрудников. Её нельзя архивировать без отдельной проверки прав.");
                var settings = await _db.SystemSettings.AsNoTracking()
                    .Where(x => x.Key == "legalEntities.mainCompanyId" || x.Key == "legalEntities.mainCompanyIds" || x.Key == "legalEntities.defaultMainCompanyId")
                    .Select(x => x.Value).ToListAsync(ct);
                if (settings.Any(value => Regex.Matches(value ?? string.Empty, @"\d+")
                    .Any(match => int.TryParse(match.Value, out var id) && id == oldLegal.Id)))
                    conflicts.Add("Организация указана как основная компания CRM. Сначала измените настройку основной компании.");
            }
        }
        return conflicts;
    }

    private static ContactMergePreview BuildMergePreview(MergePair pair, List<string> conflicts, int links)
    {
        var (primaryName, primaryPhones, primaryEmails) = GetMergeContactData(pair.Primary);
        var (duplicateName, duplicatePhones, duplicateEmails) = GetMergeContactData(pair.Duplicate);
        return new ContactMergePreview
        {
            CanMerge = conflicts.Count == 0,
            Conflicts = conflicts,
            AffectedLinks = links,
            PrimaryName = primaryName,
            DuplicateName = duplicateName,
            Phones = MergeStringLists(primaryPhones, duplicatePhones),
            Emails = MergeStringLists(primaryEmails, duplicateEmails),
        };
    }

    private static (string Name, List<string> Phones, List<string> Emails) GetMergeContactData(object source)
        => source switch
        {
            Client client => (client.Name,
                ReadProfileStringArray(GetOrCreateClientProfileObject(client.Notes), "phones", client.Phone),
                ReadProfileStringArray(GetOrCreateClientProfileObject(client.Notes), "emails", client.Email)),
            LegalEntity legal => (legal.ShortName,
                DeserializeStringList(legal.ContactPhonesJson, legal.Phone),
                DeserializeStringList(legal.ContactEmailsJson, legal.Email)),
            ExternalContact contact => (contact.FullName,
                DeserializeStringList(contact.PhonesJson, contact.Phone),
                DeserializeStringList(contact.EmailsJson, contact.Email)),
            _ => throw new InvalidOperationException("Неизвестный вид контакта."),
        };

    private static int GetMergeId(object source)
        => source switch
        {
            Client client => client.Id,
            LegalEntity legal => legal.Id,
            ExternalContact contact => contact.Id,
            _ => throw new InvalidOperationException("Неизвестный вид контакта."),
        };

    private static string GetMergeSourceType(object source) => source switch
    {
        Client => "client",
        LegalEntity => "legal",
        ExternalContact => "external",
        _ => throw new InvalidOperationException("Неизвестный вид контакта."),
    };

    private async Task<int> CreateReplacementFullProfileAsync(ExternalContact primary, object duplicate, CancellationToken ct)
    {
        var quickPhones = DeserializeStringList(primary.PhonesJson, primary.Phone);
        var quickEmails = DeserializeStringList(primary.EmailsJson, primary.Email);
        if (duplicate is Client oldClient)
        {
            var replacement = (Client)_db.Entry(oldClient).CurrentValues.ToObject();
            replacement.Id = 0;
            replacement.CreatedAt = DateTime.UtcNow;
            replacement.Name = primary.FullName;
            replacement.IsActive = true;
            replacement.LegalEntityId ??= primary.LegalEntityId;
            replacement.ResponsibleUserId ??= primary.ResponsibleUserId;
            var profile = GetOrCreateClientProfileObject(replacement.Notes);
            var phones = MergeStringLists(quickPhones, ReadProfileStringArray(profile, "phones", replacement.Phone));
            var emails = MergeStringLists(quickEmails, ReadProfileStringArray(profile, "emails", replacement.Email));
            SetStringArray(profile, "phones", phones);
            SetStringArray(profile, "emails", emails);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactPosition")))
                SetString(profile, "contactPosition", primary.Position);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactPowerOfAttorney")))
                SetString(profile, "contactPowerOfAttorney", primary.PowerOfAttorney);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactNotes")))
                SetString(profile, "contactNotes", primary.Notes);
            replacement.Phone = phones.FirstOrDefault() ?? string.Empty;
            replacement.Email = emails.FirstOrDefault() ?? string.Empty;
            replacement.Notes = SerializeClientProfileObject(profile);
            primary.Phone = replacement.Phone;
            primary.Email = replacement.Email;
            primary.PhonesJson = SerializeStringList(phones);
            primary.EmailsJson = SerializeStringList(emails);
            _db.Clients.Add(replacement);
            await _db.SaveChangesAsync(ct);
            return replacement.Id;
        }

        var oldLegal = (LegalEntity)duplicate;
        var legalReplacement = (LegalEntity)_db.Entry(oldLegal).CurrentValues.ToObject();
        legalReplacement.Id = 0;
        legalReplacement.CreatedAt = DateTime.UtcNow;
        legalReplacement.UpdatedAt = null;
        legalReplacement.ShortName = primary.FullName;
        legalReplacement.ResponsibleUserId ??= primary.ResponsibleUserId;
        legalReplacement.Inn = string.IsNullOrWhiteSpace(primary.Inn) ? legalReplacement.Inn : primary.Inn;
        var legalPhones = MergeStringLists(quickPhones,
            DeserializeStringList(legalReplacement.ContactPhonesJson, legalReplacement.Phone));
        var legalEmails = MergeStringLists(quickEmails,
            DeserializeStringList(legalReplacement.ContactEmailsJson, legalReplacement.Email));
        legalReplacement.Phone = legalPhones.FirstOrDefault();
        legalReplacement.Email = legalEmails.FirstOrDefault();
        legalReplacement.ContactPhonesJson = SerializeStringList(legalPhones);
        legalReplacement.ContactEmailsJson = SerializeStringList(legalEmails);
        legalReplacement.Notes = MergeNotes(primary.Notes, oldLegal.Notes, oldLegal.Id, 1000);
        primary.Phone = legalReplacement.Phone;
        primary.Email = legalReplacement.Email;
        primary.PhonesJson = SerializeStringList(legalPhones);
        primary.EmailsJson = SerializeStringList(legalEmails);
        _db.LegalEntities.Add(legalReplacement);
        await _db.SaveChangesAsync(ct);
        return legalReplacement.Id;
    }

    private async Task MergeExternalIntoFullProfileAsync(MergePair pair, ExternalContact duplicate, CancellationToken ct)
    {
        var primaryId = GetMergeId(pair.Primary);
        var clientPrimary = pair.Primary is Client;
        var canonical = await _db.ExternalContacts.FirstOrDefaultAsync(x => x.Id != duplicate.Id
            && (clientPrimary ? x.LinkedClientId == primaryId : x.LinkedLegalEntityId == primaryId), ct);
        if (canonical is null)
        {
            // A quick contact already linked to this profile occupies the unique
            // alias slot. Release it inside the transaction before creating the
            // canonical alias; the original row itself is archived below.
            if (clientPrimary && duplicate.LinkedClientId == primaryId)
                duplicate.LinkedClientId = null;
            if (!clientPrimary && duplicate.LinkedLegalEntityId == primaryId)
                duplicate.LinkedLegalEntityId = null;
            await _db.SaveChangesAsync(ct);
            canonical = new ExternalContact
            {
                FullName = pair.Primary is Client client ? client.Name : ((LegalEntity)pair.Primary).ShortName,
                ContactType = duplicate.ContactType,
                Inn = duplicate.Inn,
                RolesJson = duplicate.RolesJson,
                Phone = duplicate.Phone,
                Email = duplicate.Email,
                Position = duplicate.Position,
                PowerOfAttorney = duplicate.PowerOfAttorney,
                PhonesJson = duplicate.PhonesJson,
                EmailsJson = duplicate.EmailsJson,
                Notes = duplicate.Notes,
                LegalEntityId = duplicate.LegalEntityId,
                ParentContactId = duplicate.ParentContactId,
                LinkedClientId = clientPrimary ? primaryId : null,
                LinkedLegalEntityId = clientPrimary ? null : primaryId,
                ResponsibleUserId = duplicate.ResponsibleUserId,
                IsActive = duplicate.IsActive,
                SyncToPhonebook = duplicate.SyncToPhonebook,
                CreatedAt = DateTime.UtcNow,
            };
            _db.ExternalContacts.Add(canonical);
        }
        else
        {
            MergeContactFields(new MergePair("external", canonical, duplicate));
        }

        var duplicatePhones = DeserializeStringList(duplicate.PhonesJson, duplicate.Phone);
        var duplicateEmails = DeserializeStringList(duplicate.EmailsJson, duplicate.Email);
        if (pair.Primary is Client primaryClient)
        {
            var profile = GetOrCreateClientProfileObject(primaryClient.Notes);
            var phones = MergeStringLists(ReadProfileStringArray(profile, "phones", primaryClient.Phone), duplicatePhones);
            var emails = MergeStringLists(ReadProfileStringArray(profile, "emails", primaryClient.Email), duplicateEmails);
            SetStringArray(profile, "phones", phones);
            SetStringArray(profile, "emails", emails);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactPosition")))
                SetString(profile, "contactPosition", duplicate.Position);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactPowerOfAttorney")))
                SetString(profile, "contactPowerOfAttorney", duplicate.PowerOfAttorney);
            if (string.IsNullOrWhiteSpace(ReadProfileString(profile, "contactNotes")))
                SetString(profile, "contactNotes", duplicate.Notes);
            primaryClient.Phone = phones.FirstOrDefault() ?? string.Empty;
            primaryClient.Email = emails.FirstOrDefault() ?? string.Empty;
            primaryClient.LegalEntityId ??= duplicate.LegalEntityId;
            primaryClient.ResponsibleUserId ??= duplicate.ResponsibleUserId;
            primaryClient.Notes = SerializeClientProfileObject(profile);
        }
        else if (pair.Primary is LegalEntity primaryLegal)
        {
            var phones = MergeStringLists(DeserializeStringList(primaryLegal.ContactPhonesJson, primaryLegal.Phone), duplicatePhones);
            var emails = MergeStringLists(DeserializeStringList(primaryLegal.ContactEmailsJson, primaryLegal.Email), duplicateEmails);
            primaryLegal.Phone = phones.FirstOrDefault();
            primaryLegal.Email = emails.FirstOrDefault();
            primaryLegal.ContactPhonesJson = SerializeStringList(phones);
            primaryLegal.ContactEmailsJson = SerializeStringList(emails);
            primaryLegal.Inn ??= duplicate.Inn;
            primaryLegal.ResponsibleUserId ??= duplicate.ResponsibleUserId;
            primaryLegal.Notes = MergeNotes(primaryLegal.Notes, duplicate.Notes, duplicate.Id, 1000);
            primaryLegal.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<int> GetCanonicalExternalIdAsync(MergePair pair, CancellationToken ct)
    {
        var primaryId = GetMergeId(pair.Primary);
        return pair.Primary is Client
            ? await _db.ExternalContacts.Where(x => x.Id != GetMergeId(pair.Duplicate) && x.LinkedClientId == primaryId)
                .Select(x => x.Id).SingleAsync(ct)
            : await _db.ExternalContacts.Where(x => x.Id != GetMergeId(pair.Duplicate) && x.LinkedLegalEntityId == primaryId)
                .Select(x => x.Id).SingleAsync(ct);
    }

    private void MergeContactFields(MergePair pair)
    {
        if (pair.Primary is Client primaryClient && pair.Duplicate is Client duplicateClient)
        {
            var primaryProfile = GetOrCreateClientProfileObject(primaryClient.Notes);
            var duplicateProfile = GetOrCreateClientProfileObject(duplicateClient.Notes);
            var phones = MergeStringLists(ReadProfileStringArray(primaryProfile, "phones", primaryClient.Phone),
                ReadProfileStringArray(duplicateProfile, "phones", duplicateClient.Phone));
            var emails = MergeStringLists(ReadProfileStringArray(primaryProfile, "emails", primaryClient.Email),
                ReadProfileStringArray(duplicateProfile, "emails", duplicateClient.Email));
            SetStringArray(primaryProfile, "phones", phones);
            SetStringArray(primaryProfile, "emails", emails);
            foreach (var field in duplicateProfile)
            {
                if (field.Key is "phones" or "emails" || primaryProfile.ContainsKey(field.Key)) continue;
                primaryProfile[field.Key] = field.Value?.DeepClone();
            }
            primaryClient.Phone = phones.FirstOrDefault() ?? string.Empty;
            primaryClient.Email = emails.FirstOrDefault() ?? string.Empty;
            primaryClient.Company = string.IsNullOrWhiteSpace(primaryClient.Company) ? duplicateClient.Company : primaryClient.Company;
            primaryClient.Address = string.IsNullOrWhiteSpace(primaryClient.Address) ? duplicateClient.Address : primaryClient.Address;
            primaryClient.BirthDate ??= duplicateClient.BirthDate;
            primaryClient.LegalEntityId ??= duplicateClient.LegalEntityId;
            primaryClient.ClientStatusId ??= duplicateClient.ClientStatusId;
            primaryClient.ResponsibleUserId ??= duplicateClient.ResponsibleUserId;
            primaryClient.IsClientAndSupplier |= duplicateClient.IsClientAndSupplier;
            primaryClient.Notes = SerializeClientProfileObject(primaryProfile);
        }
        else if (pair.Primary is LegalEntity primaryLegal && pair.Duplicate is LegalEntity duplicateLegal)
        {
            var phones = MergeStringLists(DeserializeStringList(primaryLegal.ContactPhonesJson, primaryLegal.Phone),
                DeserializeStringList(duplicateLegal.ContactPhonesJson, duplicateLegal.Phone));
            var emails = MergeStringLists(DeserializeStringList(primaryLegal.ContactEmailsJson, primaryLegal.Email),
                DeserializeStringList(duplicateLegal.ContactEmailsJson, duplicateLegal.Email));
            primaryLegal.Phone = phones.FirstOrDefault();
            primaryLegal.Email = emails.FirstOrDefault();
            primaryLegal.ContactPhonesJson = SerializeStringList(phones);
            primaryLegal.ContactEmailsJson = SerializeStringList(emails);
            primaryLegal.Inn ??= duplicateLegal.Inn;
            primaryLegal.Kpp ??= duplicateLegal.Kpp;
            primaryLegal.Ogrn ??= duplicateLegal.Ogrn;
            primaryLegal.FullName ??= duplicateLegal.FullName;
            primaryLegal.Address ??= duplicateLegal.Address;
            primaryLegal.Director ??= duplicateLegal.Director;
            primaryLegal.ClientStatusId ??= duplicateLegal.ClientStatusId;
            primaryLegal.ResponsibleUserId ??= duplicateLegal.ResponsibleUserId;
            primaryLegal.IsClientAndSupplier |= duplicateLegal.IsClientAndSupplier;
            primaryLegal.Notes = MergeNotes(primaryLegal.Notes, duplicateLegal.Notes, duplicateLegal.Id, 1000);
            primaryLegal.UpdatedAt = DateTime.UtcNow;
        }
        else if (pair.Primary is ExternalContact primaryContact && pair.Duplicate is ExternalContact duplicateContact)
        {
            var phones = MergeStringLists(DeserializeStringList(primaryContact.PhonesJson, primaryContact.Phone),
                DeserializeStringList(duplicateContact.PhonesJson, duplicateContact.Phone));
            var emails = MergeStringLists(DeserializeStringList(primaryContact.EmailsJson, primaryContact.Email),
                DeserializeStringList(duplicateContact.EmailsJson, duplicateContact.Email));
            primaryContact.Phone = phones.FirstOrDefault();
            primaryContact.Email = emails.FirstOrDefault();
            primaryContact.PhonesJson = SerializeStringList(phones);
            primaryContact.EmailsJson = SerializeStringList(emails);
            primaryContact.RolesJson = SerializeRoles(DeserializeRoles(primaryContact.RolesJson)
                .Concat(DeserializeRoles(duplicateContact.RolesJson)));
            primaryContact.Inn ??= duplicateContact.Inn;
            primaryContact.Position ??= duplicateContact.Position;
            primaryContact.PowerOfAttorney ??= duplicateContact.PowerOfAttorney;
            primaryContact.LegalEntityId ??= duplicateContact.LegalEntityId;
            primaryContact.ResponsibleUserId ??= duplicateContact.ResponsibleUserId;
            if (!primaryContact.ParentContactId.HasValue && duplicateContact.ParentContactId != primaryContact.Id)
                primaryContact.ParentContactId = duplicateContact.ParentContactId;
            primaryContact.SyncToPhonebook |= duplicateContact.SyncToPhonebook;
            primaryContact.Notes = MergeNotes(primaryContact.Notes, duplicateContact.Notes, duplicateContact.Id, 2000);
            primaryContact.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static string? MergeNotes(string? primary, string? duplicate, int duplicateId, int limit)
    {
        if (string.IsNullOrWhiteSpace(duplicate) || string.Equals(primary?.Trim(), duplicate.Trim(), StringComparison.Ordinal))
            return primary;
        var next = string.IsNullOrWhiteSpace(primary)
            ? duplicate.Trim()
            : $"{primary.Trim()}\n\nИз объединённой карточки #{duplicateId}: {duplicate.Trim()}";
        if (next.Length > limit)
            throw new InvalidOperationException("Заметки двух карточек не помещаются в основную карточку.");
        return next;
    }

    private static bool WouldOverflowMergedNotes(string? primary, string? duplicate, int duplicateId, int limit)
    {
        if (string.IsNullOrWhiteSpace(duplicate) || string.Equals(primary?.Trim(), duplicate.Trim(), StringComparison.Ordinal))
            return false;
        var nextLength = string.IsNullOrWhiteSpace(primary)
            ? duplicate.Trim().Length
            : $"{primary.Trim()}\n\nИз объединённой карточки #{duplicateId}: {duplicate.Trim()}".Length;
        return nextLength > limit;
    }

    private IReadOnlyList<MergeReference> GetMergeReferences(MergePair pair)
    {
        var principalType = pair.Duplicate.GetType();
        return _db.Model.GetEntityTypes()
            .SelectMany(entityType => entityType.GetForeignKeys()
                .Where(foreignKey => foreignKey.PrincipalEntityType.ClrType == principalType
                    && foreignKey.Properties.Count == 1
                    && foreignKey.PrincipalKey.Properties.Count == 1
                    && foreignKey.PrincipalKey.Properties[0].Name == "Id")
                .Select(foreignKey =>
                {
                    var table = entityType.GetTableName();
                    if (table is null) return null;
                    var schema = entityType.GetSchema();
                    var column = foreignKey.Properties[0].GetColumnName(StoreObjectIdentifier.Table(table, schema));
                    return column is null ? null : new MergeReference(table, schema, column);
                }))
            .OfType<MergeReference>()
            .Distinct()
            .ToList();
    }

    private async Task<int> CountMergeReferencesAsync(MergePair pair, CancellationToken ct)
    {
        var references = GetMergeReferences(pair);
        if (references.Count == 0) return 0;
        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + string.Join(" + ", references.Select(reference =>
                $"(SELECT COUNT(*) FROM {reference.SqlTable} WHERE {reference.SqlColumn} = @duplicateId)"));
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@duplicateId";
            parameter.Value = GetMergeId(pair.Duplicate);
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private static string QuoteIdentifier(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

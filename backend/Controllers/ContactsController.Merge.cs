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
            if (pair!.Primary is ExternalContact primaryExternal && pair.Duplicate is ExternalContact duplicateExternal)
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

            foreach (var reference in GetMergeReferences(pair!))
            {
                var sql = $"UPDATE {reference.SqlTable} SET {reference.SqlColumn} = {{0}} WHERE {reference.SqlColumn} = {{1}}";
                await _db.Database.ExecuteSqlRawAsync(sql, [GetMergeId(pair!.Primary), GetMergeId(pair.Duplicate)], ct);
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
                Description = $"Объединены карточки {pair.SourceType} #{GetMergeId(pair.Duplicate)} и #{GetMergeId(pair.Primary)}; дубль помещён в архив.",
                ActorUserId = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                ActorUserName = User.Identity?.Name,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    primarySourceType = pair.SourceType,
                    primarySourceId = GetMergeId(pair.Primary),
                    duplicateSourceType = pair.SourceType,
                    duplicateSourceId = GetMergeId(pair.Duplicate),
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
            || request.PrimarySourceId == request.DuplicateSourceId
            || !string.Equals(request.PrimarySourceType, request.DuplicateSourceType, StringComparison.OrdinalIgnoreCase))
        {
            return (null, BadRequest(new { message = "Выберите две разные карточки одного вида. Быстрый контакт сначала можно оформить как полную карточку." }));
        }

        var sourceType = request.PrimarySourceType.Trim().ToLowerInvariant();
        if (sourceType is not ("client" or "legal" or "external"))
        {
            return (null, BadRequest(new { message = "Неизвестный вид контакта." }));
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
        var principalType = pair.Primary.GetType();
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

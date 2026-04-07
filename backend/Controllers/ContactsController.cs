using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/contacts")]
[Authorize]
public class ContactsController : ControllerBase
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";
    private const string YealinkImportNote = "Импортировано из Yealink RemotePhonebook.xml";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding Windows1251Encoding = CreateWindows1251Encoding();

    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;

    public ContactsController(PaymentContext db, IFormRequirementsService formRequirements)
    {
        _db = db;
        _formRequirements = formRequirements;
    }

    [HttpGet]
    public async Task<ActionResult<List<ContactDirectoryItemDto>>> GetAll(
        [FromQuery] string? scope,
        [FromQuery] string? search,
        [FromQuery] int? legalEntityId,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool? syncOnly = null,
        CancellationToken ct = default)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var normalizedScope = NormalizeScope(scope);
        var items = new List<ContactDirectoryItemDto>();

        if (!string.Equals(normalizedScope, "external", StringComparison.Ordinal))
        {
            var clientsQuery = _db.Clients
                .ApplyClientVisibility(access.IsAdmin, access.UserId)
                .Include(x => x.LegalEntity)
                .Include(x => x.ResponsibleUser)
                .AsNoTracking();

            if (legalEntityId.HasValue)
            {
                clientsQuery = clientsQuery.Where(x => x.LegalEntityId == legalEntityId.Value);
            }

            if (!includeInactive)
            {
                clientsQuery = clientsQuery.Where(x => x.IsActive);
            }

            var clients = await clientsQuery.ToListAsync(ct);
            items.AddRange(clients.Select(MapClientContact));
        }

        if (!string.Equals(normalizedScope, "clients", StringComparison.Ordinal))
        {
            var externalQuery = BuildVisibleExternalContactsQuery(access.UserId, access.IsAdmin)
                .Include(x => x.LegalEntity)
                .Include(x => x.ResponsibleUser)
                .AsNoTracking();

            if (legalEntityId.HasValue)
            {
                externalQuery = externalQuery.Where(x => x.LegalEntityId == legalEntityId.Value);
            }

            if (!includeInactive)
            {
                externalQuery = externalQuery.Where(x => x.IsActive);
            }

            var externalContacts = await externalQuery.ToListAsync(ct);
            items.AddRange(externalContacts.Select(MapExternalContact));
        }

        if (syncOnly.HasValue)
        {
            items = items.Where(x => x.SyncToPhonebook == syncOnly.Value).ToList();
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            items = items.Where(item =>
                    (item.FullName?.ToLowerInvariant().Contains(term) ?? false) ||
                    (item.OrganizationName?.ToLowerInvariant().Contains(term) ?? false) ||
                    (item.Position?.ToLowerInvariant().Contains(term) ?? false) ||
                    (item.PowerOfAttorney?.ToLowerInvariant().Contains(term) ?? false) ||
                    (item.Phone?.ToLowerInvariant().Contains(term) ?? false) ||
                    (item.Email?.ToLowerInvariant().Contains(term) ?? false) ||
                    item.Phones.Any(phone => phone.ToLowerInvariant().Contains(term)) ||
                    item.Emails.Any(email => email.ToLowerInvariant().Contains(term)))
                .ToList();
        }

        items = items
            .OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.OrganizationName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Ok(items);
    }

    [HttpPut("clients/{id:int}")]
    public async Task<ActionResult<ContactDirectoryItemDto>> UpdateClientContact(
        int id,
        [FromBody] UpdateClientContactRequest request,
        CancellationToken ct)
    {
        var missing = await _formRequirements.GetMissingFieldsAsync("contacts-client", request, ct);
        if (missing.Count > 0)
        {
            return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missing });
        }

        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await _db.Clients
            .ApplyClientVisibility(access.IsAdmin, access.UserId)
            .Include(x => x.LegalEntity)
            .Include(x => x.ResponsibleUser)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (client is null)
        {
            return NotFound();
        }

        LegalEntity? legalEntity = null;
        if (request.LegalEntityId.HasValue)
        {
            legalEntity = await _db.LegalEntities.FirstOrDefaultAsync(x => x.Id == request.LegalEntityId.Value, ct);
            if (legalEntity is null)
            {
                return BadRequest("Указанное юридическое лицо не найдено.");
            }
        }

        var normalizedPhones = NormalizeStringList(request.Phones, request.Phone);
        var normalizedEmails = NormalizeStringList(request.Emails, request.Email);

        client.Name = request.FullName.Trim();
        client.Phone = normalizedPhones.FirstOrDefault() ?? string.Empty;
        client.Email = normalizedEmails.FirstOrDefault() ?? string.Empty;
        client.LegalEntityId = request.LegalEntityId;
        client.Company = legalEntity?.ShortName
            ?? (client.LegalEntityId.HasValue ? string.Empty : client.Company);

        var profile = GetOrCreateClientProfileObject(client.Notes);
        SetStringArray(profile, "phones", normalizedPhones);
        SetStringArray(profile, "emails", normalizedEmails);
        SetString(profile, "contactPosition", request.Position);
        SetString(profile, "contactPowerOfAttorney", request.PowerOfAttorney);
        SetString(profile, "contactNotes", request.Notes);
        profile["syncToPhonebook"] = request.SyncToPhonebook;
        client.Notes = SerializeClientProfileObject(profile);

        await _db.SaveChangesAsync(ct);

        await _db.Entry(client).Reference(x => x.LegalEntity).LoadAsync(ct);
        await _db.Entry(client).Reference(x => x.ResponsibleUser).LoadAsync(ct);

        return Ok(MapClientContact(client));
    }

    [HttpPost("external")]
    public async Task<ActionResult<ContactDirectoryItemDto>> CreateExternalContact(
        [FromBody] UpsertExternalContactRequest request,
        CancellationToken ct)
    {
        var missing = await _formRequirements.GetMissingFieldsAsync("contacts-external", request, ct);
        if (missing.Count > 0)
        {
            return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missing });
        }

        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var legalEntity = await ResolveLegalEntityAsync(request.LegalEntityId, ct);
        if (request.LegalEntityId.HasValue && legalEntity is null)
        {
            return BadRequest("Указанное юридическое лицо не найдено.");
        }

        var responsibleUserId = access.IsAdmin ? request.ResponsibleUserId ?? access.UserId.Value : access.UserId.Value;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден.");
        }

        var normalizedPhones = NormalizeStringList(request.Phones, request.Phone);
        var normalizedEmails = NormalizeStringList(request.Emails, request.Email);
        var now = DateTime.UtcNow;

        var entity = new ExternalContact
        {
            FullName = request.FullName.Trim(),
            Phone = normalizedPhones.FirstOrDefault(),
            Email = normalizedEmails.FirstOrDefault(),
            PhonesJson = SerializeStringList(normalizedPhones),
            EmailsJson = SerializeStringList(normalizedEmails),
            Position = NormalizeOptional(request.Position),
            PowerOfAttorney = NormalizeOptional(request.PowerOfAttorney),
            Notes = NormalizeOptional(request.Notes),
            LegalEntityId = request.LegalEntityId,
            ResponsibleUserId = responsibleUserId,
            IsActive = request.IsActive,
            SyncToPhonebook = request.SyncToPhonebook,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.ExternalContacts.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Reference(x => x.LegalEntity).LoadAsync(ct);
        await _db.Entry(entity).Reference(x => x.ResponsibleUser).LoadAsync(ct);

        return Created($"/api/contacts/external/{entity.Id}", MapExternalContact(entity));
    }

    [HttpPut("external/{id:int}")]
    public async Task<ActionResult<ContactDirectoryItemDto>> UpdateExternalContact(
        int id,
        [FromBody] UpsertExternalContactRequest request,
        CancellationToken ct)
    {
        var missing = await _formRequirements.GetMissingFieldsAsync("contacts-external", request, ct);
        if (missing.Count > 0)
        {
            return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missing });
        }

        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var entity = await BuildVisibleExternalContactsQuery(access.UserId, access.IsAdmin)
            .Include(x => x.LegalEntity)
            .Include(x => x.ResponsibleUser)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var legalEntity = await ResolveLegalEntityAsync(request.LegalEntityId, ct);
        if (request.LegalEntityId.HasValue && legalEntity is null)
        {
            return BadRequest("Указанное юридическое лицо не найдено.");
        }

        var responsibleUserId = access.IsAdmin ? request.ResponsibleUserId ?? entity.ResponsibleUserId : entity.ResponsibleUserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден.");
        }

        var normalizedPhones = NormalizeStringList(request.Phones, request.Phone);
        var normalizedEmails = NormalizeStringList(request.Emails, request.Email);

        entity.FullName = request.FullName.Trim();
        entity.Phone = normalizedPhones.FirstOrDefault();
        entity.Email = normalizedEmails.FirstOrDefault();
        entity.PhonesJson = SerializeStringList(normalizedPhones);
        entity.EmailsJson = SerializeStringList(normalizedEmails);
        entity.Position = NormalizeOptional(request.Position);
        entity.PowerOfAttorney = NormalizeOptional(request.PowerOfAttorney);
        entity.Notes = NormalizeOptional(request.Notes);
        entity.LegalEntityId = request.LegalEntityId;
        entity.ResponsibleUserId = responsibleUserId;
        entity.IsActive = request.IsActive;
        entity.SyncToPhonebook = request.SyncToPhonebook;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await _db.Entry(entity).Reference(x => x.LegalEntity).LoadAsync(ct);
        await _db.Entry(entity).Reference(x => x.ResponsibleUser).LoadAsync(ct);

        return Ok(MapExternalContact(entity));
    }

    [HttpDelete("external/{id:int}")]
    public async Task<IActionResult> DeleteExternalContact(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var entity = await BuildVisibleExternalContactsQuery(access.UserId, access.IsAdmin)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        _db.ExternalContacts.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("exports/yealink")]
    public async Task<IActionResult> DownloadYealinkXml(CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var clients = await _db.Clients
            .Include(x => x.LegalEntity)
            .Include(x => x.ResponsibleUser)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        var externalContacts = await _db.ExternalContacts
            .Include(x => x.LegalEntity)
            .Include(x => x.ResponsibleUser)
            .AsNoTracking()
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        var clientContacts = clients.Select(MapClientContact)
            .Where(x => x.SyncToPhonebook)
            .OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var externalItems = externalContacts.Select(MapExternalContact)
            .Where(x => x.SyncToPhonebook)
            .OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("YealinkIPPhoneBook",
                new XElement("Title", "PayPlanner"),
                BuildYealinkMenu("КЛИЕНТЫ", clientContacts),
                BuildYealinkMenu("СТОРОННИЕ", externalItems)));

        var bytes = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        return File(bytes, "text/xml; charset=utf-8", "RemotePhonebook.xml");
    }

    [HttpPost("imports/yealink")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<YealinkContactImportResultDto>> ImportYealinkXml(
        IFormFile? file,
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!access.IsAdmin)
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest("Файл телефонной книги пустой.");
        }

        XDocument document;
        try
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var xml = await reader.ReadToEndAsync(ct);
            document = XDocument.Parse(xml);
        }
        catch
        {
            return BadRequest("Не удалось прочитать XML телефонной книги Yealink.");
        }

        var result = new YealinkContactImportResultDto
        {
            DryRun = dryRun,
        };
        var existingPhones = await BuildExistingPhoneIndexAsync(ct);
        var filePhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var menu in document.Descendants("Menu"))
        {
            var groupName = NormalizeImportedText(menu.Attribute("Name")?.Value);
            foreach (var unit in menu.Elements("Unit"))
            {
                ct.ThrowIfCancellationRequested();
                result.Scanned++;

                var name = NormalizeImportedText(unit.Attribute("Name")?.Value);
                var phones = ReadYealinkPhones(unit);
                var phoneKeys = GetPhoneMatchKeys(phones);
                var detail = new YealinkContactImportDetailDto
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Контакт без имени" : name,
                    Group = groupName,
                    Phones = phones,
                };

                if (phones.Count == 0 || phoneKeys.Count == 0)
                {
                    detail.Action = "SkippedNoPhone";
                    detail.Message = "Контакт пропущен: в XML нет телефонов.";
                    result.SkippedNoPhone++;
                    result.Details.Add(detail);
                    continue;
                }

                result.WithPhones++;

                (string SourceType, int SourceId)? existingMatch = null;
                foreach (var key in phoneKeys)
                {
                    if (existingPhones.TryGetValue(key, out var match))
                    {
                        existingMatch = match;
                        break;
                    }
                }
                if (existingMatch is not null)
                {
                    detail.Action = "SkippedExisting";
                    detail.MatchedSourceType = existingMatch.Value.SourceType;
                    detail.MatchedSourceId = existingMatch.Value.SourceId;
                    detail.Message = "Телефон уже есть в CRM.";
                    result.SkippedExisting++;
                    result.Details.Add(detail);
                    continue;
                }

                if (phoneKeys.Any(key => filePhones.Contains(key)))
                {
                    detail.Action = "SkippedDuplicateInFile";
                    detail.Message = "Контакт пропущен: такой телефон уже был обработан в этом XML.";
                    result.SkippedDuplicateInFile++;
                    result.Details.Add(detail);
                    continue;
                }

                var contactName = string.IsNullOrWhiteSpace(name)
                    ? $"Контакт {phones[0]}"
                    : name;

                var entityId = 0;
                if (!dryRun)
                {
                    var entity = new ExternalContact
                    {
                        FullName = contactName,
                        Phone = phones.FirstOrDefault(),
                        PhonesJson = SerializeStringList(phones),
                        Notes = string.IsNullOrWhiteSpace(groupName)
                            ? YealinkImportNote
                            : $"{YealinkImportNote}. Группа: {groupName}",
                        ResponsibleUserId = access.UserId.Value,
                        IsActive = true,
                        SyncToPhonebook = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };

                    _db.ExternalContacts.Add(entity);
                    await _db.SaveChangesAsync(ct);
                    entityId = entity.Id;
                }

                foreach (var key in phoneKeys)
                {
                    filePhones.Add(key);
                    existingPhones.TryAdd(key, ("external", entityId));
                }

                detail.Action = dryRun ? "WouldCreate" : "Created";
                detail.MatchedSourceType = "external";
                detail.MatchedSourceId = dryRun ? null : entityId;
                detail.Message = dryRun ? "Будет создан сторонний контакт." : "Создан сторонний контакт.";
                result.Created++;
                result.Details.Add(detail);
            }
        }

        return Ok(result);
    }

    private async Task<(int? UserId, bool IsAdmin)> GetCurrentAccessAsync(CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return (null, false);
        }

        var isAdmin = await _db.IsUserAdminAsync(currentUserId.Value, ct);
        return (currentUserId, isAdmin);
    }

    private IQueryable<ExternalContact> BuildVisibleExternalContactsQuery(int? currentUserId, bool isAdmin)
    {
        var query = _db.ExternalContacts.AsQueryable();
        if (isAdmin)
        {
            return query;
        }

        if (!currentUserId.HasValue)
        {
            return query.Where(_ => false);
        }

        return query.Where(x => x.ResponsibleUserId == currentUserId.Value);
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue)
        {
            return true;
        }

        return await _db.Users.AsNoTracking().AnyAsync(x => x.Id == responsibleUserId.Value && x.IsActive, ct);
    }

    private Task<LegalEntity?> ResolveLegalEntityAsync(int? legalEntityId, CancellationToken ct)
    {
        if (!legalEntityId.HasValue)
        {
            return Task.FromResult<LegalEntity?>(null);
        }

        return _db.LegalEntities.FirstOrDefaultAsync(x => x.Id == legalEntityId.Value, ct);
    }

    private static string NormalizeScope(string? scope)
    {
        var normalized = scope?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "clients" => "clients",
            "external" => "external",
            _ => "all",
        };
    }

    private static Encoding CreateWindows1251Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeImportedText(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var repaired = RepairUtf8AsWindows1251Mojibake(normalized);
        return Regex.Replace(repaired, @"\s+", " ").Trim();
    }

    private static string RepairUtf8AsWindows1251Mojibake(string value)
    {
        try
        {
            var bytes = Windows1251Encoding.GetBytes(value);
            var repaired = Encoding.UTF8.GetString(bytes);
            return repaired.Contains('\uFFFD') ? value : repaired;
        }
        catch
        {
            return value;
        }
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? items, string? fallback = null)
    {
        var normalized = (items ?? Array.Empty<string>())
            .Select(NormalizeOptional)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        var normalizedFallback = NormalizeOptional(fallback);
        if (!string.IsNullOrWhiteSpace(normalizedFallback) &&
            !normalized.Contains(normalizedFallback, StringComparer.OrdinalIgnoreCase))
        {
            normalized.Insert(0, normalizedFallback);
        }

        return normalized;
    }

    private static string? SerializeStringList(IReadOnlyCollection<string> items)
        => items.Count == 0 ? null : JsonSerializer.Serialize(items, JsonOptions);

    private static JsonObject GetOrCreateClientProfileObject(string? notes)
    {
        var existingNotes = notes?.Trim();
        var payload = ExtractClientProfilePayload(existingNotes);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var parsed = JsonNode.Parse(payload);
                if (parsed is JsonObject parsedObject)
                {
                    return parsedObject;
                }
            }
            catch
            {
                // ignored
            }
        }

        var created = new JsonObject();
        if (!string.IsNullOrWhiteSpace(existingNotes) &&
            !existingNotes.StartsWith(ClientProfileNotesPrefix, StringComparison.Ordinal))
        {
            created["legacyNotes"] = existingNotes;
        }

        return created;
    }

    private static string SerializeClientProfileObject(JsonObject profile)
        => $"{ClientProfileNotesPrefix}{profile.ToJsonString(JsonOptions)}";

    private static string? ExtractClientProfilePayload(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        return notes.StartsWith(ClientProfileNotesPrefix, StringComparison.Ordinal)
            ? notes[ClientProfileNotesPrefix.Length..].Trim()
            : null;
    }

    private static List<string> ReadProfileStringArray(JsonObject profile, string key, string? fallback = null)
    {
        var result = new List<string>();
        if (profile[key] is JsonArray array)
        {
            foreach (var item in array)
            {
                var value = item?.GetValue<string?>();
                var normalized = NormalizeOptional(value);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(normalized);
                }
            }
        }

        var normalizedFallback = NormalizeOptional(fallback);
        if (!string.IsNullOrWhiteSpace(normalizedFallback) &&
            !result.Contains(normalizedFallback, StringComparer.OrdinalIgnoreCase))
        {
            result.Insert(0, normalizedFallback);
        }

        return result;
    }

    private static string? ReadProfileString(JsonObject profile, string key)
    {
        return NormalizeOptional(profile[key]?.GetValue<string?>());
    }

    private static bool ReadProfileBool(JsonObject profile, string key, bool fallback)
    {
        if (profile[key] is JsonValue value)
        {
            try
            {
                return value.GetValue<bool>();
            }
            catch
            {
                // ignored
            }
        }

        return fallback;
    }

    private static void SetStringArray(JsonObject profile, string key, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            profile.Remove(key);
            return;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        profile[key] = array;
    }

    private static void SetString(JsonObject profile, string key, string? value)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            profile.Remove(key);
            return;
        }

        profile[key] = normalized;
    }

    private static ContactDirectoryItemDto MapClientContact(Client client)
    {
        var profile = GetOrCreateClientProfileObject(client.Notes);
        var phones = ReadProfileStringArray(profile, "phones", client.Phone);
        var emails = ReadProfileStringArray(profile, "emails", client.Email);

        return new ContactDirectoryItemDto
        {
            SourceType = "client",
            Category = "client",
            SourceId = client.Id,
            FullName = client.Name,
            OrganizationName = client.LegalEntity?.ShortName ?? client.Company,
            LegalEntityId = client.LegalEntityId,
            Position = ReadProfileString(profile, "contactPosition"),
            PowerOfAttorney = ReadProfileString(profile, "contactPowerOfAttorney"),
            Notes = ReadProfileString(profile, "contactNotes"),
            Phone = phones.FirstOrDefault(),
            Email = emails.FirstOrDefault(),
            Phones = phones,
            Emails = emails,
            IsActive = client.IsActive,
            SyncToPhonebook = ReadProfileBool(profile, "syncToPhonebook", true),
            ResponsibleUserId = client.ResponsibleUserId,
            ResponsibleUserName = string.IsNullOrWhiteSpace(client.ResponsibleUser?.FullName)
                ? client.ResponsibleUser?.Email
                : client.ResponsibleUser.FullName,
            CreatedAt = client.CreatedAt,
            UpdatedAt = null,
        };
    }

    private static ContactDirectoryItemDto MapExternalContact(ExternalContact contact)
    {
        var phones = DeserializeStringList(contact.PhonesJson, contact.Phone);
        var emails = DeserializeStringList(contact.EmailsJson, contact.Email);

        return new ContactDirectoryItemDto
        {
            SourceType = "external",
            Category = "external",
            SourceId = contact.Id,
            FullName = contact.FullName,
            OrganizationName = contact.LegalEntity?.ShortName,
            LegalEntityId = contact.LegalEntityId,
            Position = contact.Position,
            PowerOfAttorney = contact.PowerOfAttorney,
            Notes = contact.Notes,
            Phone = phones.FirstOrDefault(),
            Email = emails.FirstOrDefault(),
            Phones = phones,
            Emails = emails,
            IsActive = contact.IsActive,
            SyncToPhonebook = contact.SyncToPhonebook,
            ResponsibleUserId = contact.ResponsibleUserId,
            ResponsibleUserName = string.IsNullOrWhiteSpace(contact.ResponsibleUser?.FullName)
                ? contact.ResponsibleUser?.Email
                : contact.ResponsibleUser.FullName,
            CreatedAt = contact.CreatedAt,
            UpdatedAt = contact.UpdatedAt,
        };
    }

    private async Task<Dictionary<string, (string SourceType, int SourceId)>> BuildExistingPhoneIndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<string, (string SourceType, int SourceId)>(StringComparer.OrdinalIgnoreCase);

        var clients = await _db.Clients.AsNoTracking().ToListAsync(ct);
        foreach (var client in clients)
        {
            foreach (var key in GetPhoneMatchKeys(MapClientContact(client).Phones))
            {
                index.TryAdd(key, ("client", client.Id));
            }
        }

        var externalContacts = await _db.ExternalContacts.AsNoTracking().ToListAsync(ct);
        foreach (var contact in externalContacts)
        {
            foreach (var key in GetPhoneMatchKeys(MapExternalContact(contact).Phones))
            {
                index.TryAdd(key, ("external", contact.Id));
            }
        }

        return index;
    }

    private static List<string> DeserializeStringList(string? rawJson, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return NormalizeStringList(Array.Empty<string>(), fallback);
        }

        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(rawJson, JsonOptions) ?? new List<string>();
            return NormalizeStringList(values, fallback);
        }
        catch
        {
            return NormalizeStringList(Array.Empty<string>(), fallback);
        }
    }

    private static XElement BuildYealinkMenu(string name, IReadOnlyCollection<ContactDirectoryItemDto> contacts)
    {
        var menu = new XElement("Menu", new XAttribute("Name", name));
        foreach (var contact in contacts)
        {
            var expandedPhones = ExpandPhoneVariants(contact.Phones).Take(6).ToList();
            menu.Add(new XElement("Unit",
                new XAttribute("Name", contact.FullName),
                new XAttribute("Phone1", expandedPhones.ElementAtOrDefault(0) ?? string.Empty),
                new XAttribute("Phone2", expandedPhones.ElementAtOrDefault(1) ?? string.Empty),
                new XAttribute("Phone3", expandedPhones.ElementAtOrDefault(2) ?? string.Empty),
                new XAttribute("Phone4", expandedPhones.ElementAtOrDefault(3) ?? string.Empty),
                new XAttribute("Phone5", expandedPhones.ElementAtOrDefault(4) ?? string.Empty),
                new XAttribute("Phone6", expandedPhones.ElementAtOrDefault(5) ?? string.Empty),
                new XAttribute("default_photo", "Resource:")));
        }

        return menu;
    }

    private static List<string> ReadYealinkPhones(XElement unit)
    {
        return Enumerable.Range(1, 6)
            .Select(index => NormalizePhoneForStorage(unit.Attribute($"Phone{index}")?.Value))
            .Where(phone => !string.IsNullOrWhiteSpace(phone))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string? NormalizePhoneForStorage(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static List<string> GetPhoneMatchKeys(IEnumerable<string> phones)
    {
        return phones
            .Select(NormalizePhoneMatchKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private static string? NormalizePhoneMatchKey(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits))
        {
            return null;
        }

        if (digits.Length == 11 && (digits[0] == '7' || digits[0] == '8'))
        {
            return digits[1..];
        }

        return digits;
    }

    private static List<string> ExpandPhoneVariants(IEnumerable<string> phones)
    {
        var expanded = new List<string>();

        foreach (var original in phones)
        {
            var digits = new string((original ?? string.Empty).Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(digits))
            {
                continue;
            }

            AddIfMissing(expanded, digits);

            if (digits.Length == 10)
            {
                AddIfMissing(expanded, $"7{digits}");
                AddIfMissing(expanded, $"8{digits}");
                continue;
            }

            if (digits.Length == 11 && digits[0] == '7')
            {
                AddIfMissing(expanded, $"8{digits[1..]}");
                continue;
            }

            if (digits.Length == 11 && digits[0] == '8')
            {
                AddIfMissing(expanded, $"7{digits[1..]}");
            }
        }

        return expanded;
    }

    private static void AddIfMissing(ICollection<string> items, string value)
    {
        if (!items.Contains(value))
        {
            items.Add(value);
        }
    }
}

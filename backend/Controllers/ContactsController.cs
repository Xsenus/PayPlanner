using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

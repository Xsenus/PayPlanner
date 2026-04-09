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

    private sealed record PhoneMatchInfo(string SourceType, int SourceId, string FullName, List<string> Phones);

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
            IQueryable<Client> clientsQuery = _db.Clients
                .ApplyClientVisibility(access.IsAdmin, access.UserId)
                .Include(x => x.LegalEntity)
                .Include(x => x.ResponsibleUser);

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
            IQueryable<ExternalContact> externalQuery = BuildVisibleExternalContactsQuery(access.UserId, access.IsAdmin)
                .Include(x => x.LegalEntity)
                .Include(x => x.ResponsibleUser);

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
            .Where(x => x.IsActive)
            .ToListAsync(ct);

        var externalContacts = await _db.ExternalContacts
            .Include(x => x.LegalEntity)
            .Include(x => x.ResponsibleUser)
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
        [FromForm] string? decisionsJson = null,
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
        var decisions = ParseYealinkImportDecisions(decisionsJson);
        var existingPhones = await BuildExistingPhoneIndexAsync(ct);
        var filePhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var rowId = 0;

        foreach (var menu in document.Descendants("Menu"))
        {
            var groupName = NormalizeImportedText(menu.Attribute("Name")?.Value);
            foreach (var unit in menu.Elements("Unit"))
            {
                ct.ThrowIfCancellationRequested();
                rowId++;
                result.Scanned++;

                var name = NormalizeImportedText(unit.Attribute("Name")?.Value);
                var phones = ReadYealinkPhones(unit);
                var phoneKeys = GetPhoneMatchKeys(phones);
                var detail = new YealinkContactImportDetailDto
                {
                    RowId = rowId,
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

                PhoneMatchInfo? existingMatch = null;
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
                    detail.MatchedSourceType = existingMatch.SourceType;
                    detail.MatchedSourceId = existingMatch.SourceId;
                    detail.MatchedName = existingMatch.FullName;
                    detail.MatchedPhones = existingMatch.Phones;

                    var decision = decisions.GetValueOrDefault(rowId);
                    if (!dryRun && string.Equals(decision, "updateExisting", StringComparison.OrdinalIgnoreCase))
                    {
                        var updatedMatch = await UpdateExistingContactFromYealinkAsync(existingMatch, name, phones, ct);
                        if (updatedMatch is null)
                        {
                            detail.Action = "SkippedInvalid";
                            detail.Message = "Контакт найден, но обновить его не удалось.";
                            result.SkippedInvalid++;
                        }
                        else
                        {
                            detail.Action = "Updated";
                            detail.MatchedSourceType = updatedMatch.SourceType;
                            detail.MatchedSourceId = updatedMatch.SourceId;
                            detail.MatchedName = updatedMatch.FullName;
                            detail.MatchedPhones = updatedMatch.Phones;
                            detail.Message = "Существующий контакт обновлён: телефоны объединены, синхронизация включена.";
                            result.Updated++;
                        }
                    }
                    else
                    {
                        detail.Action = dryRun ? "Existing" : "SkippedExisting";
                        detail.Message = dryRun
                            ? "Телефон уже есть в CRM. По умолчанию контакт останется без изменений."
                            : "Контакт оставлен без изменений.";
                        result.SkippedExisting++;
                    }

                    foreach (var key in phoneKeys)
                    {
                        filePhones.Add(key);
                    }

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
                var decisionAction = decisions.GetValueOrDefault(rowId);
                if (!dryRun && string.Equals(decisionAction, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    detail.Action = "SkippedByUser";
                    detail.Message = "Контакт пропущен по выбору пользователя.";
                    result.SkippedByUser++;
                    result.Details.Add(detail);
                    continue;
                }

                var entityId = 0;
                var createdSourceType = "external";
                if (!dryRun)
                {
                    if (string.Equals(decisionAction, "createClient", StringComparison.OrdinalIgnoreCase))
                    {
                        var profile = new JsonObject
                        {
                            ["syncToPhonebook"] = true,
                            ["contactNotes"] = string.IsNullOrWhiteSpace(groupName)
                                ? YealinkImportNote
                                : $"{YealinkImportNote}. Группа: {groupName}",
                        };
                        SetStringArray(profile, "phones", phones);

                        var client = new Client
                        {
                            Name = contactName,
                            Phone = phones.FirstOrDefault() ?? string.Empty,
                            Email = string.Empty,
                            Notes = SerializeClientProfileObject(profile),
                            ResponsibleUserId = access.UserId.Value,
                            IsActive = true,
                            CreatedAt = now,
                        };

                        _db.Clients.Add(client);
                        await _db.SaveChangesAsync(ct);
                        entityId = client.Id;
                        createdSourceType = "client";
                    }
                    else
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
                }

                foreach (var key in phoneKeys)
                {
                    filePhones.Add(key);
                    existingPhones.TryAdd(key, new PhoneMatchInfo(createdSourceType, entityId, contactName, phones));
                }

                detail.Action = dryRun ? "WouldCreateExternal" : createdSourceType == "client" ? "CreatedClient" : "CreatedExternal";
                detail.MatchedSourceType = createdSourceType;
                detail.MatchedSourceId = dryRun ? null : entityId;
                detail.Message = dryRun
                    ? "По умолчанию будет создан сторонний контакт. В предпросмотре можно переключить на клиента или пропустить."
                    : createdSourceType == "client" ? "Создан клиент." : "Создан сторонний контакт.";
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

    private static Dictionary<int, string> ParseYealinkImportDecisions(string? decisionsJson)
    {
        if (string.IsNullOrWhiteSpace(decisionsJson))
        {
            return new Dictionary<int, string>();
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<YealinkContactImportDecisionDto>>(decisionsJson, JsonOptions)
                ?? new List<YealinkContactImportDecisionDto>();

            return items
                .Where(item => item.RowId > 0 && IsAllowedYealinkImportDecision(item.Action))
                .GroupBy(item => item.RowId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().Action.Trim(),
                    EqualityComparer<int>.Default);
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static bool IsAllowedYealinkImportDecision(string? action)
        => string.Equals(action, "skip", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(action, "createExternal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(action, "createClient", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(action, "updateExisting", StringComparison.OrdinalIgnoreCase);

    private async Task<PhoneMatchInfo?> UpdateExistingContactFromYealinkAsync(
        PhoneMatchInfo match,
        string? importedName,
        IReadOnlyCollection<string> importedPhones,
        CancellationToken ct)
    {
        if (string.Equals(match.SourceType, "client", StringComparison.OrdinalIgnoreCase))
        {
            var client = await _db.Clients.FirstOrDefaultAsync(x => x.Id == match.SourceId, ct);
            if (client is null)
            {
                return null;
            }

            var profile = GetOrCreateClientProfileObject(client.Notes);
            var mergedPhones = MergeStringLists(ReadProfileStringArray(profile, "phones", client.Phone), importedPhones);

            if (string.IsNullOrWhiteSpace(client.Name) && !string.IsNullOrWhiteSpace(importedName))
            {
                client.Name = importedName.Trim();
            }

            client.Phone = mergedPhones.FirstOrDefault() ?? client.Phone;
            SetStringArray(profile, "phones", mergedPhones);
            profile["syncToPhonebook"] = true;
            client.Notes = SerializeClientProfileObject(profile);
            await _db.SaveChangesAsync(ct);

            return new PhoneMatchInfo("client", client.Id, client.Name, mergedPhones);
        }

        if (string.Equals(match.SourceType, "external", StringComparison.OrdinalIgnoreCase))
        {
            var contact = await _db.ExternalContacts.FirstOrDefaultAsync(x => x.Id == match.SourceId, ct);
            if (contact is null)
            {
                return null;
            }

            var mergedPhones = MergeStringLists(DeserializeStringList(contact.PhonesJson, contact.Phone), importedPhones);
            if (string.IsNullOrWhiteSpace(contact.FullName) && !string.IsNullOrWhiteSpace(importedName))
            {
                contact.FullName = importedName.Trim();
            }

            contact.Phone = mergedPhones.FirstOrDefault();
            contact.PhonesJson = SerializeStringList(mergedPhones);
            contact.SyncToPhonebook = true;
            contact.IsActive = true;
            contact.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new PhoneMatchInfo("external", contact.Id, contact.FullName, mergedPhones);
        }

        return null;
    }

    private static List<string> MergeStringLists(IEnumerable<string> current, IEnumerable<string> incoming)
        => current
            .Concat(incoming)
            .Select(NormalizeOptional)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

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

    private ContactDirectoryItemDto MapClientContact(Client client)
    {
        var profile = GetOrCreateClientProfileObject(client.Notes);
        var phones = ReadProfileStringArray(profile, "phones", client.Phone);
        var emails = ReadProfileStringArray(profile, "emails", client.Email);
        var audit = _db.ReadAuditSnapshot(client);

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
            AuditCreatedByUserId = audit?.CreatedByUserId,
            AuditCreatedByUserName = audit?.CreatedByUserName,
            AuditUpdatedAt = audit?.UpdatedAt,
            AuditUpdatedByUserId = audit?.UpdatedByUserId,
            AuditUpdatedByUserName = audit?.UpdatedByUserName,
            AuditIsDeleted = audit?.IsDeleted ?? false,
            AuditDeletedAt = audit?.DeletedAt,
            AuditDeletedByUserId = audit?.DeletedByUserId,
            AuditDeletedByUserName = audit?.DeletedByUserName,
        };
    }

    private ContactDirectoryItemDto MapExternalContact(ExternalContact contact)
    {
        var phones = DeserializeStringList(contact.PhonesJson, contact.Phone);
        var emails = DeserializeStringList(contact.EmailsJson, contact.Email);
        var audit = _db.ReadAuditSnapshot(contact);

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
            AuditCreatedByUserId = audit?.CreatedByUserId,
            AuditCreatedByUserName = audit?.CreatedByUserName,
            AuditUpdatedAt = audit?.UpdatedAt,
            AuditUpdatedByUserId = audit?.UpdatedByUserId,
            AuditUpdatedByUserName = audit?.UpdatedByUserName,
            AuditIsDeleted = audit?.IsDeleted ?? false,
            AuditDeletedAt = audit?.DeletedAt,
            AuditDeletedByUserId = audit?.DeletedByUserId,
            AuditDeletedByUserName = audit?.DeletedByUserName,
        };
    }

    private async Task<Dictionary<string, PhoneMatchInfo>> BuildExistingPhoneIndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<string, PhoneMatchInfo>(StringComparer.OrdinalIgnoreCase);

        var clients = await _db.Clients.ToListAsync(ct);
        foreach (var client in clients)
        {
            var contact = MapClientContact(client);
            var match = new PhoneMatchInfo("client", client.Id, contact.FullName, contact.Phones);
            foreach (var key in GetPhoneMatchKeys(contact.Phones))
            {
                index.TryAdd(key, match);
            }
        }

        var externalContacts = await _db.ExternalContacts.ToListAsync(ct);
        foreach (var contact in externalContacts)
        {
            var directoryItem = MapExternalContact(contact);
            var match = new PhoneMatchInfo("external", contact.Id, directoryItem.FullName, directoryItem.Phones);
            foreach (var key in GetPhoneMatchKeys(directoryItem.Phones))
            {
                index.TryAdd(key, match);
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

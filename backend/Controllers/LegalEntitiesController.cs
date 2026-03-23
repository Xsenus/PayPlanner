using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/legal-entities")]
[Authorize]
public class LegalEntitiesController : ControllerBase
{
    private const string MainCompanySettingKey = "legalEntities.mainCompanyId";

    private readonly PaymentContext _db;
    private readonly ILegalEntityEnrichmentService _enrichmentService;
    private readonly IFormRequirementsService _formRequirements;

    public LegalEntitiesController(PaymentContext db, ILegalEntityEnrichmentService enrichmentService, IFormRequirementsService formRequirements)
    {
        _db = db;
        _enrichmentService = enrichmentService;
        _formRequirements = formRequirements;
    }

    private bool IsAdmin()
        => User.IsInRole("admin");

    private async Task<int?> GetMainCompanyIdAsync(CancellationToken ct)
    {
        var rawValue = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == MainCompanySettingKey)
            .Select(item => item.Value)
            .FirstOrDefaultAsync(ct);

        return int.TryParse(rawValue, out var id) ? id : (int?)null;
    }

    private async Task UpsertMainCompanyIdAsync(int legalEntityId, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == MainCompanySettingKey, ct);

        if (setting is null)
        {
            _db.SystemSettings.Add(new SystemSetting
            {
                Key = MainCompanySettingKey,
                Value = legalEntityId.ToString(),
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
        }
        else
        {
            setting.Value = legalEntityId.ToString();
            setting.UpdatedAt = nowUtc;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task ClearMainCompanyIdIfMatchesAsync(int legalEntityId, CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == MainCompanySettingKey, ct);
        if (setting is null || !int.TryParse(setting.Value, out var currentId) || currentId != legalEntityId)
        {
            return;
        }

        _db.SystemSettings.Remove(setting);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<IQueryable<LegalEntity>> ApplyCompanyVisibilityAsync(IQueryable<LegalEntity> query, CancellationToken ct)
    {
        if (IsAdmin())
        {
            return query;
        }

        var mainCompanyId = await GetMainCompanyIdAsync(ct);
        if (mainCompanyId.HasValue)
        {
            query = query.Where(item => item.Id != mainCompanyId.Value);
        }

        return query;
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    private async Task ApplyClientLinksAsync(int legalEntityId, IReadOnlyCollection<int> desiredClientIds, CancellationToken ct)
    {
        var desired = desiredClientIds.ToHashSet();

        var currentClients = await _db.Clients
            .Where(c => c.LegalEntityId == legalEntityId)
            .ToListAsync(ct);

        foreach (var client in currentClients)
        {
            if (!desired.Contains(client.Id))
            {
                client.LegalEntityId = null;
            }
        }

        var clientsToAssign = await _db.Clients
            .Where(c => desired.Contains(c.Id))
            .ToListAsync(ct);

        foreach (var client in clientsToAssign)
        {
            client.LegalEntityId = legalEntityId;
        }
    }

    private static LegalEntityDetailDto MapDetail(LegalEntity entity)
        => new()
        {
            Id = entity.Id,
            ShortName = entity.ShortName,
            FullName = entity.FullName,
            Inn = entity.Inn,
            Kpp = entity.Kpp,
            Ogrn = entity.Ogrn,
            Address = entity.Address,
            Phone = entity.Phone,
            Email = entity.Email,
            Director = entity.Director,
            ResponsibleUserId = entity.ResponsibleUserId,
            ResponsibleUserName = entity.ResponsibleUser?.FullName,
            Country = entity.Country,
            BaseCurrency = entity.BaseCurrency,
            ServiceLanguage = entity.ServiceLanguage,
            OrganizationForm = entity.OrganizationForm,
            Timezone = entity.Timezone,
            TaxSystem = entity.TaxSystem,
            TaxSystemMode = entity.TaxSystemMode,
            IncomeTaxRate = entity.IncomeTaxRate,
            IncomeExpenseTaxRate = entity.IncomeExpenseTaxRate,
            VatWithoutVat = entity.VatWithoutVat,
            Okved = entity.Okved,
            Okpo = entity.Okpo,
            Okato = entity.Okato,
            Oktmo = entity.Oktmo,
            Okogu = entity.Okogu,
            Okfs = entity.Okfs,
            Okopf = entity.Okopf,
            Pfr = entity.Pfr,
            Fss = entity.Fss,
            CharterCapital = entity.CharterCapital,
            TaxRegistrationDate = entity.TaxRegistrationDate,
            TaxOfficeCode = entity.TaxOfficeCode,
            TaxRegion = entity.TaxRegion,
            IfnsName = entity.IfnsName,
            TaxOfficePhone = entity.TaxOfficePhone,
            TreasuryInn = entity.TreasuryInn,
            TreasuryKpp = entity.TreasuryKpp,
            TreasuryRecipientName = entity.TreasuryRecipientName,
            TreasuryBankBik = entity.TreasuryBankBik,
            TreasuryBankName = entity.TreasuryBankName,
            TreasuryAccountNumber = entity.TreasuryAccountNumber,
            TreasuryCorrAccountNumber = entity.TreasuryCorrAccountNumber,
            TreasuryKbk = entity.TreasuryKbk,
            BankAccountsJson = entity.BankAccountsJson,
            CashRegistersJson = entity.CashRegistersJson,
            CardsJson = entity.CardsJson,
            RequisitesEnglishJson = entity.RequisitesEnglishJson,
            LogoDataUrl = entity.LogoDataUrl,
            SealDataUrl = entity.SealDataUrl,
            DirectorSignatureDataUrl = entity.DirectorSignatureDataUrl,
            DocumentDirectorJson = entity.DocumentDirectorJson,
            ChiefAccountantJson = entity.ChiefAccountantJson,
            ContactPhonesJson = entity.ContactPhonesJson,
            ContactEmailsJson = entity.ContactEmailsJson,
            Notes = entity.Notes,
            ClientsCount = entity.Clients.Count,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            Clients = entity.Clients
                .OrderBy(c => c.Name)
                .Select(c => new LegalEntityClientDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Phone = c.Phone,
                    Email = c.Email,
                    IsActive = c.IsActive,
                })
                .ToList(),
            Addresses = entity.Addresses
                .OrderByDescending(a => a.IsPrimary)
                .ThenBy(a => a.CreatedAt)
                .Select(MapAddress)
                .ToList(),
        };

    private static AddressRecordDto MapAddress(AddressRecord address)
        => new()
        {
            Id = address.Id,
            Type = (int)address.Type,
            Value = address.Value,
            UnrestrictedValue = address.UnrestrictedValue,
            PostalCode = address.PostalCode,
            Country = address.Country,
            RegionWithType = address.RegionWithType,
            CityWithType = address.CityWithType,
            StreetWithType = address.StreetWithType,
            House = address.House,
            Flat = address.Flat,
            FiasId = address.FiasId,
            KladrId = address.KladrId,
            GeoLat = address.GeoLat,
            GeoLon = address.GeoLon,
            IsPrimary = address.IsPrimary,
            ClientId = address.ClientId,
            LegalEntityId = address.LegalEntityId,
            CreatedAt = address.CreatedAt,
            UpdatedAt = address.UpdatedAt,
        };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task EnsureAndNormalizeDictionaryValuesAsync(LegalEntity entity, CancellationToken ct)
    {
        entity.Country = await ResolveDictionaryValueAsync(_db.Countries, entity.Country, "Добавлено автоматически", "#2563EB", ct);
        entity.BaseCurrency = await ResolveDictionaryValueAsync(_db.BaseCurrencies, entity.BaseCurrency, "Добавлено автоматически", "#16A34A", ct);
        entity.ServiceLanguage = await ResolveDictionaryValueAsync(_db.ServiceLanguages, entity.ServiceLanguage, "Добавлено автоматически", "#14B8A6", ct);
        entity.OrganizationForm = await ResolveDictionaryValueAsync(_db.OrganizationForms, entity.OrganizationForm, "Добавлено автоматически", "#F59E0B", ct);
        entity.Timezone = await ResolveDictionaryValueAsync(_db.Timezones, entity.Timezone, "Добавлено автоматически", "#8B5CF6", ct);
        entity.TaxSystem = await ResolveDictionaryValueAsync(_db.TaxSystems, entity.TaxSystem, "Добавлено автоматически", "#0284C7", ct);
        entity.TaxSystemMode = await ResolveDictionaryValueAsync(_db.TaxSystemModes, entity.TaxSystemMode, "Добавлено автоматически", "#0EA5E9", ct);
    }

    private async Task<string?> ResolveDictionaryValueAsync<TEntity>(DbSet<TEntity> set, string? rawValue,
        string defaultDescription, string defaultColor, CancellationToken ct)
        where TEntity : class, new()
    {
        var normalized = NormalizeOptional(rawValue);
        if (normalized is null)
            return null;

        var normalizedKey = CanonicalizeDictionaryName(normalized);
        var existing = await set
            .AsNoTracking()
            .Select(item => new { Entity = item, Name = EF.Property<string>(item, "Name") })
            .ToListAsync(ct);

        var matched = existing.FirstOrDefault(item => CanonicalizeDictionaryName(item.Name) == normalizedKey);

        if (matched is not null)
        {
            return NormalizeOptional(matched.Name);
        }

        var created = new TEntity();
        SetProperty(created, "Name", normalized);
        SetProperty(created, "Description", defaultDescription);
        SetProperty(created, "ColorHex", defaultColor);
        SetProperty(created, "IsActive", true);
        SetProperty(created, "CreatedAt", DateTime.UtcNow);
        set.Add(created);
        return normalized;
    }

    private static string CanonicalizeDictionaryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(chars);
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var propertyInfo = target.GetType().GetProperty(propertyName);
        if (propertyInfo is null || !propertyInfo.CanWrite)
            return;

        propertyInfo.SetValue(target, value);
    }

    private async Task<(IReadOnlyCollection<int> ClientIds, ActionResult? Error)> ValidateClientIdsAsync(
        IEnumerable<int> clientIds,
        CancellationToken ct)
    {
        var uniqueIds = clientIds?.Distinct().ToList() ?? new List<int>();
        if (uniqueIds.Count == 0)
        {
            return (Array.Empty<int>(), null);
        }

        var existingIds = await _db.Clients
            .AsNoTracking()
            .Where(c => uniqueIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (existingIds.Count != uniqueIds.Count)
        {
            var missing = uniqueIds.Except(existingIds).ToArray();
            return (Array.Empty<int>(), BadRequest($"Некоторые клиенты не найдены: {string.Join(", ", missing)}"));
        }

        return (existingIds, null);
    }

    private async Task<ActionResult?> ValidateInnUniquenessAsync(string? inn, int? excludeEntityId, CancellationToken ct)
    {
        var normalizedInn = NormalizeOptional(inn);
        if (normalizedInn is null)
            return null;

        var duplicate = await _db.LegalEntities
            .AsNoTracking()
            .Where(item => item.Inn != null && item.Inn.Trim() == normalizedInn)
            .Where(item => !excludeEntityId.HasValue || item.Id != excludeEntityId.Value)
            .Select(item => new { item.Id, item.ShortName, item.Inn })
            .FirstOrDefaultAsync(ct);

        if (duplicate is null)
            return null;

        return Conflict($"Юридическое лицо с ИНН {duplicate.Inn} уже существует (ID: {duplicate.Id}, {duplicate.ShortName}).");
    }

    /// <summary>
    /// Стандартизация адреса через DaData clean/address.
    /// </summary>
    [HttpPost("clean/address")]
    public async Task<ActionResult<CleanedAddressDto>> CleanAddress([FromBody] CleanAddressRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Address))
            return BadRequest("Укажите Address");

        var result = await _enrichmentService.CleanAddressAsync(request.Address, ct);
        if (result is null) return NotFound();

        return Ok(new CleanedAddressDto
        {
            Result = result.Result,
            PostalCode = result.PostalCode,
            Country = result.Country,
            RegionWithType = result.RegionWithType,
            CityWithType = result.CityWithType,
            StreetWithType = result.StreetWithType,
            House = result.House,
            Flat = result.Flat,
            FiasId = result.FiasId,
            KladrId = result.KladrId,
            GeoLat = result.GeoLat,
            GeoLon = result.GeoLon
        });
    }

    /// <summary>
    /// Стандартизация email через DaData clean/email.
    /// </summary>
    [HttpPost("clean/email")]
    public async Task<ActionResult<CleanedEmailDto>> CleanEmail([FromBody] CleanEmailRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Укажите Email");

        var result = await _enrichmentService.CleanEmailAsync(request.Email, ct);
        if (result is null) return NotFound();

        return Ok(new CleanedEmailDto
        {
            Email = result.Email,
            Type = result.Type,
            Qc = result.Qc
        });
    }

    /// <summary>
    /// Стандартизация телефона через DaData clean/phone.
    /// </summary>
    [HttpPost("clean/phone")]
    public async Task<ActionResult<CleanedPhoneDto>> CleanPhone([FromBody] CleanPhoneRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Phone))
            return BadRequest("Укажите Phone");

        var result = await _enrichmentService.CleanPhoneAsync(request.Phone, ct);
        if (result is null) return NotFound();

        return Ok(new CleanedPhoneDto
        {
            Phone = result.Phone,
            Country = result.Country,
            City = result.City,
            Provider = result.Provider,
            Qc = result.Qc
        });
    }


    private void ApplyLegalEntityAddresses(LegalEntity entity, List<LegalEntityAddressRequest>? addresses)
    {
        if (addresses is null) return;

        var normalized = addresses
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .Select(a => new AddressRecord
            {
                Type = Enum.IsDefined(typeof(AddressType), a.Type) ? (AddressType)a.Type : AddressType.Other,
                Value = a.Value!.Trim(),
                IsPrimary = a.IsPrimary,
                LegalEntityId = entity.Id,
            })
            .ToList();

        var existing = entity.Addresses.ToList();
        if (existing.Count > 0)
        {
            _db.AddressRecords.RemoveRange(existing);
            entity.Addresses.Clear();
        }

        if (normalized.Count > 0)
        {
            if (!normalized.Any(a => a.IsPrimary))
            {
                normalized[0].IsPrimary = true;
            }
            _db.AddressRecords.AddRange(normalized);
            foreach (var item in normalized) entity.Addresses.Add(item);
        }
    }

    [HttpPost]
    public async Task<ActionResult<LegalEntityDetailDto>> Create([FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("legal-entities", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (string.IsNullOrWhiteSpace(request.ShortName))
        {
            return BadRequest("Необходимо указать краткое название");
        }

        if (!await ResponsibleUserExistsAsync(request.ResponsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден или неактивен");
        }

        var duplicateInnError = await ValidateInnUniquenessAsync(request.Inn, excludeEntityId: null, ct);
        if (duplicateInnError is not null)
        {
            return duplicateInnError;
        }


        var (clientIds, errorResult) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var entity = new LegalEntity
        {
            ShortName = request.ShortName.Trim(),
            FullName = NormalizeOptional(request.FullName),
            Inn = NormalizeOptional(request.Inn),
            Kpp = NormalizeOptional(request.Kpp),
            Ogrn = NormalizeOptional(request.Ogrn),
            Address = NormalizeOptional(request.Address),
            Phone = NormalizeOptional(request.Phone),
            Email = NormalizeOptional(request.Email),
            Director = NormalizeOptional(request.Director),
            ResponsibleUserId = request.ResponsibleUserId,
            Country = NormalizeOptional(request.Country),
            BaseCurrency = NormalizeOptional(request.BaseCurrency),
            ServiceLanguage = NormalizeOptional(request.ServiceLanguage),
            OrganizationForm = NormalizeOptional(request.OrganizationForm),
            Timezone = NormalizeOptional(request.Timezone),
            TaxSystem = NormalizeOptional(request.TaxSystem),
            TaxSystemMode = NormalizeOptional(request.TaxSystemMode),
            IncomeTaxRate = NormalizeOptional(request.IncomeTaxRate),
            IncomeExpenseTaxRate = NormalizeOptional(request.IncomeExpenseTaxRate),
            VatWithoutVat = request.VatWithoutVat,
            Okved = NormalizeOptional(request.Okved),
            Okpo = NormalizeOptional(request.Okpo),
            Okato = NormalizeOptional(request.Okato),
            Oktmo = NormalizeOptional(request.Oktmo),
            Okogu = NormalizeOptional(request.Okogu),
            Okfs = NormalizeOptional(request.Okfs),
            Okopf = NormalizeOptional(request.Okopf),
            Pfr = NormalizeOptional(request.Pfr),
            Fss = NormalizeOptional(request.Fss),
            CharterCapital = NormalizeOptional(request.CharterCapital),
            TaxRegistrationDate = NormalizeOptional(request.TaxRegistrationDate),
            TaxOfficeCode = NormalizeOptional(request.TaxOfficeCode),
            TaxRegion = NormalizeOptional(request.TaxRegion),
            IfnsName = NormalizeOptional(request.IfnsName),
            TaxOfficePhone = NormalizeOptional(request.TaxOfficePhone),
            TreasuryInn = NormalizeOptional(request.TreasuryInn),
            TreasuryKpp = NormalizeOptional(request.TreasuryKpp),
            TreasuryRecipientName = NormalizeOptional(request.TreasuryRecipientName),
            TreasuryBankBik = NormalizeOptional(request.TreasuryBankBik),
            TreasuryBankName = NormalizeOptional(request.TreasuryBankName),
            TreasuryAccountNumber = NormalizeOptional(request.TreasuryAccountNumber),
            TreasuryCorrAccountNumber = NormalizeOptional(request.TreasuryCorrAccountNumber),
            TreasuryKbk = NormalizeOptional(request.TreasuryKbk),
            BankAccountsJson = NormalizeOptional(request.BankAccountsJson),
            CashRegistersJson = NormalizeOptional(request.CashRegistersJson),
            CardsJson = NormalizeOptional(request.CardsJson),
            RequisitesEnglishJson = NormalizeOptional(request.RequisitesEnglishJson),
            LogoDataUrl = NormalizeOptional(request.LogoDataUrl),
            SealDataUrl = NormalizeOptional(request.SealDataUrl),
            DirectorSignatureDataUrl = NormalizeOptional(request.DirectorSignatureDataUrl),
            DocumentDirectorJson = NormalizeOptional(request.DocumentDirectorJson),
            ChiefAccountantJson = NormalizeOptional(request.ChiefAccountantJson),
            ContactPhonesJson = NormalizeOptional(request.ContactPhonesJson),
            ContactEmailsJson = NormalizeOptional(request.ContactEmailsJson),
            Notes = NormalizeOptional(request.Notes),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await EnsureAndNormalizeDictionaryValuesAsync(entity, ct);
        _db.LegalEntities.Add(entity);
        await _db.SaveChangesAsync(ct);

        if (clientIds.Count > 0)
        {
            await ApplyClientLinksAsync(entity.Id, clientIds, ct);
        }

        ApplyLegalEntityAddresses(entity, request.Addresses);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        await _db.Entry(entity).Collection(le => le.Addresses).LoadAsync(ct);
        await _db.Entry(entity).Reference(le => le.ResponsibleUser).LoadAsync(ct);
        return Created($"/api/legal-entities/{entity.Id}", MapDetail(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var linkedClientsCount = entity.Clients.Count;
        if (linkedClientsCount > 0)
        {
            return Conflict($"Нельзя удалить юридическое лицо, пока к нему привязаны клиенты ({linkedClientsCount}). Сначала отвяжите клиентов.");
        }

        foreach (var client in entity.Clients)
        {
            client.LegalEntityId = null;
        }

        await ClearMainCompanyIdIfMatchesAsync(entity.Id, ct);
        _db.LegalEntities.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("company-profile")]
    public async Task<ActionResult<LegalEntityDetailDto>> GetCompanyProfile(CancellationToken ct)
    {
        var mainCompanyId = await GetMainCompanyIdAsync(ct);
        if (!mainCompanyId.HasValue)
        {
            return NotFound("Головная организация не выбрана.");
        }

        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(le => le.Id == mainCompanyId.Value, ct);

        if (entity is null)
        {
            return NotFound("Головная организация не найдена.");
        }

        return Ok(MapDetail(entity));
    }

    [HttpPut("company-profile/main/{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<LegalEntityDetailDto>> SetCompanyProfileMain(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(le => le.Id == id, ct);

        if (entity is null)
        {
            return NotFound();
        }

        await UpsertMainCompanyIdAsync(id, ct);
        return Ok(MapDetail(entity));
    }

    /// <summary>
    /// Получение полных реквизитов по ИНН/ОГРН (опц. КПП) через DaData findById/party.
    /// </summary>
    [HttpPost("find-by-id")]
    public async Task<ActionResult<LegalEntityDetailsDto>> FindById([FromBody] LegalEntityFindRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Укажите ИНН или ОГРН в поле Query");

        var details = await _enrichmentService.FindByInnOrOgrnAsync(request.Query.Trim(), request.Kpp?.Trim(), ct);
        if (details is null) return NotFound();

        var dto = new LegalEntityDetailsDto
        {
            ShortName = details.ShortName,
            FullName = details.FullName,
            Inn = details.Inn,
            Kpp = details.Kpp,
            Ogrn = details.Ogrn,
            OpfShort = details.OpfShort,
            OpfFull = details.OpfFull,
            Status = details.Status,
            RegistrationDate = details.RegistrationDate,
            LiquidationDate = details.LiquidationDate,
            Okved = details.Okved,
            OkvedType = details.OkvedType,
            BranchCount = details.BranchCount,
            Director = details.ManagementName,
            DirectorPost = details.ManagementPost,
            Address = details.AddressValue,
            AddressUnrestricted = details.AddressUnrestrictedValue,
            Country = details.Country,
            RegionWithType = details.RegionWithType,
            CityWithType = details.CityWithType,
            StreetWithType = details.StreetWithType,
            House = details.House,
            Flat = details.Flat,
            PostalCode = details.PostalCode,
            Okato = details.Okato,
            Oktmo = details.Oktmo,
            Okpo = details.Okpo,
            Okogu = details.Okogu,
            Okfs = details.Okfs,
            TaxSystem = details.TaxSystem,
            Phones = details.Phones,
            Emails = details.Emails
        };

        return Ok(dto);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LegalEntityListItemDto>>> GetAll([FromQuery] string? search, CancellationToken ct)
    {
        var query = await ApplyCompanyVisibilityAsync(_db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .AsNoTracking()
            .AsQueryable(), ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(le =>
                EF.Functions.Like(le.ShortName, term) ||
                EF.Functions.Like(le.FullName ?? string.Empty, term) ||
                EF.Functions.Like(le.Inn ?? string.Empty, term) ||
                EF.Functions.Like(le.Kpp ?? string.Empty, term) ||
                EF.Functions.Like(le.Ogrn ?? string.Empty, term));
        }

        var items = await query
            .OrderBy(le => le.ShortName)
            .ToListAsync(ct);

        return Ok(items.Select(MapDetail));
    }

    [HttpGet("paged")]
    public async Task<ActionResult<PagedResponse<LegalEntityListItemDto>>> GetPaged(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var baseQuery = await ApplyCompanyVisibilityAsync(_db.LegalEntities
            .AsNoTracking()
            .AsQueryable(), ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            baseQuery = baseQuery.Where(le =>
                EF.Functions.Like(le.ShortName, term) ||
                EF.Functions.Like(le.FullName ?? string.Empty, term) ||
                EF.Functions.Like(le.Inn ?? string.Empty, term) ||
                EF.Functions.Like(le.Kpp ?? string.Empty, term) ||
                EF.Functions.Like(le.Ogrn ?? string.Empty, term) ||
                EF.Functions.Like(le.Director ?? string.Empty, term) ||
                EF.Functions.Like(le.Address ?? string.Empty, term));
        }

        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .OrderBy(le => le.ShortName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<LegalEntityListItemDto>
        {
            Items = items.Select(MapDetail).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> GetById(int id, CancellationToken ct)
    {
        var mainCompanyId = await GetMainCompanyIdAsync(ct);
        if (!IsAdmin() && mainCompanyId == id)
        {
            return NotFound();
        }

        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(le => le.Id == id, ct);

        if (entity is null)
        {
            return NotFound();
        }

        return Ok(MapDetail(entity));
    }

    /// <summary>
    /// Подсказки по юрлицам/ИП через DaData suggest.
    /// </summary>
    [HttpPost("suggest")]
    public async Task<ActionResult<IEnumerable<LegalEntitySuggestionDto>>> Suggest([FromBody] LegalEntitySuggestionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query) && string.IsNullOrWhiteSpace(request.Inn))
        {
            return BadRequest("Необходимо указать запрос или ИНН");
        }

        var suggestions = await _enrichmentService.SuggestAsync(request.Query, request.Inn, request.Limit, ct);
        var response = suggestions.Select(s => new LegalEntitySuggestionDto
        {
            ShortName = s.ShortName,
            FullName = s.FullName,
            Inn = s.Inn,
            Kpp = s.Kpp,
            Ogrn = s.Ogrn,
            Address = s.Address,
            Phone = s.Phone,
            Email = s.Email,
            Director = s.ManagementName
        }).ToList();

        return Ok(response);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> Update(int id, [FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("legal-entities", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        if (string.IsNullOrWhiteSpace(request.ShortName))
        {
            return BadRequest("Необходимо указать краткое название");
        }

        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!await ResponsibleUserExistsAsync(request.ResponsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден или неактивен");
        }

        var duplicateInnError = await ValidateInnUniquenessAsync(request.Inn, id, ct);
        if (duplicateInnError is not null)
        {
            return duplicateInnError;
        }

        var (clientIds, errorResult) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (errorResult is not null)
        {
            return errorResult;
        }

        entity.ShortName = request.ShortName.Trim();
        entity.FullName = NormalizeOptional(request.FullName);
        entity.Inn = NormalizeOptional(request.Inn);
        entity.Kpp = NormalizeOptional(request.Kpp);
        entity.Ogrn = NormalizeOptional(request.Ogrn);
        entity.Address = NormalizeOptional(request.Address);
        entity.Phone = NormalizeOptional(request.Phone);
        entity.Email = NormalizeOptional(request.Email);
        entity.Director = NormalizeOptional(request.Director);
        entity.ResponsibleUserId = request.ResponsibleUserId;
        entity.Country = NormalizeOptional(request.Country);
        entity.BaseCurrency = NormalizeOptional(request.BaseCurrency);
        entity.ServiceLanguage = NormalizeOptional(request.ServiceLanguage);
        entity.OrganizationForm = NormalizeOptional(request.OrganizationForm);
        entity.Timezone = NormalizeOptional(request.Timezone);
        entity.TaxSystem = NormalizeOptional(request.TaxSystem);
        entity.TaxSystemMode = NormalizeOptional(request.TaxSystemMode);
        entity.IncomeTaxRate = NormalizeOptional(request.IncomeTaxRate);
        entity.IncomeExpenseTaxRate = NormalizeOptional(request.IncomeExpenseTaxRate);
        entity.VatWithoutVat = request.VatWithoutVat;
        entity.Okved = NormalizeOptional(request.Okved);
        entity.Okpo = NormalizeOptional(request.Okpo);
        entity.Okato = NormalizeOptional(request.Okato);
        entity.Oktmo = NormalizeOptional(request.Oktmo);
        entity.Okogu = NormalizeOptional(request.Okogu);
        entity.Okfs = NormalizeOptional(request.Okfs);
        entity.Okopf = NormalizeOptional(request.Okopf);
        entity.Pfr = NormalizeOptional(request.Pfr);
        entity.Fss = NormalizeOptional(request.Fss);
        entity.CharterCapital = NormalizeOptional(request.CharterCapital);
        entity.TaxRegistrationDate = NormalizeOptional(request.TaxRegistrationDate);
        entity.TaxOfficeCode = NormalizeOptional(request.TaxOfficeCode);
        entity.TaxRegion = NormalizeOptional(request.TaxRegion);
        entity.IfnsName = NormalizeOptional(request.IfnsName);
        entity.TaxOfficePhone = NormalizeOptional(request.TaxOfficePhone);
        entity.TreasuryInn = NormalizeOptional(request.TreasuryInn);
        entity.TreasuryKpp = NormalizeOptional(request.TreasuryKpp);
        entity.TreasuryRecipientName = NormalizeOptional(request.TreasuryRecipientName);
        entity.TreasuryBankBik = NormalizeOptional(request.TreasuryBankBik);
        entity.TreasuryBankName = NormalizeOptional(request.TreasuryBankName);
        entity.TreasuryAccountNumber = NormalizeOptional(request.TreasuryAccountNumber);
        entity.TreasuryCorrAccountNumber = NormalizeOptional(request.TreasuryCorrAccountNumber);
        entity.TreasuryKbk = NormalizeOptional(request.TreasuryKbk);
        entity.BankAccountsJson = NormalizeOptional(request.BankAccountsJson);
        entity.CashRegistersJson = NormalizeOptional(request.CashRegistersJson);
        entity.CardsJson = NormalizeOptional(request.CardsJson);
        entity.RequisitesEnglishJson = NormalizeOptional(request.RequisitesEnglishJson);
        entity.LogoDataUrl = NormalizeOptional(request.LogoDataUrl);
        entity.SealDataUrl = NormalizeOptional(request.SealDataUrl);
        entity.DirectorSignatureDataUrl = NormalizeOptional(request.DirectorSignatureDataUrl);
        entity.DocumentDirectorJson = NormalizeOptional(request.DocumentDirectorJson);
        entity.ChiefAccountantJson = NormalizeOptional(request.ChiefAccountantJson);
        entity.ContactPhonesJson = NormalizeOptional(request.ContactPhonesJson);
        entity.ContactEmailsJson = NormalizeOptional(request.ContactEmailsJson);
        entity.Notes = NormalizeOptional(request.Notes);
        entity.UpdatedAt = DateTime.UtcNow;

        await EnsureAndNormalizeDictionaryValuesAsync(entity, ct);
        await ApplyClientLinksAsync(entity.Id, clientIds, ct);
        ApplyLegalEntityAddresses(entity, request.Addresses);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        await _db.Entry(entity).Collection(le => le.Addresses).LoadAsync(ct);
        await _db.Entry(entity).Reference(le => le.ResponsibleUser).LoadAsync(ct);
        return Ok(MapDetail(entity));
    }

    /// <summary>
    /// Перенос legacy-полей Address (у юр. и физ. лиц) в AddressRecords с optional проверкой через DaData.
    /// </summary>
    [HttpPost("migrate-legacy-addresses")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<LegalEntityAddressMigrationResultDto>> MigrateLegacyAddresses(
        [FromBody] LegacyAddressMigrationRequest? request,
        CancellationToken ct)
    {
        var validateWithDadata = request?.ValidateWithDadata ?? true;

        var legalEntities = await _db.LegalEntities
            .Include(le => le.Addresses)
            .Where(le => le.Address != null && le.Address != string.Empty)
            .OrderBy(le => le.ShortName)
            .ToListAsync(ct);

        var clients = await _db.Clients
            .Include(c => c.Addresses)
            .Where(c => c.Address != null && c.Address != string.Empty)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var result = new LegalEntityAddressMigrationResultDto
        {
            Scanned = legalEntities.Count + clients.Count,
            ScannedLegalEntities = legalEntities.Count,
            ScannedClients = clients.Count,
            ValidateWithDadata = validateWithDadata,
        };

        foreach (var entity in legalEntities)
        {
            await MigrateLegacyAddressAsync(
                result,
                ownerType: "legal",
                ownerId: entity.Id,
                ownerName: entity.ShortName,
                legacyAddressRaw: entity.Address,
                addresses: entity.Addresses,
                addressType: AddressType.Legal,
                assignOwner: record => record.LegalEntityId = entity.Id,
                ct: ct);
        }

        foreach (var client in clients)
        {
            await MigrateLegacyAddressAsync(
                result,
                ownerType: "client",
                ownerId: client.Id,
                ownerName: client.Name,
                legacyAddressRaw: client.Address,
                addresses: client.Addresses,
                addressType: AddressType.Registration,
                assignOwner: record => record.ClientId = client.Id,
                ct: ct);
        }

        if (result.Added > 0 || result.Updated > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return Ok(result);
    }

    private async Task MigrateLegacyAddressAsync(
        LegalEntityAddressMigrationResultDto result,
        string ownerType,
        int ownerId,
        string ownerName,
        string? legacyAddressRaw,
        ICollection<AddressRecord> addresses,
        AddressType addressType,
        Action<AddressRecord> assignOwner,
        CancellationToken ct)
    {
        var legacyAddress = NormalizeOptional(legacyAddressRaw);
        if (string.IsNullOrWhiteSpace(legacyAddress))
        {
            result.EmptySkipped++;
            result.Details.Add(BuildMigrationDetail(ownerType, ownerId, ownerName, new LegalEntityAddressMigrationDetailDto
            {
                LegacyAddress = legacyAddressRaw,
                Action = "SkippedEmpty",
                Message = "Пустой legacy-адрес",
            }));
            return;
        }

        var cleaned = result.ValidateWithDadata
            ? await _enrichmentService.CleanAddressAsync(legacyAddress, ct)
            : null;

        if (result.ValidateWithDadata && cleaned is null)
        {
            // fallback: если clean недоступен/запрещён (например, 403), пробуем enrich через suggest/address
            var suggest = await _enrichmentService.SuggestAddressesAsync(legacyAddress, 1, ct);
            var first = suggest.FirstOrDefault();
            if (first is not null)
            {
                var normalized = NormalizeOptional(first.UnrestrictedValue) ?? NormalizeOptional(first.Value) ?? legacyAddress;
                cleaned = new CleanedAddress(
                    Source: legacyAddress,
                    Result: normalized,
                    PostalCode: NormalizeOptional(first.PostalCode),
                    Country: null,
                    CountryIsoCode: null,
                    FederalDistrict: null,
                    RegionWithType: NormalizeOptional(first.RegionWithType),
                    CityWithType: NormalizeOptional(first.CityWithType),
                    SettlementWithType: NormalizeOptional(first.SettlementWithType),
                    StreetWithType: NormalizeOptional(first.StreetWithType),
                    House: NormalizeOptional(first.House),
                    Block: null,
                    Flat: NormalizeOptional(first.Flat),
                    FiasId: NormalizeOptional(first.FiasId),
                    FiasLevel: null,
                    KladrId: NormalizeOptional(first.KladrId),
                    Okato: null,
                    Oktmo: null,
                    TaxOffice: null,
                    TaxOfficeLegal: null,
                    GeoLat: NormalizeOptional(first.GeoLat),
                    GeoLon: NormalizeOptional(first.GeoLon),
                    Qc: null,
                    QcComplete: null,
                    QcHouse: null,
                    QcGeo: null,
                    UnparsedParts: null,
                    Timezone: null);
            }
        }

        if (result.ValidateWithDadata)
        {
            if (cleaned is null)
            {
                result.DadataNotMatched++;
            }
            else
            {
                result.DadataMatched++;
            }
        }

        var normalizedValue = NormalizeOptional(cleaned?.Result) ?? legacyAddress;
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            result.Failed++;
            result.Details.Add(BuildMigrationDetail(ownerType, ownerId, ownerName, new LegalEntityAddressMigrationDetailDto
            {
                LegacyAddress = legacyAddress,
                Action = "Failed",
                Message = "Не удалось получить нормализованный адрес",
                DadataMatched = cleaned is not null,
            }));
            return;
        }

        var existingAddress = addresses
            .FirstOrDefault(a => a.Type == addressType && IsSameAddress(a, legacyAddress, normalizedValue));

        if (existingAddress is not null)
        {
            var existingChanged = ApplyCleanedData(existingAddress, legacyAddress, normalizedValue, cleaned, result.ValidateWithDadata && cleaned is not null);
            if (existingChanged)
            {
                existingAddress.UpdatedAt = DateTime.UtcNow;
                result.Updated++;
                result.Details.Add(BuildMigrationDetail(ownerType, ownerId, ownerName, new LegalEntityAddressMigrationDetailDto
                {
                    LegacyAddress = legacyAddress,
                    NormalizedAddress = normalizedValue,
                    Action = "Updated",
                    AddressRecordId = existingAddress.Id,
                    DadataMatched = cleaned is not null,
                    Message = "Существующая запись дополнена/обновлена",
                }));
            }
            else
            {
                result.DuplicateSkipped++;
                result.Details.Add(BuildMigrationDetail(ownerType, ownerId, ownerName, new LegalEntityAddressMigrationDetailDto
                {
                    LegacyAddress = legacyAddress,
                    NormalizedAddress = normalizedValue,
                    Action = "DuplicateSkipped",
                    AddressRecordId = existingAddress.Id,
                    DadataMatched = cleaned is not null,
                    Message = "Дубликат: запись уже есть",
                }));
            }

            return;
        }

        var hasPrimary = addresses.Any(a => a.Type == addressType && a.IsPrimary);

        var newRecord = new AddressRecord
        {
            Type = addressType,
            IsPrimary = !hasPrimary,
            Value = normalizedValue,
            UnrestrictedValue = legacyAddress,
            PostalCode = NormalizeOptional(cleaned?.PostalCode),
            Country = NormalizeOptional(cleaned?.Country),
            RegionWithType = NormalizeOptional(cleaned?.RegionWithType),
            CityWithType = NormalizeOptional(cleaned?.CityWithType),
            StreetWithType = NormalizeOptional(cleaned?.StreetWithType),
            House = NormalizeOptional(cleaned?.House),
            Flat = NormalizeOptional(cleaned?.Flat),
            FiasId = NormalizeOptional(cleaned?.FiasId),
            KladrId = NormalizeOptional(cleaned?.KladrId),
            GeoLat = NormalizeOptional(cleaned?.GeoLat),
            GeoLon = NormalizeOptional(cleaned?.GeoLon),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        assignOwner(newRecord);
        _db.AddressRecords.Add(newRecord);
        addresses.Add(newRecord);
        result.Added++;
        result.Details.Add(BuildMigrationDetail(ownerType, ownerId, ownerName, new LegalEntityAddressMigrationDetailDto
        {
            LegacyAddress = legacyAddress,
            NormalizedAddress = normalizedValue,
            Action = "Added",
            DadataMatched = cleaned is not null,
            Message = "Создана новая запись адреса",
        }));

        // legacy поле Address не изменяем: миграция должна быть строго неразрушающей.
    }

    private static LegalEntityAddressMigrationDetailDto BuildMigrationDetail(
        string ownerType,
        int ownerId,
        string ownerName,
        LegalEntityAddressMigrationDetailDto detail)
    {
        var normalizedType = ownerType.Equals("client", StringComparison.OrdinalIgnoreCase) ? "client" : "legal";
        return detail with
        {
            OwnerType = normalizedType,
            OwnerId = ownerId,
            OwnerName = ownerName,
            LegalEntityId = normalizedType == "legal" ? ownerId : null,
            LegalEntityName = normalizedType == "legal" ? ownerName : null,
            ClientId = normalizedType == "client" ? ownerId : null,
            ClientName = normalizedType == "client" ? ownerName : null,
        };
    }

    private static bool IsSameAddress(AddressRecord existing, string legacyAddress, string normalizedAddress)
    {
        return AddressEquals(existing.Value, legacyAddress)
               || AddressEquals(existing.Value, normalizedAddress)
               || AddressEquals(existing.UnrestrictedValue, legacyAddress)
               || AddressEquals(existing.UnrestrictedValue, normalizedAddress);
    }

    private static bool AddressEquals(string? left, string? right)
    {
        return string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ApplyCleanedData(
        AddressRecord target,
        string legacyAddress,
        string normalizedAddress,
        CleanedAddress? cleaned,
        bool overwriteWithDadata)
    {
        var changed = false;

        if (overwriteWithDadata)
        {
            if (!AddressEquals(target.Value, normalizedAddress))
            {
                target.Value = normalizedAddress;
                changed = true;
            }
            changed |= SetIfDifferent(target.UnrestrictedValue, v => target.UnrestrictedValue = v, legacyAddress);

            changed |= SetIfDifferent(target.PostalCode, v => target.PostalCode = v, NormalizeOptional(cleaned?.PostalCode));
            changed |= SetIfDifferent(target.Country, v => target.Country = v, NormalizeOptional(cleaned?.Country));
            changed |= SetIfDifferent(target.RegionWithType, v => target.RegionWithType = v, NormalizeOptional(cleaned?.RegionWithType));
            changed |= SetIfDifferent(target.CityWithType, v => target.CityWithType = v, NormalizeOptional(cleaned?.CityWithType));
            changed |= SetIfDifferent(target.StreetWithType, v => target.StreetWithType = v, NormalizeOptional(cleaned?.StreetWithType));
            changed |= SetIfDifferent(target.House, v => target.House = v, NormalizeOptional(cleaned?.House));
            changed |= SetIfDifferent(target.Flat, v => target.Flat = v, NormalizeOptional(cleaned?.Flat));
            changed |= SetIfDifferent(target.FiasId, v => target.FiasId = v, NormalizeOptional(cleaned?.FiasId));
            changed |= SetIfDifferent(target.KladrId, v => target.KladrId = v, NormalizeOptional(cleaned?.KladrId));
            changed |= SetIfDifferent(target.GeoLat, v => target.GeoLat = v, NormalizeOptional(cleaned?.GeoLat));
            changed |= SetIfDifferent(target.GeoLon, v => target.GeoLon = v, NormalizeOptional(cleaned?.GeoLon));
            return changed;
        }

        // Неразрушающий режим: существующие значения не перезаписываем, только дополняем пустые поля.
        if (string.IsNullOrWhiteSpace(target.Value))
        {
            target.Value = normalizedAddress;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.UnrestrictedValue))
        {
            target.UnrestrictedValue = legacyAddress;
            changed = true;
        }

        changed |= FillIfEmpty(target.PostalCode, v => target.PostalCode = v, NormalizeOptional(cleaned?.PostalCode));
        changed |= FillIfEmpty(target.Country, v => target.Country = v, NormalizeOptional(cleaned?.Country));
        changed |= FillIfEmpty(target.RegionWithType, v => target.RegionWithType = v, NormalizeOptional(cleaned?.RegionWithType));
        changed |= FillIfEmpty(target.CityWithType, v => target.CityWithType = v, NormalizeOptional(cleaned?.CityWithType));
        changed |= FillIfEmpty(target.StreetWithType, v => target.StreetWithType = v, NormalizeOptional(cleaned?.StreetWithType));
        changed |= FillIfEmpty(target.House, v => target.House = v, NormalizeOptional(cleaned?.House));
        changed |= FillIfEmpty(target.Flat, v => target.Flat = v, NormalizeOptional(cleaned?.Flat));
        changed |= FillIfEmpty(target.FiasId, v => target.FiasId = v, NormalizeOptional(cleaned?.FiasId));
        changed |= FillIfEmpty(target.KladrId, v => target.KladrId = v, NormalizeOptional(cleaned?.KladrId));
        changed |= FillIfEmpty(target.GeoLat, v => target.GeoLat = v, NormalizeOptional(cleaned?.GeoLat));
        changed |= FillIfEmpty(target.GeoLon, v => target.GeoLon = v, NormalizeOptional(cleaned?.GeoLon));

        return changed;
    }


    private static bool SetIfDifferent(string? current, Action<string?> setter, string? incoming)
    {
        var normalizedCurrent = NormalizeOptional(current);
        var normalizedIncoming = NormalizeOptional(incoming);
        if (string.Equals(normalizedCurrent, normalizedIncoming, StringComparison.OrdinalIgnoreCase)) return false;
        setter(normalizedIncoming);
        return true;
    }

    private static bool FillIfEmpty(string? current, Action<string?> setter, string? incoming)
    {
        if (!string.IsNullOrWhiteSpace(current)) return false;
        var normalizedIncoming = NormalizeOptional(incoming);
        if (string.IsNullOrWhiteSpace(normalizedIncoming)) return false;
        setter(normalizedIncoming);
        return true;
    }
}

public sealed record LegacyAddressMigrationRequest(bool ValidateWithDadata = true);

public sealed record LegalEntityAddressMigrationResultDto
{
    public int Scanned { get; init; }
    public int ScannedLegalEntities { get; init; }
    public int ScannedClients { get; init; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int DuplicateSkipped { get; set; }
    public int EmptySkipped { get; set; }
    public int Failed { get; set; }
    public int DadataMatched { get; set; }
    public int DadataNotMatched { get; set; }
    public bool ValidateWithDadata { get; init; }
    public List<LegalEntityAddressMigrationDetailDto> Details { get; init; } = new();
}

public sealed record LegalEntityAddressMigrationDetailDto
{
    public string OwnerType { get; init; } = "legal";
    public int OwnerId { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public int? LegalEntityId { get; init; }
    public string? LegalEntityName { get; init; }
    public int? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string? LegacyAddress { get; init; }
    public string? NormalizedAddress { get; init; }
    public string Action { get; init; } = string.Empty;
    public int? AddressRecordId { get; init; }
    public bool? DadataMatched { get; init; }
    public string? Message { get; init; }
}

public sealed record LegalEntityFindRequest(string Query, string? Kpp);

public sealed record LegalEntityDetailsDto
{
    public string? Address { get; init; }
    public string? AddressUnrestricted { get; init; }
    public string? Country { get; init; }
    public string? RegionWithType { get; init; }
    public string? CityWithType { get; init; }
    public string? StreetWithType { get; init; }
    public string? House { get; init; }
    public string? Flat { get; init; }
    public string? PostalCode { get; init; }
    public int? BranchCount { get; init; }
    public string? Director { get; init; }
    public string? DirectorPost { get; init; }
    public IReadOnlyList<string> Emails { get; init; } = Array.Empty<string>();
    public string? FullName { get; init; }
    public string? Inn { get; init; }
    public string? Kpp { get; init; }
    public DateTimeOffset? LiquidationDate { get; init; }
    public string? Ogrn { get; init; }
    public string? Okato { get; init; }
    public string? Oktmo { get; init; }
    public string? Okpo { get; init; }
    public string? Okogu { get; init; }
    public string? Okfs { get; init; }
    public string? Okved { get; init; }
    public string? OkvedType { get; init; }
    public string? OpfFull { get; init; }
    public string? OpfShort { get; init; }
    public IReadOnlyList<string> Phones { get; init; } = Array.Empty<string>();
    public DateTimeOffset? RegistrationDate { get; init; }
    public string ShortName { get; init; } = string.Empty;
    public string? Status { get; init; }
    public string? TaxSystem { get; init; }
}

public sealed record CleanAddressRequest(string Address);
public sealed record CleanPhoneRequest(string Phone);
public sealed record CleanEmailRequest(string Email);

public sealed record CleanedAddressDto
{
    public string? CityWithType { get; init; }
    public string? Country { get; init; }
    public string? FiasId { get; init; }
    public string? Flat { get; init; }
    public string? GeoLat { get; init; }
    public string? GeoLon { get; init; }
    public string? House { get; init; }
    public string? KladrId { get; init; }
    public string? PostalCode { get; init; }
    public string? RegionWithType { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? StreetWithType { get; init; }
}

public sealed record CleanedPhoneDto
{
    public string? City { get; init; }
    public string? Country { get; init; }
    public string Phone { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public int? Qc { get; init; }
}

public sealed record CleanedEmailDto
{
    public string Email { get; init; } = string.Empty;
    public int? Qc { get; init; }
    public string? Type { get; init; }
}

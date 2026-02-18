using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/legal-entities")]
[Authorize]
public class LegalEntitiesController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly ILegalEntityEnrichmentService _enrichmentService;

    public LegalEntitiesController(PaymentContext db, ILegalEntityEnrichmentService enrichmentService)
    {
        _db = db;
        _enrichmentService = enrichmentService;
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
            Country = entity.Country,
            BaseCurrency = entity.BaseCurrency,
            ServiceLanguage = entity.ServiceLanguage,
            OrganizationForm = entity.OrganizationForm,
            Timezone = entity.Timezone,
            TaxSystem = entity.TaxSystem,
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

    [HttpPost]
    public async Task<ActionResult<LegalEntityDetailDto>> Create([FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ShortName))
        {
            return BadRequest("Необходимо указать краткое название");
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
            Country = NormalizeOptional(request.Country),
            BaseCurrency = NormalizeOptional(request.BaseCurrency),
            ServiceLanguage = NormalizeOptional(request.ServiceLanguage),
            OrganizationForm = NormalizeOptional(request.OrganizationForm),
            Timezone = NormalizeOptional(request.Timezone),
            TaxSystem = NormalizeOptional(request.TaxSystem),
            Notes = NormalizeOptional(request.Notes),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.LegalEntities.Add(entity);
        await _db.SaveChangesAsync(ct);

        if (clientIds.Count > 0)
        {
            await ApplyClientLinksAsync(entity.Id, clientIds, ct);
            await _db.SaveChangesAsync(ct);
        }

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        await _db.Entry(entity).Collection(le => le.Addresses).LoadAsync(ct);
        return Created($"/api/legal-entities/{entity.Id}", MapDetail(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        foreach (var client in entity.Clients)
        {
            client.LegalEntityId = null;
        }

        _db.LegalEntities.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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
            AddressUnrestricted = details.AddressUnrestrictedValue
        };

        return Ok(dto);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LegalEntityListItemDto>>> GetAll([FromQuery] string? search, CancellationToken ct)
    {
        var query = _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .AsNoTracking()
            .AsQueryable();

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

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
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
        if (string.IsNullOrWhiteSpace(request.ShortName))
        {
            return BadRequest("Необходимо указать краткое название");
        }

        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
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
        entity.Country = NormalizeOptional(request.Country);
        entity.BaseCurrency = NormalizeOptional(request.BaseCurrency);
        entity.ServiceLanguage = NormalizeOptional(request.ServiceLanguage);
        entity.OrganizationForm = NormalizeOptional(request.OrganizationForm);
        entity.Timezone = NormalizeOptional(request.Timezone);
        entity.TaxSystem = NormalizeOptional(request.TaxSystem);
        entity.Notes = NormalizeOptional(request.Notes);
        entity.UpdatedAt = DateTime.UtcNow;

        await ApplyClientLinksAsync(entity.Id, clientIds, ct);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        await _db.Entry(entity).Collection(le => le.Addresses).LoadAsync(ct);
        return Ok(MapDetail(entity));
    }

    /// <summary>
    /// Перенос legacy-поля LegalEntity.Address в AddressRecords с optional проверкой через DaData.
    /// </summary>
    [HttpPost("migrate-legacy-addresses")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LegalEntityAddressMigrationResultDto>> MigrateLegacyAddresses(
        [FromBody] LegacyAddressMigrationRequest? request,
        CancellationToken ct)
    {
        var validateWithDadata = request?.ValidateWithDadata ?? true;

        var entities = await _db.LegalEntities
            .Include(le => le.Addresses)
            .Where(le => le.Address != null && le.Address != string.Empty)
            .OrderBy(le => le.ShortName)
            .ToListAsync(ct);

        var result = new LegalEntityAddressMigrationResultDto
        {
            Scanned = entities.Count,
            ValidateWithDadata = validateWithDadata,
        };

        foreach (var entity in entities)
        {
            var legacyAddress = NormalizeOptional(entity.Address);
            if (string.IsNullOrWhiteSpace(legacyAddress))
            {
                result.EmptySkipped++;
                result.Details.Add(new LegalEntityAddressMigrationDetailDto
                {
                    LegalEntityId = entity.Id,
                    LegalEntityName = entity.ShortName,
                    LegacyAddress = entity.Address,
                    Action = "SkippedEmpty",
                    Message = "Пустой legacy-адрес",
                });
                continue;
            }

            var cleaned = validateWithDadata
                ? await _enrichmentService.CleanAddressAsync(legacyAddress, ct)
                : null;

            if (validateWithDadata)
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
                result.Details.Add(new LegalEntityAddressMigrationDetailDto
                {
                    LegalEntityId = entity.Id,
                    LegalEntityName = entity.ShortName,
                    LegacyAddress = legacyAddress,
                    Action = "Failed",
                    Message = "Не удалось получить нормализованный адрес",
                    DadataMatched = cleaned is not null,
                });
                continue;
            }

            var existingLegalAddress = entity.Addresses
                .FirstOrDefault(a => a.Type == AddressType.Legal && IsSameAddress(a, legacyAddress, normalizedValue));

            if (existingLegalAddress is not null)
            {
                var existingChanged = ApplyCleanedData(existingLegalAddress, legacyAddress, normalizedValue, cleaned);
                if (existingChanged)
                {
                    existingLegalAddress.UpdatedAt = DateTime.UtcNow;
                    result.Updated++;
                    result.Details.Add(new LegalEntityAddressMigrationDetailDto
                    {
                        LegalEntityId = entity.Id,
                        LegalEntityName = entity.ShortName,
                        LegacyAddress = legacyAddress,
                        NormalizedAddress = normalizedValue,
                        Action = "Updated",
                        AddressRecordId = existingLegalAddress.Id,
                        DadataMatched = cleaned is not null,
                        Message = "Существующая запись дополнена/обновлена",
                    });
                }
                else
                {
                    result.DuplicateSkipped++;
                    result.Details.Add(new LegalEntityAddressMigrationDetailDto
                    {
                        LegalEntityId = entity.Id,
                        LegalEntityName = entity.ShortName,
                        LegacyAddress = legacyAddress,
                        NormalizedAddress = normalizedValue,
                        Action = "DuplicateSkipped",
                        AddressRecordId = existingLegalAddress.Id,
                        DadataMatched = cleaned is not null,
                        Message = "Дубликат: запись уже есть",
                    });
                }
            }
            else
            {
                var hasPrimaryLegal = entity.Addresses.Any(a => a.Type == AddressType.Legal && a.IsPrimary);

                var newRecord = new AddressRecord
                {
                    LegalEntityId = entity.Id,
                    Type = AddressType.Legal,
                    IsPrimary = !hasPrimaryLegal,
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

                _db.AddressRecords.Add(newRecord);
                entity.Addresses.Add(newRecord);
                result.Added++;
                result.Details.Add(new LegalEntityAddressMigrationDetailDto
                {
                    LegalEntityId = entity.Id,
                    LegalEntityName = entity.ShortName,
                    LegacyAddress = legacyAddress,
                    NormalizedAddress = normalizedValue,
                    Action = "Added",
                    DadataMatched = cleaned is not null,
                    Message = "Создана новая запись адреса",
                });
            }

            // legacy поле Address не изменяем: миграция должна быть строго неразрушающей.
        }

        if (result.Added > 0 || result.Updated > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return Ok(result);
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
        CleanedAddress? cleaned)
    {
        var changed = false;

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
    public int LegalEntityId { get; init; }
    public string LegalEntityName { get; init; } = string.Empty;
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
    public int? BranchCount { get; init; }
    public string? Director { get; init; }
    public string? DirectorPost { get; init; }
    public string? FullName { get; init; }
    public string? Inn { get; init; }
    public string? Kpp { get; init; }
    public DateTimeOffset? LiquidationDate { get; init; }
    public string? Ogrn { get; init; }
    public string? Okved { get; init; }
    public string? OkvedType { get; init; }
    public string? OpfFull { get; init; }
    public string? OpfShort { get; init; }
    public DateTimeOffset? RegistrationDate { get; init; }
    public string ShortName { get; init; } = string.Empty;
    public string? Status { get; init; }
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

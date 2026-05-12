using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/legal-entities")]
[Authorize]
public class LegalEntitiesController : ControllerBase
{
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private const long MaxLegalEntityRequestSizeBytes = 16 * 1024 * 1024;
    private const int MaxInlineLegalEntityAssetDataUrlLength = 350_000;
    private const int MaxInlineLegalEntityAssetsTotalLength = 800_000;
    private static readonly HashSet<string> LegalQuickClientRequirementFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Inn",
        "ShortName",
        "Comment",
    };

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

    private sealed record MainCompanySettingsState(IReadOnlyList<int> CompanyIds, int? DefaultCompanyId);

    private async Task<MainCompanySettingsState> LoadMainCompanySettingsStateAsync(CancellationToken ct)
    {
        var values = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == LegacyMainCompanySettingKey
                || item.Key == MainCompanyIdsSettingKey
                || item.Key == DefaultMainCompanyIdSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var companyIds = new List<int>();

        if (values.TryGetValue(MainCompanyIdsSettingKey, out var rawIds) && !string.IsNullOrWhiteSpace(rawIds))
        {
            try
            {
                var parsedIds = JsonSerializer.Deserialize<List<int>>(rawIds);
                if (parsedIds is not null)
                {
                    companyIds.AddRange(parsedIds.Where(id => id > 0));
                }
            }
            catch (JsonException)
            {
                // Ignore invalid JSON and fall back to legacy value below.
            }
        }

        if (companyIds.Count == 0
            && values.TryGetValue(LegacyMainCompanySettingKey, out var rawLegacyId)
            && int.TryParse(rawLegacyId, out var legacyId)
            && legacyId > 0)
        {
            companyIds.Add(legacyId);
        }

        companyIds = companyIds
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        int? defaultCompanyId = null;
        if (values.TryGetValue(DefaultMainCompanyIdSettingKey, out var rawDefaultId)
            && int.TryParse(rawDefaultId, out var configuredDefaultId)
            && configuredDefaultId > 0)
        {
            defaultCompanyId = configuredDefaultId;
        }
        else if (values.TryGetValue(LegacyMainCompanySettingKey, out rawLegacyId)
                 && int.TryParse(rawLegacyId, out legacyId)
                 && legacyId > 0)
        {
            defaultCompanyId = legacyId;
        }

        if (defaultCompanyId.HasValue && !companyIds.Contains(defaultCompanyId.Value))
        {
            defaultCompanyId = companyIds.FirstOrDefault();
        }

        if (!defaultCompanyId.HasValue && companyIds.Count > 0)
        {
            defaultCompanyId = companyIds[0];
        }

        return new MainCompanySettingsState(companyIds, defaultCompanyId);
    }

    private async Task<int?> GetMainCompanyIdAsync(CancellationToken ct)
        => (await LoadMainCompanySettingsStateAsync(ct)).DefaultCompanyId;

    private async Task UpsertMainCompanyIdAsync(int legalEntityId, CancellationToken ct)
    {
        var state = await LoadMainCompanySettingsStateAsync(ct);
        var companyIds = state.CompanyIds.Contains(legalEntityId)
            ? state.CompanyIds
            : state.CompanyIds.Concat([legalEntityId]).Distinct().OrderBy(id => id).ToList();

        await SaveMainCompanySettingsStateAsync(companyIds, legalEntityId, ct);
    }

    private async Task SaveMainCompanySettingsStateAsync(IEnumerable<int> companyIds, int? defaultCompanyId, CancellationToken ct)
    {
        var normalizedIds = companyIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var normalizedIdSet = normalizedIds.ToHashSet();

        var settings = await _db.SystemSettings
            .Where(item =>
                item.Key == LegacyMainCompanySettingKey
                || item.Key == MainCompanyIdsSettingKey
                || item.Key == DefaultMainCompanyIdSettingKey)
            .ToListAsync(ct);

        var nowUtc = DateTime.UtcNow;

        void UpsertSetting(string key, string value)
        {
            var setting = settings.FirstOrDefault(item => item.Key == key);
            if (setting is null)
            {
                _db.SystemSettings.Add(new SystemSetting
                {
                    Key = key,
                    Value = value,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc,
                });
                return;
            }

            setting.Value = value;
            setting.UpdatedAt = nowUtc;
        }

        void RemoveSetting(string key)
        {
            var setting = settings.FirstOrDefault(item => item.Key == key);
            if (setting is not null)
            {
                _db.SystemSettings.Remove(setting);
            }
        }

        if (normalizedIds.Count == 0)
        {
            var allCompanyAccesses = await _db.UserCompanyAccesses.ToListAsync(ct);
            if (allCompanyAccesses.Count > 0)
            {
                _db.UserCompanyAccesses.RemoveRange(allCompanyAccesses);
            }

            RemoveSetting(MainCompanyIdsSettingKey);
            RemoveSetting(DefaultMainCompanyIdSettingKey);
            RemoveSetting(LegacyMainCompanySettingKey);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var normalizedDefaultId = defaultCompanyId.HasValue && normalizedIds.Contains(defaultCompanyId.Value)
            ? defaultCompanyId.Value
            : normalizedIds[0];

        var staleAccesses = await _db.UserCompanyAccesses
            .Where(item => !normalizedIdSet.Contains(item.LegalEntityId))
            .ToListAsync(ct);
        if (staleAccesses.Count > 0)
        {
            _db.UserCompanyAccesses.RemoveRange(staleAccesses);
        }

        UpsertSetting(MainCompanyIdsSettingKey, JsonSerializer.Serialize(normalizedIds));
        UpsertSetting(DefaultMainCompanyIdSettingKey, normalizedDefaultId.ToString(CultureInfo.InvariantCulture));
        UpsertSetting(LegacyMainCompanySettingKey, normalizedDefaultId.ToString(CultureInfo.InvariantCulture));

        await _db.SaveChangesAsync(ct);
    }

    private async Task RemoveCompanyFromMainCompanySettingsAsync(int legalEntityId, CancellationToken ct)
    {
        var state = await LoadMainCompanySettingsStateAsync(ct);
        if (!state.CompanyIds.Contains(legalEntityId))
        {
            return;
        }

        var remainingIds = state.CompanyIds.Where(id => id != legalEntityId).ToList();
        var nextDefaultId = state.DefaultCompanyId == legalEntityId
            ? (remainingIds.Count > 0 ? remainingIds[0] : null)
            : state.DefaultCompanyId;

        await SaveMainCompanySettingsStateAsync(remainingIds, nextDefaultId, ct);
    }

    private async Task<IQueryable<LegalEntity>> ApplyCompanyVisibilityAsync(IQueryable<LegalEntity> query, CancellationToken ct)
    {
        var state = await LoadMainCompanySettingsStateAsync(ct);
        if (state.CompanyIds.Count > 0)
        {
            query = query.Where(item => !state.CompanyIds.Contains(item.Id));
        }

        return query;
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    private async Task<bool> ClientStatusExistsAsync(int? clientStatusId, CancellationToken ct)
    {
        if (!clientStatusId.HasValue) return true;
        return await _db.ClientStatuses.AsNoTracking().AnyAsync(status => status.Id == clientStatusId.Value, ct);
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

    private LegalEntityDetailDto MapDetail(LegalEntity entity, bool includeInlineAssets = true)
    {
        var audit = _db.ReadAuditSnapshot(entity);
        return new()
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
            ClientStatusId = entity.ClientStatusId,
            ClientStatus = entity.ClientStatus is null ? null : new ClientStatusDto
            {
                Id = entity.ClientStatus.Id,
                Name = entity.ClientStatus.Name,
                Description = entity.ClientStatus.Description,
                ColorHex = entity.ClientStatus.ColorHex,
                IsActive = entity.ClientStatus.IsActive,
                CreatedAt = entity.ClientStatus.CreatedAt,
                SystemKey = entity.ClientStatus.SystemKey,
            },
            IsClientAndSupplier = entity.IsClientAndSupplier,
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
            LogoDataUrl = includeInlineAssets ? entity.LogoDataUrl : null,
            SealDataUrl = includeInlineAssets ? entity.SealDataUrl : null,
            DirectorSignatureDataUrl = includeInlineAssets ? entity.DirectorSignatureDataUrl : null,
            DocumentDirectorJson = entity.DocumentDirectorJson,
            ChiefAccountantJson = entity.ChiefAccountantJson,
            ContactPhonesJson = entity.ContactPhonesJson,
            ContactEmailsJson = entity.ContactEmailsJson,
            Notes = entity.Notes,
            ClientsCount = entity.Clients.Count,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            AuditCreatedByUserId = audit?.CreatedByUserId,
            AuditCreatedByUserName = audit?.CreatedByUserName,
            AuditUpdatedAt = audit?.UpdatedAt,
            AuditUpdatedByUserId = audit?.UpdatedByUserId,
            AuditUpdatedByUserName = audit?.UpdatedByUserName,
            AuditIsDeleted = audit?.IsDeleted ?? false,
            AuditDeletedAt = audit?.DeletedAt,
            AuditDeletedByUserId = audit?.DeletedByUserId,
            AuditDeletedByUserName = audit?.DeletedByUserName,
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
    }

    private async Task<LegalEntity?> LoadLegalEntityForDetailAsync(int id, CancellationToken ct)
    {
        return await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .Include(le => le.ClientStatus)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
    }

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
            AreaWithType = address.AreaWithType,
            CityWithType = address.CityWithType,
            SettlementWithType = address.SettlementWithType,
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

    private static int SumInlineAssetPayloadLength(LegalEntityRequest request)
        => (request.LogoDataUrl?.Length ?? 0)
        + (request.SealDataUrl?.Length ?? 0)
        + (request.DirectorSignatureDataUrl?.Length ?? 0);

    private static string? ValidateInlineAssetPayload(LegalEntityRequest request)
    {
        if ((request.LogoDataUrl?.Length ?? 0) > MaxInlineLegalEntityAssetDataUrlLength)
        {
            return "Логотип компании слишком большой для сохранения. Уменьшите изображение и попробуйте снова.";
        }

        if ((request.SealDataUrl?.Length ?? 0) > MaxInlineLegalEntityAssetDataUrlLength)
        {
            return "Печать компании слишком большая для сохранения. Уменьшите изображение и попробуйте снова.";
        }

        if ((request.DirectorSignatureDataUrl?.Length ?? 0) > MaxInlineLegalEntityAssetDataUrlLength)
        {
            return "Подпись руководителя слишком большая для сохранения. Уменьшите изображение и попробуйте снова.";
        }

        if (SumInlineAssetPayloadLength(request) > MaxInlineLegalEntityAssetsTotalLength)
        {
            return "Суммарный размер логотипа, печати и подписи слишком большой для одного запроса. Уменьшите изображения и попробуйте снова.";
        }

        return null;
    }

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
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return null;

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

    private static string CanonicalizeAddressToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value
            .Trim()
            .ToLowerInvariant()
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal);

        normalized = Regex.Replace(normalized, @"\b\d{6}\b", " ", RegexOptions.CultureInvariant);

        normalized = Regex.Replace(
            normalized,
            @"\b(россия|рф|г|гор|город|ул|улица|д|дом|кв|квартира|обл|область)\b",
            " ",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var chars = normalized
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(chars);
    }

    private static string BuildAddressIdentityKey(AddressRecord address)
    {
        var typeKey = ((int)address.Type).ToString(CultureInfo.InvariantCulture);
        var fiasKey = CanonicalizeAddressToken(address.FiasId);
        if (!string.IsNullOrWhiteSpace(fiasKey))
        {
            return $"{typeKey}|fias:{fiasKey}";
        }

        var textKey = CanonicalizeAddressToken(address.UnrestrictedValue);
        if (string.IsNullOrWhiteSpace(textKey))
        {
            textKey = CanonicalizeAddressToken(address.Value);
        }

        if (!string.IsNullOrWhiteSpace(textKey))
        {
            return $"{typeKey}|text:{textKey}";
        }

        var structuredKey = string.Join("|", new[]
        {
            CanonicalizeAddressToken(address.Country),
            CanonicalizeAddressToken(address.RegionWithType),
            CanonicalizeAddressToken(address.AreaWithType),
            CanonicalizeAddressToken(address.CityWithType),
            CanonicalizeAddressToken(address.SettlementWithType),
            CanonicalizeAddressToken(address.StreetWithType),
            CanonicalizeAddressToken(address.House),
            CanonicalizeAddressToken(address.Flat),
        });

        return $"{typeKey}|{structuredKey}";
    }

    private static void MergeAddressRecord(AddressRecord target, AddressRecord incoming)
    {
        target.Value = NormalizeOptional(incoming.Value) ?? target.Value;
        target.UnrestrictedValue = target.UnrestrictedValue ?? NormalizeOptional(incoming.UnrestrictedValue);
        target.PostalCode = target.PostalCode ?? NormalizeOptional(incoming.PostalCode);
        target.Country = target.Country ?? NormalizeOptional(incoming.Country);
        target.RegionWithType = target.RegionWithType ?? NormalizeOptional(incoming.RegionWithType);
        target.AreaWithType = target.AreaWithType ?? NormalizeOptional(incoming.AreaWithType);
        target.CityWithType = target.CityWithType ?? NormalizeOptional(incoming.CityWithType);
        target.SettlementWithType = target.SettlementWithType ?? NormalizeOptional(incoming.SettlementWithType);
        target.StreetWithType = target.StreetWithType ?? NormalizeOptional(incoming.StreetWithType);
        target.House = target.House ?? NormalizeOptional(incoming.House);
        target.Flat = target.Flat ?? NormalizeOptional(incoming.Flat);
        target.FiasId = target.FiasId ?? NormalizeOptional(incoming.FiasId);
        target.KladrId = target.KladrId ?? NormalizeOptional(incoming.KladrId);
        target.GeoLat = target.GeoLat ?? NormalizeOptional(incoming.GeoLat);
        target.GeoLon = target.GeoLon ?? NormalizeOptional(incoming.GeoLon);
        target.IsPrimary |= incoming.IsPrimary;
    }

    private static IReadOnlyList<LegalEntitySuggestion> DeduplicateLegalEntitySuggestions(
        IReadOnlyList<LegalEntitySuggestion> suggestions,
        int limit)
    {
        var normalizedLimit = Math.Clamp(limit <= 0 ? 5 : limit, 1, 20);
        var deduped = new List<LegalEntitySuggestion>(normalizedLimit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in suggestions)
        {
            var key = BuildLegalEntitySuggestionIdentityKey(suggestion);
            if (!string.IsNullOrWhiteSpace(key) && !seen.Add(key))
            {
                continue;
            }

            deduped.Add(suggestion);
            if (deduped.Count >= normalizedLimit)
            {
                break;
            }
        }

        return deduped;
    }

    private static string BuildLegalEntitySuggestionIdentityKey(LegalEntitySuggestion suggestion)
    {
        var inn = Regex.Replace(NormalizeOptional(suggestion.Inn) ?? string.Empty, "\\D+", string.Empty);
        if (!string.IsNullOrWhiteSpace(inn))
        {
            var kpp = Regex.Replace(NormalizeOptional(suggestion.Kpp) ?? string.Empty, "\\D+", string.Empty);
            return string.IsNullOrWhiteSpace(kpp) ? $"inn:{inn}" : $"inn:{inn}|kpp:{kpp}";
        }

        var ogrn = Regex.Replace(NormalizeOptional(suggestion.Ogrn) ?? string.Empty, "\\D+", string.Empty);
        if (!string.IsNullOrWhiteSpace(ogrn))
        {
            return $"ogrn:{ogrn}";
        }

        var name = CanonicalizeAddressToken(suggestion.FullName ?? suggestion.ShortName);
        var address = CanonicalizeAddressToken(suggestion.Address);
        return $"{name}|{address}";
    }

    private static string? ResolveUpdatedAssetValue(string? currentValue, string? incomingValue, bool? changed)
    {
        if (changed == true)
        {
            return NormalizeOptional(incomingValue);
        }

        if (incomingValue is not null)
        {
            return NormalizeOptional(incomingValue);
        }

        return currentValue;
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
            AreaWithType = result.AreaWithType,
            CityWithType = result.CityWithType,
            SettlementWithType = result.SettlementWithType,
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


    private static string? ResolveLegacyAddressValue(List<LegalEntityAddressRequest>? addresses, string? legacyAddress)
    {
        if (addresses is null)
        {
            return NormalizeOptional(legacyAddress);
        }

        var normalizedAddresses = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address.Value))
            .Select(address => new AddressRecord
            {
                Type = Enum.IsDefined(typeof(AddressType), address.Type) ? (AddressType)address.Type : AddressType.Other,
                Value = address.Value!.Trim(),
                UnrestrictedValue = NormalizeOptional(address.UnrestrictedValue),
                IsPrimary = address.IsPrimary,
            });

        return NormalizeOptional(LegalEntityAddressResolver.ResolvePreferredAddress(normalizedAddresses, legacyAddress));
    }

    private async Task ApplyLegalEntityAddressesAsync(LegalEntity entity, List<LegalEntityAddressRequest>? addresses, CancellationToken ct)
    {
        if (addresses is null) return;

        var deduped = new List<AddressRecord>();

        foreach (var address in addresses.Where(a => !string.IsNullOrWhiteSpace(a.Value)))
        {
            var normalized = new AddressRecord
            {
                Type = Enum.IsDefined(typeof(AddressType), address.Type) ? (AddressType)address.Type : AddressType.Other,
                Value = address.Value!.Trim(),
                UnrestrictedValue = NormalizeOptional(address.UnrestrictedValue),
                PostalCode = NormalizeOptional(address.PostalCode),
                Country = NormalizeOptional(address.Country),
                RegionWithType = NormalizeOptional(address.RegionWithType),
                AreaWithType = NormalizeOptional(address.AreaWithType),
                CityWithType = NormalizeOptional(address.CityWithType),
                SettlementWithType = NormalizeOptional(address.SettlementWithType),
                StreetWithType = NormalizeOptional(address.StreetWithType),
                House = NormalizeOptional(address.House),
                Flat = NormalizeOptional(address.Flat),
                FiasId = NormalizeOptional(address.FiasId),
                KladrId = NormalizeOptional(address.KladrId),
                GeoLat = NormalizeOptional(address.GeoLat),
                GeoLon = NormalizeOptional(address.GeoLon),
                IsPrimary = address.IsPrimary,
                ClientId = null,
                LegalEntityId = entity.Id,
            };

            var existingIndex = deduped.FindIndex(item => BuildAddressIdentityKey(item) == BuildAddressIdentityKey(normalized));
            if (existingIndex < 0)
            {
                deduped.Add(normalized);
                continue;
            }

            MergeAddressRecord(deduped[existingIndex], normalized);
        }

        if (entity.Id > 0)
        {
            var trackedAddresses = _db.ChangeTracker
                .Entries<AddressRecord>()
                .Where(entry => entry.Entity.LegalEntityId == entity.Id)
                .ToList();

            foreach (var trackedAddress in trackedAddresses)
            {
                trackedAddress.State = EntityState.Detached;
            }

            await _db.AddressRecords
                .Where(item => item.LegalEntityId == entity.Id)
                .ExecuteDeleteAsync(ct);
        }

        entity.Addresses.Clear();

        if (deduped.Count == 0)
        {
            return;
        }

        var primaryIndex = deduped.FindIndex(item => item.IsPrimary);
        if (primaryIndex < 0)
        {
            primaryIndex = 0;
        }

        for (var index = 0; index < deduped.Count; index++)
        {
            deduped[index].IsPrimary = index == primaryIndex;
            deduped[index].ClientId = null;
            deduped[index].LegalEntityId = entity.Id;
        }

        _db.AddressRecords.AddRange(deduped);
    }

    [HttpPost]
    [RequestSizeLimit(MaxLegalEntityRequestSizeBytes)]
    public async Task<ActionResult<LegalEntityDetailDto>> Create([FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        var requirementsSection = string.Equals(request.FormRequirementsSection, "clients-modal-legal", StringComparison.OrdinalIgnoreCase)
            ? "clients-modal-legal"
            : "legal-entities";
        var missingCreate = await _formRequirements.GetMissingFieldsAsync(requirementsSection, request, ct);
        if (requirementsSection == "clients-modal-legal")
        {
            missingCreate = missingCreate
                .Where(field => LegalQuickClientRequirementFields.Contains(field))
                .ToList();
        }
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (string.IsNullOrWhiteSpace(request.ShortName))
        {
            return BadRequest("Необходимо указать краткое название");
        }

        if (!await ResponsibleUserExistsAsync(request.ResponsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден или неактивен");
        }

        if (!await ClientStatusExistsAsync(request.ClientStatusId, ct))
        {
            return BadRequest($"Статус клиента #{request.ClientStatusId} не найден");
        }

        var inlineAssetError = ValidateInlineAssetPayload(request);
        if (inlineAssetError is not null)
        {
            return BadRequest(inlineAssetError);
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
            Address = ResolveLegacyAddressValue(request.Addresses, request.Address),
            Phone = NormalizeOptional(request.Phone),
            Email = NormalizeOptional(request.Email),
            Director = NormalizeOptional(request.Director),
            ResponsibleUserId = request.ResponsibleUserId,
            ClientStatusId = request.ClientStatusId,
            IsClientAndSupplier = request.IsClientAndSupplier,
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

        await ApplyLegalEntityAddressesAsync(entity, request.Addresses, ct);
        await _db.SaveChangesAsync(ct);

        var responseEntity = await LoadLegalEntityForDetailAsync(entity.Id, ct);
        return Created($"/api/legal-entities/{entity.Id}", MapDetail(responseEntity ?? entity, includeInlineAssets: false));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .Include(le => le.ClientStatus)
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

        var addresses = await _db.AddressRecords
            .Where(item => item.LegalEntityId == entity.Id)
            .ToListAsync(ct);
        if (addresses.Count > 0)
        {
            _db.AddressRecords.RemoveRange(addresses);
        }

        var companyAccesses = await _db.UserCompanyAccesses
            .Where(item => item.LegalEntityId == entity.Id)
            .ToListAsync(ct);
        if (companyAccesses.Count > 0)
        {
            _db.UserCompanyAccesses.RemoveRange(companyAccesses);
        }

        await RemoveCompanyFromMainCompanySettingsAsync(entity.Id, ct);
        _db.LegalEntities.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("main-companies/settings")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MainCompanySettingsDto>> GetMainCompanySettings(CancellationToken ct)
    {
        var state = await LoadMainCompanySettingsStateAsync(ct);
        var companies = state.CompanyIds.Count == 0
            ? new List<LegalEntity>()
            : await _db.LegalEntities
                .AsNoTracking()
                .Include(le => le.Clients)
                .Include(le => le.Addresses)
                .Include(le => le.ResponsibleUser)
                .Include(le => le.ClientStatus)
                .Where(le => state.CompanyIds.Contains(le.Id))
                .OrderBy(le => le.ShortName)
                .ToListAsync(ct);

        return Ok(new MainCompanySettingsDto
        {
            DefaultCompanyId = state.DefaultCompanyId,
            Companies = companies.Select(item => (LegalEntityListItemDto)MapDetail(item)).ToList(),
        });
    }

    [HttpPut("main-companies/settings")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MainCompanySettingsDto>> UpdateMainCompanySettings(
        [FromBody] UpdateMainCompanySettingsRequest request,
        CancellationToken ct)
    {
        var normalizedIds = (request.CompanyIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (normalizedIds.Count > 0)
        {
            var existingIds = await _db.LegalEntities
                .AsNoTracking()
                .Where(le => normalizedIds.Contains(le.Id))
                .Select(le => le.Id)
                .ToListAsync(ct);

            var missingIds = normalizedIds
                .Except(existingIds)
                .OrderBy(id => id)
                .ToArray();

            if (missingIds.Length > 0)
            {
                return NotFound(new
                {
                    message = "Не удалось найти часть выбранных компаний.",
                    missingIds,
                });
            }
        }

        var normalizedDefaultId = request.DefaultCompanyId.HasValue && normalizedIds.Contains(request.DefaultCompanyId.Value)
            ? request.DefaultCompanyId
            : (normalizedIds.Count > 0 ? normalizedIds[0] : null);

        await SaveMainCompanySettingsStateAsync(normalizedIds, normalizedDefaultId, ct);
        return await GetMainCompanySettings(ct);
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
            .Include(le => le.ClientStatus)
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
            .Include(le => le.ClientStatus)
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
            AreaWithType = details.AreaWithType,
            CityWithType = details.CityWithType,
            SettlementWithType = details.SettlementWithType,
            StreetWithType = details.StreetWithType,
            House = details.House,
            Flat = details.Flat,
            PostalCode = details.PostalCode,
            FiasId = details.FiasId,
            KladrId = details.KladrId,
            GeoLat = details.GeoLat,
            GeoLon = details.GeoLon,
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
    public async Task<ActionResult<IEnumerable<LegalEntityListItemDto>>> GetAll(
        [FromQuery] string? search,
        CancellationToken ct,
        [FromQuery] string? counterpartyRole = null)
    {
        var query = await ApplyCompanyVisibilityAsync(_db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .Include(le => le.ClientStatus)
            .AsQueryable(), ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(le =>
                le.ShortName.ToLower().Contains(term) ||
                (le.FullName ?? string.Empty).ToLower().Contains(term) ||
                (le.Inn ?? string.Empty).ToLower().Contains(term) ||
                (le.Kpp ?? string.Empty).ToLower().Contains(term) ||
                (le.Ogrn ?? string.Empty).ToLower().Contains(term));
        }

        query = query.ApplyCounterpartyRoleFilter(counterpartyRole);

        var items = await query
            .OrderBy(le => le.ShortName)
            .ToListAsync(ct);

        return Ok(items.Select(item => MapDetail(item)));
    }

    [HttpGet("paged")]
    public async Task<ActionResult<PagedResponse<LegalEntityListItemDto>>> GetPaged(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default,
        [FromQuery] string? counterpartyRole = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var baseQuery = await ApplyCompanyVisibilityAsync(_db.LegalEntities
            .AsQueryable(), ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            baseQuery = baseQuery.Where(le =>
                le.ShortName.ToLower().Contains(term) ||
                (le.FullName ?? string.Empty).ToLower().Contains(term) ||
                (le.Inn ?? string.Empty).ToLower().Contains(term) ||
                (le.Kpp ?? string.Empty).ToLower().Contains(term) ||
                (le.Ogrn ?? string.Empty).ToLower().Contains(term) ||
                (le.Director ?? string.Empty).ToLower().Contains(term) ||
                (le.Address ?? string.Empty).ToLower().Contains(term));
        }

        baseQuery = baseQuery.ApplyCounterpartyRoleFilter(counterpartyRole);

        var total = await baseQuery.CountAsync(ct);

        var items = await baseQuery
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .Include(le => le.ClientStatus)
            .OrderBy(le => le.ShortName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var itemIds = items.Select(item => item.Id).ToList();
        var casesCountByLegalEntity = itemIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.ClientCases
                .AsNoTracking()
                .Where(clientCase => clientCase.LegalEntityId.HasValue
                    && itemIds.Contains(clientCase.LegalEntityId.Value))
                .GroupBy(clientCase => clientCase.LegalEntityId!.Value)
                .Select(group => new { LegalEntityId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.LegalEntityId, item => item.Count, ct);

        var caseLinksByLegalEntity = new Dictionary<int, List<RelatedDocumentLink>>();
        if (itemIds.Count > 0)
        {
            var caseRows = await _db.ClientCases
                .AsNoTracking()
                .Where(clientCase => clientCase.LegalEntityId.HasValue
                    && itemIds.Contains(clientCase.LegalEntityId.Value))
                .OrderByDescending(clientCase => clientCase.CreatedAt)
                .ThenByDescending(clientCase => clientCase.Id)
                .Select(clientCase => new
                {
                    clientCase.Id,
                    clientCase.Title,
                    clientCase.Status,
                    clientCase.ClientId,
                    clientCase.LegalEntityId,
                    clientCase.CreatedAt,
                })
                .ToListAsync(ct);

            caseLinksByLegalEntity = caseRows
                .Where(row => row.LegalEntityId.HasValue)
                .GroupBy(row => row.LegalEntityId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => new RelatedDocumentLink
                    {
                        Id = row.Id,
                        Label = string.IsNullOrWhiteSpace(row.Title) ? $"Дело #{row.Id}" : row.Title,
                        SecondaryLabel = row.Status.ToString(),
                        ClientId = row.ClientId,
                        LegalEntityId = row.LegalEntityId,
                        Date = row.CreatedAt,
                    }).ToList());
        }

        var contractsCountByLegalEntity = itemIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.LegalEntityContracts
                .AsNoTracking()
                .Where(link => itemIds.Contains(link.LegalEntityId))
                .GroupBy(link => link.LegalEntityId)
                .Select(group => new { LegalEntityId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.LegalEntityId, item => item.Count, ct);

        var contractLinksByLegalEntity = new Dictionary<int, List<RelatedDocumentLink>>();
        if (itemIds.Count > 0)
        {
            var contractRows = await _db.LegalEntityContracts
                .AsNoTracking()
                .Where(link => itemIds.Contains(link.LegalEntityId))
                .Select(link => new
                {
                    link.LegalEntityId,
                    link.ContractId,
                    link.Contract.Number,
                    link.Contract.Title,
                    link.Contract.Date,
                })
                .ToListAsync(ct);

            contractLinksByLegalEntity = contractRows
                .GroupBy(row => row.LegalEntityId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(row => row.Date)
                        .Select(row => new RelatedDocumentLink
                        {
                            Id = row.ContractId,
                            Label = !string.IsNullOrWhiteSpace(row.Number)
                                ? $"№{row.Number}"
                                : !string.IsNullOrWhiteSpace(row.Title)
                                    ? row.Title!
                                    : $"Договор #{row.ContractId}",
                            SecondaryLabel = row.Title,
                            LegalEntityId = row.LegalEntityId,
                            Date = row.Date,
                        }).ToList());
        }

        var actsCountByLegalEntity = itemIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.Acts
                .AsNoTracking()
                .Where(act => act.LegalEntityId.HasValue
                    && itemIds.Contains(act.LegalEntityId.Value))
                .GroupBy(act => act.LegalEntityId!.Value)
                .Select(group => new { LegalEntityId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.LegalEntityId, item => item.Count, ct);

        var actLinksByLegalEntity = new Dictionary<int, List<RelatedDocumentLink>>();
        if (itemIds.Count > 0)
        {
            var actRows = await _db.Acts
                .AsNoTracking()
                .Where(act => act.LegalEntityId.HasValue
                    && itemIds.Contains(act.LegalEntityId.Value))
                .Select(act => new
                {
                    act.Id,
                    act.Number,
                    act.Title,
                    act.Date,
                    act.Amount,
                    act.ClientId,
                    act.LegalEntityId,
                })
                .ToListAsync(ct);

            actLinksByLegalEntity = actRows
                .Where(row => row.LegalEntityId.HasValue)
                .GroupBy(row => row.LegalEntityId!.Value)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(row => row.Date)
                        .Select(row => new RelatedDocumentLink
                        {
                            Id = row.Id,
                            Label = !string.IsNullOrWhiteSpace(row.Number)
                                ? $"№{row.Number}"
                                : !string.IsNullOrWhiteSpace(row.Title)
                                    ? row.Title!
                                    : $"Акт #{row.Id}",
                            SecondaryLabel = row.Title,
                            ClientId = row.ClientId,
                            LegalEntityId = row.LegalEntityId,
                            Date = row.Date,
                            Amount = row.Amount,
                        }).ToList());
        }

        var invoicesCountByLegalEntity = new Dictionary<int, int>();
        var paymentsCountByLegalEntity = new Dictionary<int, int>();
        var invoiceLinksByLegalEntity = new Dictionary<int, List<RelatedDocumentLink>>();
        var paymentLinksByLegalEntity = new Dictionary<int, List<RelatedDocumentLink>>();
        if (itemIds.Count > 0)
        {
            var invoiceRows = await _db.Payments
                .AsNoTracking()
                .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
                .Where(payment =>
                    (payment.LegalEntityId.HasValue
                        && itemIds.Contains(payment.LegalEntityId.Value)) ||
                    (payment.ExpenseLegalEntityId.HasValue
                        && itemIds.Contains(payment.ExpenseLegalEntityId.Value)))
                .Select(payment => new
                {
                    payment.Id,
                    payment.LegalEntityId,
                    payment.ExpenseLegalEntityId,
                    payment.Account,
                    payment.LegacyAccountNumber,
                    payment.Date,
                    payment.Amount,
                    payment.Type,
                })
                .ToListAsync(ct);

            void AddInvoiceLink(int legalEntityId, RelatedDocumentLink link)
            {
                if (!invoiceLinksByLegalEntity.TryGetValue(legalEntityId, out var links))
                {
                    links = new List<RelatedDocumentLink>();
                    invoiceLinksByLegalEntity[legalEntityId] = links;
                }

                links.Add(link);
            }

            foreach (var row in invoiceRows)
            {
                var number = !string.IsNullOrWhiteSpace(row.Account) ? row.Account : row.LegacyAccountNumber;
                var link = new RelatedDocumentLink
                {
                    Id = row.Id,
                    Label = !string.IsNullOrWhiteSpace(number) ? $"№{number}" : $"Счет #{row.Id}",
                    LegalEntityId = row.LegalEntityId ?? row.ExpenseLegalEntityId,
                    Date = row.Date,
                    Amount = row.Amount,
                    Type = row.Type.ToString(),
                };

                if (row.LegalEntityId.HasValue && itemIds.Contains(row.LegalEntityId.Value))
                {
                    invoicesCountByLegalEntity[row.LegalEntityId.Value] = invoicesCountByLegalEntity.GetValueOrDefault(row.LegalEntityId.Value) + 1;
                    AddInvoiceLink(row.LegalEntityId.Value, link);
                }

                if (row.ExpenseLegalEntityId.HasValue
                    && itemIds.Contains(row.ExpenseLegalEntityId.Value)
                    && row.ExpenseLegalEntityId != row.LegalEntityId)
                {
                    invoicesCountByLegalEntity[row.ExpenseLegalEntityId.Value] = invoicesCountByLegalEntity.GetValueOrDefault(row.ExpenseLegalEntityId.Value) + 1;
                    AddInvoiceLink(row.ExpenseLegalEntityId.Value, link);
                }
            }

            var paymentRows = await _db.Payments
                .AsNoTracking()
                .Where(payment => payment.DocumentKind != DocumentKind.Invoice)
                .Where(payment =>
                    (payment.LegalEntityId.HasValue
                        && itemIds.Contains(payment.LegalEntityId.Value)) ||
                    (payment.ExpenseLegalEntityId.HasValue
                        && itemIds.Contains(payment.ExpenseLegalEntityId.Value)))
                .Select(payment => new
                {
                    payment.Id,
                    payment.LegalEntityId,
                    payment.ExpenseLegalEntityId,
                    payment.PaymentOrderNumber,
                    payment.Description,
                    payment.Date,
                    payment.Amount,
                    payment.Type,
                })
                .ToListAsync(ct);

            void AddPaymentLink(int legalEntityId, RelatedDocumentLink link)
            {
                if (!paymentLinksByLegalEntity.TryGetValue(legalEntityId, out var links))
                {
                    links = new List<RelatedDocumentLink>();
                    paymentLinksByLegalEntity[legalEntityId] = links;
                }

                links.Add(link);
            }

            foreach (var row in paymentRows)
            {
                var link = new RelatedDocumentLink
                {
                    Id = row.Id,
                    Label = !string.IsNullOrWhiteSpace(row.PaymentOrderNumber)
                        ? $"№{row.PaymentOrderNumber}"
                        : !string.IsNullOrWhiteSpace(row.Description)
                            ? row.Description
                            : $"Платеж #{row.Id}",
                    LegalEntityId = row.LegalEntityId ?? row.ExpenseLegalEntityId,
                    Date = row.Date,
                    Amount = row.Amount,
                    Type = row.Type.ToString(),
                };

                if (row.LegalEntityId.HasValue && itemIds.Contains(row.LegalEntityId.Value))
                {
                    paymentsCountByLegalEntity[row.LegalEntityId.Value] = paymentsCountByLegalEntity.GetValueOrDefault(row.LegalEntityId.Value) + 1;
                    AddPaymentLink(row.LegalEntityId.Value, link);
                }

                if (row.ExpenseLegalEntityId.HasValue
                    && itemIds.Contains(row.ExpenseLegalEntityId.Value)
                    && row.ExpenseLegalEntityId != row.LegalEntityId)
                {
                    paymentsCountByLegalEntity[row.ExpenseLegalEntityId.Value] = paymentsCountByLegalEntity.GetValueOrDefault(row.ExpenseLegalEntityId.Value) + 1;
                    AddPaymentLink(row.ExpenseLegalEntityId.Value, link);
                }
            }
        }

        return Ok(new PagedResponse<LegalEntityListItemDto>
        {
            Items = items.Select(item =>
            {
                var dto = MapDetail(item);
                dto.CasesCount = casesCountByLegalEntity.GetValueOrDefault(item.Id);
                dto.ContractsCount = contractsCountByLegalEntity.GetValueOrDefault(item.Id);
                dto.InvoicesCount = invoicesCountByLegalEntity.GetValueOrDefault(item.Id);
                dto.ActsCount = actsCountByLegalEntity.GetValueOrDefault(item.Id);
                dto.PaymentsCount = paymentsCountByLegalEntity.GetValueOrDefault(item.Id);
                dto.RelatedCases = caseLinksByLegalEntity.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                dto.RelatedContracts = contractLinksByLegalEntity.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                dto.RelatedInvoices = invoiceLinksByLegalEntity.GetValueOrDefault(item.Id)?.OrderByDescending(link => link.Date).ToList() ?? new List<RelatedDocumentLink>();
                dto.RelatedActs = actLinksByLegalEntity.GetValueOrDefault(item.Id) ?? new List<RelatedDocumentLink>();
                dto.RelatedPayments = paymentLinksByLegalEntity.GetValueOrDefault(item.Id)?.OrderByDescending(link => link.Date).ToList() ?? new List<RelatedDocumentLink>();
                return dto;
            }).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> GetById(int id, CancellationToken ct)
    {
        var mainCompanyIds = (await LoadMainCompanySettingsStateAsync(ct)).CompanyIds;
        if (!IsAdmin() && mainCompanyIds.Contains(id))
        {
            return NotFound();
        }

        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .Include(le => le.Addresses)
            .Include(le => le.ResponsibleUser)
            .Include(le => le.ClientStatus)
            .FirstOrDefaultAsync(le => le.Id == id, ct);

        if (entity is null)
        {
            return NotFound();
        }

        return Ok(MapDetail(entity));
    }

    [HttpGet("{id:int}/case-finance-summaries")]
    public async Task<ActionResult<IEnumerable<ClientCaseFinanceSummaryDto>>> GetCaseFinanceSummaries(int id, CancellationToken ct)
    {
        var visibleQuery = await ApplyCompanyVisibilityAsync(_db.LegalEntities.AsQueryable(), ct);
        var exists = await visibleQuery.AsNoTracking().AnyAsync(entity => entity.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        var cases = await _db.ClientCases
            .AsNoTracking()
            .Where(clientCase => clientCase.LegalEntityId == id)
            .OrderByDescending(clientCase => clientCase.CreatedAt)
            .ThenByDescending(clientCase => clientCase.Id)
            .Select(clientCase => new
            {
                clientCase.Id,
                clientCase.Title,
                clientCase.Status,
                clientCase.CreatedAt,
            })
            .ToListAsync(ct);

        if (cases.Count == 0)
        {
            return Ok(Array.Empty<ClientCaseFinanceSummaryDto>());
        }

        var caseIds = cases.Select(item => item.Id).ToList();
        var caseIdSet = caseIds.ToHashSet();
        var summaries = cases.ToDictionary(
            item => item.Id,
            item => new ClientCaseFinanceSummaryDto
            {
                CaseId = item.Id,
                CaseTitle = item.Title,
                CaseStatus = item.Status.ToString(),
                CaseCreatedAt = item.CreatedAt,
            });

        var payments = await _db.Payments
            .AsNoTracking()
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment => payment.Status != PaymentStatus.Cancelled)
            .Where(payment =>
                (payment.Type == PaymentType.Income && payment.LegalEntityId == id) ||
                (payment.Type == PaymentType.Expense &&
                    (payment.ExpenseLegalEntityId == id ||
                     (payment.ExpenseLegalEntityId == null && payment.LegalEntityId == id))))
            .Where(payment =>
                (payment.ClientCaseId.HasValue && caseIds.Contains(payment.ClientCaseId.Value)) ||
                payment.InvoiceClientCases.Any(link => caseIds.Contains(link.ClientCaseId)))
            .ToListAsync(ct);

        var visiblePayments = InvoiceManagedPaymentRules.FilterVisiblePayments(payments);

        foreach (var payment in visiblePayments)
        {
            var linkedCaseIds = payment.InvoiceClientCases
                .Select(link => link.ClientCaseId)
                .Concat(payment.ClientCaseId.HasValue ? new[] { payment.ClientCaseId.Value } : Array.Empty<int>())
                .Where(caseIdSet.Contains)
                .Distinct();

            foreach (var caseId in linkedCaseIds)
            {
                var summary = summaries[caseId];
                var outstanding = payment.Amount > payment.PaidAmount
                    ? Math.Round(payment.Amount - payment.PaidAmount, 2)
                    : 0m;

                if (payment.Type == PaymentType.Income)
                {
                    summary.IncomeTotal += payment.Amount;
                    summary.IncomePaid += payment.PaidAmount;
                    summary.IncomeOutstanding += outstanding;
                    summary.IncomeCount += 1;
                }
                else
                {
                    summary.ExpenseTotal += payment.Amount;
                    summary.ExpensePaid += payment.PaidAmount;
                    summary.ExpenseOutstanding += outstanding;
                    summary.ExpenseCount += 1;
                }
            }
        }

        return Ok(caseIds.Select(caseId => summaries[caseId]).ToList());
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
        var response = DeduplicateLegalEntitySuggestions(suggestions, request.Limit).Select(s => new LegalEntitySuggestionDto
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
    [RequestSizeLimit(MaxLegalEntityRequestSizeBytes)]
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
            .Include(le => le.ClientStatus)
            .FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!await ResponsibleUserExistsAsync(request.ResponsibleUserId, ct))
        {
            return BadRequest("Указанный ответственный не найден или неактивен");
        }

        if (!await ClientStatusExistsAsync(request.ClientStatusId, ct))
        {
            return BadRequest($"Статус клиента #{request.ClientStatusId} не найден");
        }

        var inlineAssetError = ValidateInlineAssetPayload(request);
        if (inlineAssetError is not null)
        {
            return BadRequest(inlineAssetError);
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
        entity.Address = ResolveLegacyAddressValue(request.Addresses, request.Address);
        entity.Phone = NormalizeOptional(request.Phone);
        entity.Email = NormalizeOptional(request.Email);
        entity.Director = NormalizeOptional(request.Director);
        entity.ResponsibleUserId = request.ResponsibleUserId;
        entity.ClientStatusId = request.ClientStatusId;
        entity.IsClientAndSupplier = request.IsClientAndSupplier;
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
        entity.LogoDataUrl = ResolveUpdatedAssetValue(entity.LogoDataUrl, request.LogoDataUrl, request.LogoChanged);
        entity.SealDataUrl = ResolveUpdatedAssetValue(entity.SealDataUrl, request.SealDataUrl, request.SealChanged);
        entity.DirectorSignatureDataUrl = ResolveUpdatedAssetValue(entity.DirectorSignatureDataUrl, request.DirectorSignatureDataUrl, request.DirectorSignatureChanged);
        entity.DocumentDirectorJson = NormalizeOptional(request.DocumentDirectorJson);
        entity.ChiefAccountantJson = NormalizeOptional(request.ChiefAccountantJson);
        entity.ContactPhonesJson = NormalizeOptional(request.ContactPhonesJson);
        entity.ContactEmailsJson = NormalizeOptional(request.ContactEmailsJson);
        entity.Notes = NormalizeOptional(request.Notes);
        entity.UpdatedAt = DateTime.UtcNow;

        await EnsureAndNormalizeDictionaryValuesAsync(entity, ct);
        await ApplyClientLinksAsync(entity.Id, clientIds, ct);
        await ApplyLegalEntityAddressesAsync(entity, request.Addresses, ct);
        await _db.SaveChangesAsync(ct);

        var responseEntity = await LoadLegalEntityForDetailAsync(entity.Id, ct);
        return Ok(MapDetail(responseEntity ?? entity, includeInlineAssets: false));
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
                    AreaWithType: NormalizeOptional(first.AreaWithType),
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
            changed |= SetIfDifferent(target.AreaWithType, v => target.AreaWithType = v, NormalizeOptional(cleaned?.AreaWithType));
            changed |= SetIfDifferent(target.CityWithType, v => target.CityWithType = v, NormalizeOptional(cleaned?.CityWithType));
            changed |= SetIfDifferent(target.SettlementWithType, v => target.SettlementWithType = v, NormalizeOptional(cleaned?.SettlementWithType));
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
        changed |= FillIfEmpty(target.AreaWithType, v => target.AreaWithType = v, NormalizeOptional(cleaned?.AreaWithType));
        changed |= FillIfEmpty(target.CityWithType, v => target.CityWithType = v, NormalizeOptional(cleaned?.CityWithType));
        changed |= FillIfEmpty(target.SettlementWithType, v => target.SettlementWithType = v, NormalizeOptional(cleaned?.SettlementWithType));
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
    public string? AreaWithType { get; init; }
    public string? CityWithType { get; init; }
    public string? SettlementWithType { get; init; }
    public string? StreetWithType { get; init; }
    public string? House { get; init; }
    public string? Flat { get; init; }
    public string? PostalCode { get; init; }
    public string? FiasId { get; init; }
    public string? KladrId { get; init; }
    public string? GeoLat { get; init; }
    public string? GeoLon { get; init; }
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
    public string? AreaWithType { get; init; }
    public string Result { get; init; } = string.Empty;
    public string? SettlementWithType { get; init; }
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

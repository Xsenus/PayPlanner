using System.Text.Json;
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

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private const string CLIENT_PROFILE_NOTES_PREFIX = "__PP_CLIENT_PROFILE_V2__:";
    private static readonly JsonSerializerOptions ClientProfileJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly ILegalEntityEnrichmentService _enrichment;
    private readonly IFormRequirementsService _formRequirements;

    public ClientsController(PaymentContext db, ILegalEntityEnrichmentService enrichment, IFormRequirementsService formRequirements)
    {
        _db = db;
        _enrichment = enrichment;
        _formRequirements = formRequirements;
    }

    private IQueryable<Client> BuildVisibleClientsQuery(int? currentUserId, bool isAdmin)
        => _db.Clients.ApplyClientVisibility(isAdmin, currentUserId);

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

    private static string? FormatResponsibleUserName(User? user)
    {
        if (user is null) return null;
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
    }

    private static void PopulateResponsibleUserNames(IEnumerable<Client> clients)
    {
        foreach (var client in clients)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
    }

    private static void PopulateAuditMetadata(PaymentContext db, Client client)
    {
        var entry = db.Entry(client);
        client.AuditCreatedByUserId = entry.Property<int?>("CreatedByUserId").CurrentValue;
        client.AuditCreatedByUserName = entry.Property<string?>("CreatedByUserName").CurrentValue;
        client.AuditUpdatedAt = entry.Property<DateTime?>("UpdatedAt").CurrentValue;
        client.AuditUpdatedByUserId = entry.Property<int?>("UpdatedByUserId").CurrentValue;
        client.AuditUpdatedByUserName = entry.Property<string?>("UpdatedByUserName").CurrentValue;
        client.AuditIsDeleted = entry.Property<bool>("IsDeleted").CurrentValue;
        client.AuditDeletedAt = entry.Property<DateTime?>("DeletedAt").CurrentValue;
        client.AuditDeletedByUserId = entry.Property<int?>("DeletedByUserId").CurrentValue;
        client.AuditDeletedByUserName = entry.Property<string?>("DeletedByUserName").CurrentValue;
    }

    private static void PopulateAuditMetadata(PaymentContext db, ClientCase clientCase)
    {
        var entry = db.Entry(clientCase);
        clientCase.AuditCreatedByUserId = entry.Property<int?>("CreatedByUserId").CurrentValue;
        clientCase.AuditCreatedByUserName = entry.Property<string?>("CreatedByUserName").CurrentValue;
        clientCase.AuditUpdatedAt = entry.Property<DateTime?>("UpdatedAt").CurrentValue;
        clientCase.AuditUpdatedByUserId = entry.Property<int?>("UpdatedByUserId").CurrentValue;
        clientCase.AuditUpdatedByUserName = entry.Property<string?>("UpdatedByUserName").CurrentValue;
        clientCase.AuditIsDeleted = entry.Property<bool>("IsDeleted").CurrentValue;
        clientCase.AuditDeletedAt = entry.Property<DateTime?>("DeletedAt").CurrentValue;
        clientCase.AuditDeletedByUserId = entry.Property<int?>("DeletedByUserId").CurrentValue;
        clientCase.AuditDeletedByUserName = entry.Property<string?>("DeletedByUserName").CurrentValue;
    }

    private static T? DeserializeClientProfile<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(raw, ClientProfileJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    private static List<int> NormalizeIds(IEnumerable<int>? ids)
    {
        return ids?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<int>();
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var clients = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.Addresses)
            .Include(c => c.ResponsibleUser)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        PopulateResponsibleUserNames(clients);
        return Ok(clients);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.Addresses)
            .Include(c => c.ResponsibleUser)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is not null)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
            PopulateAuditMetadata(_db, client);
            foreach (var clientCase in client.Cases)
            {
                PopulateAuditMetadata(_db, clientCase);
            }
        }
        return client is null ? NotFound() : Ok(client);
    }

    [HttpGet("{id:int}/stats")]
    public async Task<IActionResult> GetStats(int id, [FromQuery] int? caseId, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();

        var q = _db.Payments.Where(p =>
                (p.Type == PaymentType.Income && p.ClientId == id) ||
                (p.Type == PaymentType.Expense &&
                    (p.ExpenseClientId == id || (p.ExpenseClientId == null && p.ClientId == id))))
            .Include(p => p.DealType).Include(p => p.IncomeType)
            .Include(p => p.PaymentSource).Include(p => p.PaymentStatusEntity).AsQueryable();

        if (caseId.HasValue)
        {
            q = q.Where(p =>
                p.ClientCaseId == caseId.Value ||
                p.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value));
        }

        var payments = await q
            .OrderByDescending(p => p.Date)
            .AsNoTracking()
            .ToListAsync(ct);

        var totalIncome = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.PaidAmount);
        var totalExpenses = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.PaidAmount);
        var outstandingIncome = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.OutstandingAmount);
        var outstandingExpenses = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.OutstandingAmount);
        var paidPayments = payments.Count(p => p.Status == PaymentStatus.Completed);
        var pendingPayments = payments.Count(p => p.Status != PaymentStatus.Completed && p.OutstandingAmount > 0m);
        var overduePayments = payments.Count(p => p.Status == PaymentStatus.Overdue && p.OutstandingAmount > 0m);
        var lastPaymentDate = payments
            .Where(p => p.LastPaymentDate.HasValue)
            .OrderByDescending(p => p.LastPaymentDate)
            .Select(p => p.LastPaymentDate)
            .FirstOrDefault()
            ?? payments.OrderByDescending(p => p.Date).Select(p => (DateTime?)p.Date).FirstOrDefault();

        var stats = new ClientStats
        {
            ClientId = client.Id,
            ClientName = client.Name,
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            OutstandingIncome = outstandingIncome,
            OutstandingExpenses = outstandingExpenses,
            TotalPayments = payments.Count,
            PaidPayments = paidPayments,
            PendingPayments = pendingPayments,
            OverduePayments = overduePayments,
            LastPaymentDate = lastPaymentDate,
            RecentPayments = payments.ToList()
        };
        stats.NetAmount = stats.TotalIncome - stats.TotalExpenses;
        return Ok(stats);
    }

    [HttpGet("{id:int}/case-finance-summaries")]
    public async Task<IActionResult> GetCaseFinanceSummaries(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();

        var caseIds = await _db.ClientCases
            .AsNoTracking()
            .Where(c => c.ClientId == id)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var summaries = caseIds.ToDictionary(
            caseId => caseId,
            caseId => new ClientCaseFinanceSummaryDto { CaseId = caseId });

        if (caseIds.Count == 0)
        {
            return Ok(Array.Empty<ClientCaseFinanceSummaryDto>());
        }

        var caseIdSet = caseIds.ToHashSet();

        var payments = await _db.Payments
            .AsNoTracking()
            .Include(p => p.InvoiceClientCases)
            .Where(p => p.Status != PaymentStatus.Cancelled)
            .Where(p =>
                (p.Type == PaymentType.Income && p.ClientId == id) ||
                (p.Type == PaymentType.Expense &&
                    (p.ExpenseClientId == id || (p.ExpenseClientId == null && p.ClientId == id))))
            .Where(p =>
                (p.ClientCaseId.HasValue && caseIds.Contains(p.ClientCaseId.Value)) ||
                p.InvoiceClientCases.Any(link => caseIds.Contains(link.ClientCaseId)))
            .ToListAsync(ct);

        foreach (var payment in payments)
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


    [HttpPost("dadata/clean-address")]
    public async Task<IActionResult> CleanAddress([FromBody] ClientCleanAddressRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Address)) return BadRequest("Укажите Address");

        var result = await _enrichment.CleanAddressAsync(request.Address, ct);
        if (result is null) return NotFound();

        return Ok(new
        {
            source = result.Source,
            result = result.Result,
            postalCode = result.PostalCode,
            country = result.Country,
            countryIsoCode = result.CountryIsoCode,
            federalDistrict = result.FederalDistrict,
            regionWithType = result.RegionWithType,
            areaWithType = result.AreaWithType,
            cityWithType = result.CityWithType,
            settlementWithType = result.SettlementWithType,
            streetWithType = result.StreetWithType,
            house = result.House,
            block = result.Block,
            flat = result.Flat,
            fiasId = result.FiasId,
            fiasLevel = result.FiasLevel,
            kladrId = result.KladrId,
            okato = result.Okato,
            oktmo = result.Oktmo,
            taxOffice = result.TaxOffice,
            taxOfficeLegal = result.TaxOfficeLegal,
            geoLat = result.GeoLat,
            geoLon = result.GeoLon,
            qc = result.Qc,
            qcComplete = result.QcComplete,
            qcHouse = result.QcHouse,
            qcGeo = result.QcGeo,
            unparsedParts = result.UnparsedParts,
            timezone = result.Timezone,
        });
    }

    [HttpPost("dadata/suggest-address")]
    public async Task<IActionResult> SuggestAddress([FromBody] ClientSuggestAddressRequest request, CancellationToken ct)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return Ok(Array.Empty<object>());

        var suggestions = await _enrichment.SuggestAddressesAsync(query, request.Limit, ct);
        return Ok(suggestions.Select(s => new
        {
            value = s.Value,
            unrestrictedValue = s.UnrestrictedValue,
            postalCode = s.PostalCode,
            fiasId = s.FiasId,
            kladrId = s.KladrId,
            geoLat = s.GeoLat,
            geoLon = s.GeoLon,
            regionWithType = s.RegionWithType,
            areaWithType = s.AreaWithType,
            cityWithType = s.CityWithType,
            settlementWithType = s.SettlementWithType,
            streetWithType = s.StreetWithType,
            house = s.House,
            flat = s.Flat,
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model, CancellationToken ct)
    {
        var requirementsModel = BuildClientRequirementsModel(model);
        var requiredQuickFields = await _formRequirements.GetRequiredFieldsAsync("clients-modal", ct);
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("clients-modal", requirementsModel, ct);
        missingCreate = NormalizeQuickClientModalMissingFields(requiredQuickFields, requirementsModel, missingCreate);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        var access = await GetCurrentAccessAsync(ct);
        if (!access.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (model.LegalEntityId.HasValue)
        {
            var exists = await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == model.LegalEntityId.Value);
            if (!exists)
            {
                return BadRequest($"Юридическое лицо #{model.LegalEntityId.Value} не найдено");
            }
        }

        if (model.ClientStatusId.HasValue)
        {
            var statusExists = await _db.ClientStatuses.AsNoTracking().AnyAsync(s => s.Id == model.ClientStatusId.Value);
            if (!statusExists)
            {
                return BadRequest($"Статус клиента #{model.ClientStatusId.Value} не найден");
            }
        }

        var responsibleUserId = access.IsAdmin
            ? (model.ResponsibleUserId ?? access.UserId.Value)
            : access.UserId.Value;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        var entity = new Client
        {
            Name = model.Name,
            Email = model.Email,
            Phone = model.Phone,
            Company = model.Company,
            Address = model.Address,
            Notes = model.Notes,
            IsActive = model.IsActive,
            LegalEntityId = model.LegalEntityId,
            ResponsibleUserId = responsibleUserId,
            ClientStatusId = model.ClientStatusId,
        };

        _db.Clients.Add(entity);
        await _db.SaveChangesAsync(ct);
        var profilePayload = ExtractProfilePayloadFromNotes(model.Notes);
        await SyncClientAddressesAsync(entity, profilePayload);
        await SyncClientPassportAsync(entity, profilePayload);
        await _db.SaveChangesAsync(ct);
        var response = await LoadClientResponseAsync(entity.Id, ct);
        return Created($"/api/clients/{entity.Id}", response);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("clients", BuildClientRequirementsModel(model));
        missingUpdate = missingUpdate
            .Where(field => !string.Equals(field, "Email", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "Phone", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();
        if (model.LegalEntityId.HasValue)
        {
            var exists = await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == model.LegalEntityId.Value);
            if (!exists)
            {
                return BadRequest($"Юридическое лицо #{model.LegalEntityId.Value} не найдено");
            }
        }

        if (model.ClientStatusId.HasValue)
        {
            var statusExists = await _db.ClientStatuses.AsNoTracking().AnyAsync(s => s.Id == model.ClientStatusId.Value);
            if (!statusExists)
            {
                return BadRequest($"Статус клиента #{model.ClientStatusId.Value} не найден");
            }
        }
        var responsibleUserId = access.IsAdmin
            ? model.ResponsibleUserId
            : e.ResponsibleUserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        var profilePayload = ExtractProfilePayloadFromNotes(model.Notes);
        await SyncClientAddressesAsync(e, profilePayload);
        await SyncClientPassportAsync(e, profilePayload);
        e.IsActive = model.IsActive;
        e.LegalEntityId = model.LegalEntityId;
        e.ResponsibleUserId = responsibleUserId;
        e.ClientStatusId = model.ClientStatusId;
        await _db.SaveChangesAsync(ct);
        var response = await LoadClientResponseAsync(e.Id, ct);
        return Ok(response);
    }


    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Client> LoadClientResponseAsync(int clientId, CancellationToken ct)
    {
        var client = await _db.Clients
            .AsNoTracking()
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .Include(c => c.Addresses)
            .Include(c => c.Passports)
            .FirstAsync(c => c.Id == clientId, ct);

        client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        return client;
    }

    private static string? ExtractProfilePayloadFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var raw = notes.Trim();
        if (!raw.StartsWith(CLIENT_PROFILE_NOTES_PREFIX, StringComparison.Ordinal)) return null;
        return raw[CLIENT_PROFILE_NOTES_PREFIX.Length..].Trim();
    }

    private async Task SyncClientAddressesAsync(Client client, string? profilePayload)
    {
        var profile = DeserializeClientProfile<ClientAddressProfile>(profilePayload?.Trim());
        if (profile is null) return;

        var existing = await _db.AddressRecords.Where(x => x.ClientId == client.Id).ToListAsync();

        if (profile.AddressEntries is { Count: > 0 })
        {
            var normalizedEntries = profile.AddressEntries
                .Select((item, index) => new AddressRecord
                {
                    ClientId = client.Id,
                    Type = ParseAddressType(item.Type),
                    IsPrimary = index == 0,
                    Value = NormalizeOptional(item.FullAddress) ?? NormalizeOptional(item.SourceQuery) ?? string.Empty,
                    UnrestrictedValue = NormalizeOptional(item.FullAddress),
                    PostalCode = NormalizeOptional(item.PostalCode),
                    Country = NormalizeOptional(item.Country),
                    RegionWithType = NormalizeOptional(item.Region),
                    AreaWithType = NormalizeOptional(item.District),
                    CityWithType = NormalizeOptional(item.City),
                    SettlementWithType = NormalizeOptional(item.Settlement),
                    StreetWithType = NormalizeOptional(item.Street),
                    House = NormalizeOptional(item.House),
                    Flat = NormalizeOptional(item.Flat),
                    FiasId = NormalizeOptional(item.FiasId),
                    KladrId = NormalizeOptional(item.KladrId),
                    GeoLat = NormalizeOptional(item.GeoLat),
                    GeoLon = NormalizeOptional(item.GeoLon),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .ToList();

            if (existing.Count > 0)
            {
                _db.AddressRecords.RemoveRange(existing);
            }

            if (normalizedEntries.Count > 0)
            {
                _db.AddressRecords.AddRange(normalizedEntries);
            }

            return;
        }

        // backward compatibility for legacy single-value address fields
        var registrationValue = NormalizeOptional(profile.RegistrationAddress);
        var actualValue = NormalizeOptional(profile.ActualAddressMatchesRegistration == true
            ? profile.RegistrationAddress
            : profile.ActualAddress);

        UpsertAddress(existing, client.Id, AddressType.Registration, true,
            registrationValue, null, profile.RegistrationAddressPostalCode, profile.RegistrationAddressFiasId, profile.RegistrationAddressKladrId,
            profile.RegistrationAddressGeoLat, profile.RegistrationAddressGeoLon);

        UpsertAddress(existing, client.Id, AddressType.Actual, true,
            actualValue, null, profile.ActualAddressPostalCode, profile.ActualAddressFiasId, profile.ActualAddressKladrId,
            profile.ActualAddressGeoLat, profile.ActualAddressGeoLon);

        var activeTypes = new HashSet<AddressType>();
        if (!string.IsNullOrWhiteSpace(registrationValue)) activeTypes.Add(AddressType.Registration);
        if (!string.IsNullOrWhiteSpace(actualValue)) activeTypes.Add(AddressType.Actual);

        foreach (var stale in existing.Where(x => !activeTypes.Contains(x.Type)).ToList())
        {
            _db.AddressRecords.Remove(stale);
        }
    }


    private async Task SyncClientPassportAsync(Client client, string? profilePayload)
    {
        var profile = DeserializeClientProfile<ClientAddressProfile>(profilePayload?.Trim());
        if (profile is null) return;

        var existing = await _db.ClientPassports.Where(x => x.ClientId == client.Id).ToListAsync();

        var normalizedPassports = (profile.PassportEntries is { Count: > 0 }
                ? profile.PassportEntries.Select((item, index) => new { Item = item, Index = index })
                : new[]
                {
                    new
                    {
                        Item = new ClientPassportProfile
                        {
                            Series = profile.PassportSeries,
                            Number = profile.PassportNumber,
                            IssuedBy = profile.PassportIssuedBy,
                            IssueDate = profile.PassportIssueDate,
                            DepartmentCode = profile.PassportDepartmentCode,
                            BirthPlacePassport = profile.BirthPlacePassport,
                            BirthPlaceFias = profile.BirthPlaceFias,
                        },
                        Index = 0,
                    },
                })
            .Select(x => new ClientPassport
            {
                ClientId = client.Id,
                Series = NormalizeOptional(x.Item.Series),
                Number = NormalizeOptional(x.Item.Number),
                IssuedBy = NormalizeOptional(x.Item.IssuedBy),
                IssuedAt = x.Item.IssueDate,
                DepartmentCode = NormalizeOptional(x.Item.DepartmentCode),
                BirthPlacePassport = NormalizeOptional(x.Item.BirthPlacePassport),
                BirthPlaceFias = NormalizeOptional(x.Item.BirthPlaceFias),
                ValidFrom = x.Item.ValidFrom,
                ValidTo = x.Item.ValidTo,
                IsPrimary = x.Index == 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Series)
                        || !string.IsNullOrWhiteSpace(x.Number)
                        || !string.IsNullOrWhiteSpace(x.IssuedBy)
                        || x.IssuedAt.HasValue
                        || !string.IsNullOrWhiteSpace(x.DepartmentCode))
            .ToList();

        if (existing.Count > 0)
        {
            _db.ClientPassports.RemoveRange(existing);
        }

        if (normalizedPassports.Count > 0)
        {
            _db.ClientPassports.AddRange(normalizedPassports);
        }
    }

    private static AddressType ParseAddressType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AddressType.Other;
        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Contains("reg") || normalized.Contains("рег")) return AddressType.Registration;
        if (normalized.Contains("act") || normalized.Contains("факт")) return AddressType.Actual;
        if (normalized.Contains("legal") || normalized.Contains("юр")) return AddressType.Legal;
        return AddressType.Other;
    }

    private void UpsertAddress(
        List<AddressRecord> existing,
        int clientId,
        AddressType type,
        bool isPrimary,
        string? value,
        string? unrestrictedValue,
        string? postalCode,
        string? fiasId,
        string? kladrId,
        string? geoLat,
        string? geoLon)
    {
        var current = existing.FirstOrDefault(x => x.Type == type);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (current is not null)
            {
                _db.AddressRecords.Remove(current);
                existing.Remove(current);
            }
            return;
        }

        if (current is null)
        {
            current = new AddressRecord
            {
                ClientId = clientId,
                Type = type,
                CreatedAt = DateTime.UtcNow,
            };
            existing.Add(current);
            _db.AddressRecords.Add(current);
        }

        current.Value = value;
        current.UnrestrictedValue = unrestrictedValue;
        current.PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        current.FiasId = string.IsNullOrWhiteSpace(fiasId) ? null : fiasId.Trim();
        current.KladrId = string.IsNullOrWhiteSpace(kladrId) ? null : kladrId.Trim();
        current.GeoLat = string.IsNullOrWhiteSpace(geoLat) ? null : geoLat.Trim();
        current.GeoLon = string.IsNullOrWhiteSpace(geoLon) ? null : geoLon.Trim();
        current.IsPrimary = isPrimary;
        current.UpdatedAt = DateTime.UtcNow;
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var usage = await GetClientUsageSummary(id);
        if (usage.Count > 0)
        {
            return Conflict(new
            {
                message = "Клиент используется в связанных данных и не может быть удалён.",
                usage,
            });
        }

        await RemoveClientProfileArtifactsAsync(new[] { id }, ct);
        _db.Clients.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkIdsRequest request, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Не выбраны клиенты для удаления." });
        }

        var clients = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);

        var foundIds = clients.Select(client => client.Id).ToHashSet();
        var missingIds = ids.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
        {
            return NotFound(new
            {
                message = "Не удалось найти часть клиентов для удаления.",
                missingIds,
            });
        }

        var blockedClients = new List<object>();
        foreach (var client in clients)
        {
            var usage = await GetClientUsageSummary(client.Id);
            if (usage.Count > 0)
            {
                blockedClients.Add(new
                {
                    id = client.Id,
                    name = client.Name,
                    usage,
                });
            }
        }

        if (blockedClients.Count > 0)
        {
            return Conflict(new
            {
                message = "Один или несколько клиентов используются в связанных данных и не могут быть удалены.",
                blockedClients,
            });
        }

        await RemoveClientProfileArtifactsAsync(ids, ct);
        _db.Clients.RemoveRange(clients);
        await _db.SaveChangesAsync(ct);

        return Ok(new { deleted = clients.Count });
    }

    [HttpPost("change-responsible")]
    public async Task<IActionResult> ChangeResponsible([FromBody] BulkAssignResponsibleRequest request, CancellationToken ct)
    {
        var access = await GetCurrentAccessAsync(ct);
        if (!access.IsAdmin && !access.UserId.HasValue)
        {
            return Unauthorized();
        }

        var ids = NormalizeIds(request.Ids);
        if (ids.Count == 0)
        {
            return BadRequest(new { message = "Не выбраны клиенты для смены ответственного." });
        }

        if (!await ResponsibleUserExistsAsync(request.ResponsibleUserId, ct))
        {
            return BadRequest(new { message = $"Ответственный #{request.ResponsibleUserId} не найден." });
        }

        var clients = await BuildVisibleClientsQuery(access.UserId, access.IsAdmin)
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);

        var foundIds = clients.Select(client => client.Id).ToHashSet();
        var missingIds = ids.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
        {
            return NotFound(new
            {
                message = "Не удалось найти часть клиентов для смены ответственного.",
                missingIds,
            });
        }

        foreach (var client in clients)
        {
            client.ResponsibleUserId = request.ResponsibleUserId;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            updated = clients.Count,
            responsibleUserId = request.ResponsibleUserId,
        });
    }

    private async Task<List<string>> GetClientUsageSummary(int clientId)
    {
        var usage = new List<string>();

        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.ClientId == clientId || p.ExpenseClientId == clientId))
            usage.Add("платежи");

        if (await _db.Acts.AsNoTracking().AnyAsync(a => a.ClientId == clientId))
            usage.Add("акты");

        if (await _db.ClientCases.AsNoTracking().AnyAsync(c => c.ClientId == clientId))
            usage.Add("дела клиента");

        if (await _db.ClientContracts.AsNoTracking().AnyAsync(cc => cc.ClientId == clientId))
            usage.Add("договоры");

        if (await _db.PowersOfAttorney.AsNoTracking().AnyAsync(p => p.ClientId == clientId))
            usage.Add("доверенности");

        return usage;
    }

    private async Task RemoveClientProfileArtifactsAsync(IEnumerable<int> clientIds, CancellationToken ct)
    {
        var normalizedIds = NormalizeIds(clientIds);
        if (normalizedIds.Count == 0)
        {
            return;
        }

        var addresses = await _db.AddressRecords
            .Where(item => item.ClientId.HasValue && normalizedIds.Contains(item.ClientId.Value))
            .ToListAsync(ct);
        if (addresses.Count > 0)
        {
            _db.AddressRecords.RemoveRange(addresses);
        }

        var passports = await _db.ClientPassports
            .Where(item => normalizedIds.Contains(item.ClientId))
            .ToListAsync(ct);
        if (passports.Count > 0)
        {
            _db.ClientPassports.RemoveRange(passports);
        }
    }

    private static List<string> NormalizeQuickClientModalMissingFields(
        IEnumerable<string> requiredFields,
        ClientRequirementsModel model,
        IEnumerable<string> missingFields)
    {
        var requiredSet = new HashSet<string>(requiredFields, StringComparer.OrdinalIgnoreCase);
        var normalized = new HashSet<string>(
            missingFields.Where(field =>
                !string.Equals(field, "Email", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "Notes", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        if (requiredSet.Contains("Name"))
        {
            if (string.IsNullOrWhiteSpace(model.LastName)) normalized.Add("LastName");
            if (string.IsNullOrWhiteSpace(model.FirstName)) normalized.Add("FirstName");
            if (string.IsNullOrWhiteSpace(model.MiddleName)) normalized.Add("MiddleName");
        }

        if (requiredSet.Contains("Notes") && string.IsNullOrWhiteSpace(model.Comment))
        {
            normalized.Add("Comment");
        }

        return normalized.ToList();
    }

    private static ClientRequirementsModel BuildClientRequirementsModel(Client model)
    {
        var profilePayload = ExtractProfilePayloadFromNotes(model.Notes);
        var profile = DeserializeClientProfile<ClientRequirementsProfile>(profilePayload);

        return new ClientRequirementsModel
        {
            Name = model.Name,
            Email = model.Email,
            Phone = model.Phone,
            Company = model.Company,
            Address = model.Address,
            Notes = model.Notes,
            IsActive = model.IsActive,
            LegalEntityId = model.LegalEntityId,
            ResponsibleUserId = model.ResponsibleUserId,
            ClientStatusId = model.ClientStatusId,

            LastName = profile?.LastName,
            FirstName = profile?.FirstName,
            MiddleName = profile?.MiddleName,
            Gender = profile?.Gender,
            BirthDate = profile?.BirthDate,
            Inn = profile?.Inn,
            Snils = profile?.Snils,

            Phones = profile?.Phones ?? new List<string>(),
            Emails = profile?.Emails ?? new List<string>(),
            Addresses = profile?.Addresses ?? new List<string>(),
            AddressEntries = profile?.AddressEntries ?? new List<object>(),
            Passports = profile?.Passports ?? new List<string>(),
            PassportEntries = profile?.PassportEntries ?? new List<object>(),
            BirthCertificates = profile?.BirthCertificates ?? new List<string>(),
            BirthCertificateEntries = profile?.BirthCertificateEntries ?? new List<object>(),
            MilitaryIds = profile?.MilitaryIds ?? new List<string>(),
            MilitaryIdEntries = profile?.MilitaryIdEntries ?? new List<object>(),
            Education = profile?.Education ?? new List<object>(),
            Professions = profile?.Professions ?? new List<object>(),
            Workplaces = profile?.Workplaces ?? new List<object>(),
            Relatives = profile?.Relatives ?? new List<object>(),

            PowersOfAttorney = model.PowersOfAttorney?.Select(x => x.Id).ToList() ?? new List<int>(),
            Comment = profile?.Comment,
            LegacyNotes = profile?.LegacyNotes,
        };
    }
}

public sealed record ClientCleanAddressRequest(string Address);
public sealed record ClientSuggestAddressRequest(string Query, int Limit = 7);


public sealed class ClientRequirementsModel
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Company { get; init; }
    public string? Address { get; init; }
    public string? Notes { get; init; }
    public bool IsActive { get; init; }
    public int? LegalEntityId { get; init; }
    public int? ResponsibleUserId { get; init; }
    public int? ClientStatusId { get; init; }

    public string? LastName { get; init; }
    public string? FirstName { get; init; }
    public string? MiddleName { get; init; }
    public string? Gender { get; init; }
    public string? BirthDate { get; init; }
    public string? Inn { get; init; }
    public string? Snils { get; init; }

    public List<string> Phones { get; init; } = new();
    public List<string> Emails { get; init; } = new();
    public List<string> Addresses { get; init; } = new();
    public List<object> AddressEntries { get; init; } = new();
    public List<string> Passports { get; init; } = new();
    public List<object> PassportEntries { get; init; } = new();
    public List<string> BirthCertificates { get; init; } = new();
    public List<object> BirthCertificateEntries { get; init; } = new();
    public List<string> MilitaryIds { get; init; } = new();
    public List<object> MilitaryIdEntries { get; init; } = new();
    public List<object> Education { get; init; } = new();
    public List<object> Professions { get; init; } = new();
    public List<object> Workplaces { get; init; } = new();
    public List<object> Relatives { get; init; } = new();
    public List<int> PowersOfAttorney { get; init; } = new();

    public string? Comment { get; init; }
    public string? LegacyNotes { get; init; }
}

public sealed class ClientRequirementsProfile
{
    public string? LastName { get; init; }
    public string? FirstName { get; init; }
    public string? MiddleName { get; init; }
    public string? Gender { get; init; }
    public string? BirthDate { get; init; }
    public string? Inn { get; init; }
    public string? Snils { get; init; }

    public List<string>? Phones { get; init; }
    public List<string>? Emails { get; init; }
    public List<string>? Addresses { get; init; }
    public List<object>? AddressEntries { get; init; }
    public List<string>? Passports { get; init; }
    public List<object>? PassportEntries { get; init; }
    public List<string>? BirthCertificates { get; init; }
    public List<object>? BirthCertificateEntries { get; init; }
    public List<string>? MilitaryIds { get; init; }
    public List<object>? MilitaryIdEntries { get; init; }
    public List<object>? Education { get; init; }
    public List<object>? Professions { get; init; }
    public List<object>? Workplaces { get; init; }
    public List<object>? Relatives { get; init; }

    public string? Comment { get; init; }
    public string? LegacyNotes { get; init; }
}

public sealed class ClientAddressProfile
{
    public string? RegistrationAddress { get; init; }
    public string? RegistrationAddressPostalCode { get; init; }
    public string? RegistrationAddressFiasId { get; init; }
    public string? RegistrationAddressKladrId { get; init; }
    public string? RegistrationAddressGeoLat { get; init; }
    public string? RegistrationAddressGeoLon { get; init; }
    public string? ActualAddress { get; init; }
    public string? ActualAddressPostalCode { get; init; }
    public string? ActualAddressFiasId { get; init; }
    public string? ActualAddressKladrId { get; init; }
    public string? ActualAddressGeoLat { get; init; }
    public string? ActualAddressGeoLon { get; init; }
    public bool? ActualAddressMatchesRegistration { get; init; }

    public List<ClientAddressEntryProfile>? AddressEntries { get; init; }

    public string? PassportSeries { get; init; }
    public string? PassportNumber { get; init; }
    public string? PassportIssuedBy { get; init; }
    public DateTime? PassportIssueDate { get; init; }
    public string? PassportDepartmentCode { get; init; }
    public string? BirthPlacePassport { get; init; }
    public string? BirthPlaceFias { get; init; }

    public List<ClientPassportProfile>? PassportEntries { get; init; }
}

public sealed class ClientAddressEntryProfile
{
    public string? Type { get; init; }
    public string? SourceQuery { get; init; }
    public string? FullAddress { get; init; }
    public string? PostalCode { get; init; }
    public string? Country { get; init; }
    public string? Region { get; init; }
    public string? District { get; init; }
    public string? City { get; init; }
    public string? Settlement { get; init; }
    public string? Street { get; init; }
    public string? House { get; init; }
    public string? Flat { get; init; }
    public string? FiasId { get; init; }
    public string? KladrId { get; init; }
    public string? GeoLat { get; init; }
    public string? GeoLon { get; init; }
}

public sealed class ClientPassportProfile
{
    public string? Series { get; init; }
    public string? Number { get; init; }
    public string? IssuedBy { get; init; }
    public DateTime? IssueDate { get; init; }
    public string? DepartmentCode { get; init; }
    public string? BirthPlacePassport { get; init; }
    public string? BirthPlaceFias { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
}

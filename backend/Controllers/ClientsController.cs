using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private const string CLIENT_PROFILE_NOTES_PREFIX = "__PP_CLIENT_PROFILE_V2__:";

    private readonly PaymentContext _db;
    private readonly ILegalEntityEnrichmentService _enrichment;

    public ClientsController(PaymentContext db, ILegalEntityEnrichmentService enrichment)
    {
        _db = db;
        _enrichment = enrichment;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await _db.Clients
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.Addresses)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.Addresses)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return client is null ? NotFound() : Ok(client);
    }

    [HttpGet("{id:int}/stats")]
    public async Task<IActionResult> GetStats(int id, [FromQuery] int? caseId, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();

        var q = _db.Payments.Where(p => p.ClientId == id)
            .Include(p => p.DealType).Include(p => p.IncomeType)
            .Include(p => p.PaymentSource).Include(p => p.PaymentStatusEntity).AsQueryable();

        if (caseId.HasValue) q = q.Where(p => p.ClientCaseId == caseId.Value);

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


    [HttpPost("dadata/clean-address")]
    public async Task<IActionResult> CleanAddress([FromBody] CleanAddressRequest request, CancellationToken ct)
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
    public async Task<IActionResult> SuggestAddress([FromBody] SuggestAddressRequest request, CancellationToken ct)
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
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model)
    {
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
            ClientStatusId = model.ClientStatusId,
        };

        _db.Clients.Add(entity);
        await _db.SaveChangesAsync();
        var profilePayload = ExtractProfilePayloadFromNotes(model.Notes);
        await SyncClientAddressesAsync(entity, profilePayload);
        await SyncClientPassportAsync(entity, profilePayload);
        await _db.SaveChangesAsync();
        await _db.Entry(entity).Reference(c => c.LegalEntity).LoadAsync();
        await _db.Entry(entity).Reference(c => c.ClientStatus).LoadAsync();
        return Created($"/api/clients/{entity.Id}", entity);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model)
    {
        var e = await _db.Clients.FindAsync(id);
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
        e.ClientStatusId = model.ClientStatusId;
        await _db.SaveChangesAsync();
        await _db.Entry(e).Reference(c => c.LegalEntity).LoadAsync();
        await _db.Entry(e).Reference(c => c.ClientStatus).LoadAsync();
        return Ok(e);
    }


    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractProfilePayloadFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var raw = notes.Trim();
        if (!raw.StartsWith(CLIENT_PROFILE_NOTES_PREFIX, StringComparison.Ordinal)) return null;
        return raw[CLIENT_PROFILE_NOTES_PREFIX.Length..].Trim();
    }

    private async Task SyncClientAddressesAsync(Client client, string? profilePayload)
    {
        var raw = profilePayload?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        ClientAddressProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ClientAddressProfile>(raw);
        }
        catch
        {
            return;
        }

        if (profile is null) return;

        var registrationValue = profile.RegistrationAddress?.Trim();
        var actualValue = (profile.ActualAddressMatchesRegistration == true
            ? profile.RegistrationAddress
            : profile.ActualAddress)?.Trim();

        var existing = await _db.AddressRecords.Where(x => x.ClientId == client.Id).ToListAsync();

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
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.Clients.FindAsync(id);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync();
        foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
        await _db.SaveChangesAsync();

        _db.Clients.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public sealed record CleanAddressRequest(string Address);
public sealed record SuggestAddressRequest(string Query, int Limit = 7);

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
    public string? PassportSeries { get; init; }
    public string? PassportNumber { get; init; }
    public string? PassportIssuedBy { get; init; }
    public DateTime? PassportIssueDate { get; init; }
    public string? PassportDepartmentCode { get; init; }
}

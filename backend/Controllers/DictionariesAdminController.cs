using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/dictionaries")]
[Authorize(Policy = "Admin")]
public class DictionariesAdminController : ControllerBase
{
    private readonly PaymentContext _db;
    public DictionariesAdminController(PaymentContext db) => _db = db;

    private IActionResult InUseError(string title)
        => BadRequest($"Нельзя удалить элемент справочника \"{title}\", так как он используется в данных.");

    // DealType
    [HttpPost("deal-types")]
    public async Task<IActionResult> CreateDealType([FromBody] DealType m)
    {
        _db.DealTypes.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/deal-types/{m.Id}", m);
    }
    [HttpPut("deal-types/{id:int}")]
    public async Task<IActionResult> UpdateDealType(int id, [FromBody] DealType m)
    {
        var e = await _db.DealTypes.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("deal-types/{id:int}")]
    public async Task<IActionResult> DeleteDealType(int id)
    {
        var e = await _db.DealTypes.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.DealTypeId == id))
            return InUseError(e.Name);
        _db.DealTypes.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("deal-types/{id:int}/toggle")]
    public async Task<IActionResult> ToggleDealType(int id)
    {
        var e = await _db.DealTypes.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // IncomeType
    [HttpPost("income-types")]
    public async Task<IActionResult> CreateIncomeType([FromBody] IncomeType m)
    {
        _db.IncomeTypes.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/income-types/{m.Id}", m);
    }
    [HttpPut("income-types/{id:int}")]
    public async Task<IActionResult> UpdateIncomeType(int id, [FromBody] IncomeType m)
    {
        var e = await _db.IncomeTypes.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive; e.PaymentType = m.PaymentType;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("income-types/{id:int}")]
    public async Task<IActionResult> DeleteIncomeType(int id)
    {
        var e = await _db.IncomeTypes.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.IncomeTypeId == id))
            return InUseError(e.Name);
        _db.IncomeTypes.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("income-types/{id:int}/toggle")]
    public async Task<IActionResult> ToggleIncomeType(int id)
    {
        var e = await _db.IncomeTypes.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // PaymentSource
    [HttpPost("payment-sources")]
    public async Task<IActionResult> CreatePaymentSource([FromBody] PaymentSource m)
    {
        _db.PaymentSources.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/payment-sources/{m.Id}", m);
    }
    [HttpPut("payment-sources/{id:int}")]
    public async Task<IActionResult> UpdatePaymentSource(int id, [FromBody] PaymentSource m)
    {
        var e = await _db.PaymentSources.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("payment-sources/{id:int}")]
    public async Task<IActionResult> DeletePaymentSource(int id)
    {
        var e = await _db.PaymentSources.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.PaymentSourceId == id))
            return InUseError(e.Name);
        _db.PaymentSources.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("payment-sources/{id:int}/toggle")]
    public async Task<IActionResult> TogglePaymentSource(int id)
    {
        var e = await _db.PaymentSources.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // ServiceDictionaryItem
    [HttpPost("services")]
    public async Task<IActionResult> CreateService([FromBody] ServiceDictionaryItem m)
    {
        _db.Services.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/services/{m.Id}", m);
    }
    [HttpPut("services/{id:int}")]
    public async Task<IActionResult> UpdateService(int id, [FromBody] ServiceDictionaryItem m)
    {
        var e = await _db.Services.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("services/{id:int}")]
    public async Task<IActionResult> DeleteService(int id)
    {
        var e = await _db.Services.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.ServiceId == id))
            return InUseError(e.Name);
        _db.Services.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("services/{id:int}/toggle")]
    public async Task<IActionResult> ToggleService(int id)
    {
        var e = await _db.Services.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }


    // CountryDictionaryItem
    [HttpPost("countries")]
    public async Task<IActionResult> CreateCountry([FromBody] CountryDictionaryItem m)
    {
        _db.Countries.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/countries/{m.Id}", m);
    }
    [HttpPut("countries/{id:int}")]
    public async Task<IActionResult> UpdateCountry(int id, [FromBody] CountryDictionaryItem m)
    {
        var e = await _db.Countries.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("countries/{id:int}")]
    public async Task<IActionResult> DeleteCountry(int id)
    {
        var e = await _db.Countries.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Country == e.Name))
            return InUseError(e.Name);
        _db.Countries.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("countries/{id:int}/toggle")]
    public async Task<IActionResult> ToggleCountry(int id)
    {
        var e = await _db.Countries.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // BaseCurrencyDictionaryItem
    [HttpPost("base-currencies")]
    public async Task<IActionResult> CreateBaseCurrency([FromBody] BaseCurrencyDictionaryItem m)
    {
        _db.BaseCurrencies.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/base-currencies/{m.Id}", m);
    }
    [HttpPut("base-currencies/{id:int}")]
    public async Task<IActionResult> UpdateBaseCurrency(int id, [FromBody] BaseCurrencyDictionaryItem m)
    {
        var e = await _db.BaseCurrencies.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("base-currencies/{id:int}")]
    public async Task<IActionResult> DeleteBaseCurrency(int id)
    {
        var e = await _db.BaseCurrencies.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.BaseCurrency == e.Name))
            return InUseError(e.Name);
        _db.BaseCurrencies.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("base-currencies/{id:int}/toggle")]
    public async Task<IActionResult> ToggleBaseCurrency(int id)
    {
        var e = await _db.BaseCurrencies.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // ServiceLanguageDictionaryItem
    [HttpPost("service-languages")]
    public async Task<IActionResult> CreateServiceLanguage([FromBody] ServiceLanguageDictionaryItem m)
    {
        _db.ServiceLanguages.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/service-languages/{m.Id}", m);
    }
    [HttpPut("service-languages/{id:int}")]
    public async Task<IActionResult> UpdateServiceLanguage(int id, [FromBody] ServiceLanguageDictionaryItem m)
    {
        var e = await _db.ServiceLanguages.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("service-languages/{id:int}")]
    public async Task<IActionResult> DeleteServiceLanguage(int id)
    {
        var e = await _db.ServiceLanguages.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.ServiceLanguage == e.Name))
            return InUseError(e.Name);
        _db.ServiceLanguages.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("service-languages/{id:int}/toggle")]
    public async Task<IActionResult> ToggleServiceLanguage(int id)
    {
        var e = await _db.ServiceLanguages.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // OrganizationFormDictionaryItem
    [HttpPost("organization-forms")]
    public async Task<IActionResult> CreateOrganizationForm([FromBody] OrganizationFormDictionaryItem m)
    {
        _db.OrganizationForms.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/organization-forms/{m.Id}", m);
    }
    [HttpPut("organization-forms/{id:int}")]
    public async Task<IActionResult> UpdateOrganizationForm(int id, [FromBody] OrganizationFormDictionaryItem m)
    {
        var e = await _db.OrganizationForms.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("organization-forms/{id:int}")]
    public async Task<IActionResult> DeleteOrganizationForm(int id)
    {
        var e = await _db.OrganizationForms.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.OrganizationForm == e.Name))
            return InUseError(e.Name);
        _db.OrganizationForms.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("organization-forms/{id:int}/toggle")]
    public async Task<IActionResult> ToggleOrganizationForm(int id)
    {
        var e = await _db.OrganizationForms.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // TimezoneDictionaryItem
    [HttpPost("timezones")]
    public async Task<IActionResult> CreateTimezone([FromBody] TimezoneDictionaryItem m)
    {
        _db.Timezones.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/timezones/{m.Id}", m);
    }
    [HttpPut("timezones/{id:int}")]
    public async Task<IActionResult> UpdateTimezone(int id, [FromBody] TimezoneDictionaryItem m)
    {
        var e = await _db.Timezones.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("timezones/{id:int}")]
    public async Task<IActionResult> DeleteTimezone(int id)
    {
        var e = await _db.Timezones.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Timezone == e.Name))
            return InUseError(e.Name);
        _db.Timezones.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("timezones/{id:int}/toggle")]
    public async Task<IActionResult> ToggleTimezone(int id)
    {
        var e = await _db.Timezones.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // ClientStatusEntity
    [HttpPost("client-statuses")]
    public async Task<IActionResult> CreateClientStatus([FromBody] ClientStatusEntity m)
    {
        _db.ClientStatuses.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/client-statuses/{m.Id}", m);
    }

    [HttpPut("client-statuses/{id:int}")]
    public async Task<IActionResult> UpdateClientStatus(int id, [FromBody] ClientStatusEntity m)
    {
        var e = await _db.ClientStatuses.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }

    [HttpDelete("client-statuses/{id:int}")]
    public async Task<IActionResult> DeleteClientStatus(int id)
    {
        var e = await _db.ClientStatuses.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Clients.AsNoTracking().AnyAsync(c => c.ClientStatusId == id))
            return InUseError(e.Name);
        _db.ClientStatuses.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("client-statuses/{id:int}/toggle")]
    public async Task<IActionResult> ToggleClientStatus(int id)
    {
        var e = await _db.ClientStatuses.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // PaymentStatusEntity
    [HttpPost("payment-statuses")]
    public async Task<IActionResult> CreatePaymentStatus([FromBody] PaymentStatusEntity m)
    {
        _db.PaymentStatuses.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/payment-statuses/{m.Id}", m);
    }
    [HttpPut("payment-statuses/{id:int}")]
    public async Task<IActionResult> UpdatePaymentStatus(int id, [FromBody] PaymentStatusEntity m)
    {
        var e = await _db.PaymentStatuses.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("payment-statuses/{id:int}")]
    public async Task<IActionResult> DeletePaymentStatus(int id)
    {
        var e = await _db.PaymentStatuses.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Payments.AsNoTracking().AnyAsync(p => p.PaymentStatusId == id))
            return InUseError(e.Name);
        _db.PaymentStatuses.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("payment-statuses/{id:int}/toggle")]
    public async Task<IActionResult> TogglePaymentStatus(int id)
    {
        var e = await _db.PaymentStatuses.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }
}

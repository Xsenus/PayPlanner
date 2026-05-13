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

    private static string CanonicalizeDictionaryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static DocumentStatusSeed? ResolveDocumentStatusSeed(string? code)
    {
        var normalized = DocumentWorkflowStatus.Normalize(code);
        if (normalized is null) return null;

        foreach (var item in DocumentWorkflowStatus.Defaults)
        {
            if (string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static void ApplyMandatoryDocumentStatusRules(DocumentStatusEntity status, DocumentStatusSeed seed)
    {
        if (!DocumentWorkflowStatus.IsMandatory(seed.Code)) return;

        status.Name = seed.Name;
        status.Description = seed.Description;
        status.ColorHex = string.IsNullOrWhiteSpace(status.ColorHex) ? seed.ColorHex : status.ColorHex;
        status.IsActive = true;
        status.UseForInvoices = seed.UseForInvoices;
        status.UseForActs = seed.UseForActs;
        status.UseForContracts = seed.UseForContracts;
        status.SortOrder = seed.SortOrder;
    }

    private sealed record SimpleDictionaryMeta(
        Func<CancellationToken, Task<List<object>>> LoadAsNoTrackingAsync,
        Func<CancellationToken, Task<List<object>>> LoadTrackingAsync,
        Action<LegalEntity, string?> AssignLegalEntityField,
        Func<LegalEntity, string?> ReadLegalEntityField,
        Func<object, int> GetId,
        Func<object, string> GetName,
        Func<object, string> GetDescription,
        Func<object, string> GetColorHex,
        Func<object, bool> GetIsActive,
        Action<object, string> SetName,
        Action<object, string> SetDescription,
        Action<object, string> SetColorHex,
        Action<object, bool> SetIsActive,
        Action<object> AddEntity,
        Action<object> RemoveEntity);

    private SimpleDictionaryMeta? ResolveSimpleDictionaryMeta(string kind)
    {
        return kind switch
        {
            "countries" => new(
                ct => _db.Countries.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.Countries.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.Country = v,
                le => le.Country,
                x => ((CountryDictionaryItem)x).Id,
                x => ((CountryDictionaryItem)x).Name,
                x => ((CountryDictionaryItem)x).Description,
                x => ((CountryDictionaryItem)x).ColorHex,
                x => ((CountryDictionaryItem)x).IsActive,
                (x, v) => ((CountryDictionaryItem)x).Name = v,
                (x, v) => ((CountryDictionaryItem)x).Description = v,
                (x, v) => ((CountryDictionaryItem)x).ColorHex = v,
                (x, v) => ((CountryDictionaryItem)x).IsActive = v,
                x => _db.Countries.Add((CountryDictionaryItem)x),
                x => _db.Countries.Remove((CountryDictionaryItem)x)),
            "base-currencies" => new(
                ct => _db.BaseCurrencies.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.BaseCurrencies.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.BaseCurrency = v,
                le => le.BaseCurrency,
                x => ((BaseCurrencyDictionaryItem)x).Id,
                x => ((BaseCurrencyDictionaryItem)x).Name,
                x => ((BaseCurrencyDictionaryItem)x).Description,
                x => ((BaseCurrencyDictionaryItem)x).ColorHex,
                x => ((BaseCurrencyDictionaryItem)x).IsActive,
                (x, v) => ((BaseCurrencyDictionaryItem)x).Name = v,
                (x, v) => ((BaseCurrencyDictionaryItem)x).Description = v,
                (x, v) => ((BaseCurrencyDictionaryItem)x).ColorHex = v,
                (x, v) => ((BaseCurrencyDictionaryItem)x).IsActive = v,
                x => _db.BaseCurrencies.Add((BaseCurrencyDictionaryItem)x),
                x => _db.BaseCurrencies.Remove((BaseCurrencyDictionaryItem)x)),
            "service-languages" => new(
                ct => _db.ServiceLanguages.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.ServiceLanguages.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.ServiceLanguage = v,
                le => le.ServiceLanguage,
                x => ((ServiceLanguageDictionaryItem)x).Id,
                x => ((ServiceLanguageDictionaryItem)x).Name,
                x => ((ServiceLanguageDictionaryItem)x).Description,
                x => ((ServiceLanguageDictionaryItem)x).ColorHex,
                x => ((ServiceLanguageDictionaryItem)x).IsActive,
                (x, v) => ((ServiceLanguageDictionaryItem)x).Name = v,
                (x, v) => ((ServiceLanguageDictionaryItem)x).Description = v,
                (x, v) => ((ServiceLanguageDictionaryItem)x).ColorHex = v,
                (x, v) => ((ServiceLanguageDictionaryItem)x).IsActive = v,
                x => _db.ServiceLanguages.Add((ServiceLanguageDictionaryItem)x),
                x => _db.ServiceLanguages.Remove((ServiceLanguageDictionaryItem)x)),
            "service-categories" => new(
                ct => _db.ServiceCategories.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.ServiceCategories.Select(item => (object)item).ToListAsync(ct),
                (_, __) => { },
                _ => string.Empty,
                x => ((ServiceCategoryDictionaryItem)x).Id,
                x => ((ServiceCategoryDictionaryItem)x).Name,
                x => ((ServiceCategoryDictionaryItem)x).Description,
                x => ((ServiceCategoryDictionaryItem)x).ColorHex,
                x => ((ServiceCategoryDictionaryItem)x).IsActive,
                (x, v) => ((ServiceCategoryDictionaryItem)x).Name = v,
                (x, v) => ((ServiceCategoryDictionaryItem)x).Description = v,
                (x, v) => ((ServiceCategoryDictionaryItem)x).ColorHex = v,
                (x, v) => ((ServiceCategoryDictionaryItem)x).IsActive = v,
                x => _db.ServiceCategories.Add((ServiceCategoryDictionaryItem)x),
                x => _db.ServiceCategories.Remove((ServiceCategoryDictionaryItem)x)),
            "organization-forms" => new(
                ct => _db.OrganizationForms.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.OrganizationForms.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.OrganizationForm = v,
                le => le.OrganizationForm,
                x => ((OrganizationFormDictionaryItem)x).Id,
                x => ((OrganizationFormDictionaryItem)x).Name,
                x => ((OrganizationFormDictionaryItem)x).Description,
                x => ((OrganizationFormDictionaryItem)x).ColorHex,
                x => ((OrganizationFormDictionaryItem)x).IsActive,
                (x, v) => ((OrganizationFormDictionaryItem)x).Name = v,
                (x, v) => ((OrganizationFormDictionaryItem)x).Description = v,
                (x, v) => ((OrganizationFormDictionaryItem)x).ColorHex = v,
                (x, v) => ((OrganizationFormDictionaryItem)x).IsActive = v,
                x => _db.OrganizationForms.Add((OrganizationFormDictionaryItem)x),
                x => _db.OrganizationForms.Remove((OrganizationFormDictionaryItem)x)),
            "timezones" => new(
                ct => _db.Timezones.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.Timezones.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.Timezone = v,
                le => le.Timezone,
                x => ((TimezoneDictionaryItem)x).Id,
                x => ((TimezoneDictionaryItem)x).Name,
                x => ((TimezoneDictionaryItem)x).Description,
                x => ((TimezoneDictionaryItem)x).ColorHex,
                x => ((TimezoneDictionaryItem)x).IsActive,
                (x, v) => ((TimezoneDictionaryItem)x).Name = v,
                (x, v) => ((TimezoneDictionaryItem)x).Description = v,
                (x, v) => ((TimezoneDictionaryItem)x).ColorHex = v,
                (x, v) => ((TimezoneDictionaryItem)x).IsActive = v,
                x => _db.Timezones.Add((TimezoneDictionaryItem)x),
                x => _db.Timezones.Remove((TimezoneDictionaryItem)x)),
            "tax-systems" => new(
                ct => _db.TaxSystems.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.TaxSystems.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.TaxSystem = v,
                le => le.TaxSystem,
                x => ((TaxSystemDictionaryItem)x).Id,
                x => ((TaxSystemDictionaryItem)x).Name,
                x => ((TaxSystemDictionaryItem)x).Description,
                x => ((TaxSystemDictionaryItem)x).ColorHex,
                x => ((TaxSystemDictionaryItem)x).IsActive,
                (x, v) => ((TaxSystemDictionaryItem)x).Name = v,
                (x, v) => ((TaxSystemDictionaryItem)x).Description = v,
                (x, v) => ((TaxSystemDictionaryItem)x).ColorHex = v,
                (x, v) => ((TaxSystemDictionaryItem)x).IsActive = v,
                x => _db.TaxSystems.Add((TaxSystemDictionaryItem)x),
                x => _db.TaxSystems.Remove((TaxSystemDictionaryItem)x)),
            "tax-system-modes" => new(
                ct => _db.TaxSystemModes.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.TaxSystemModes.Select(item => (object)item).ToListAsync(ct),
                (le, v) => le.TaxSystemMode = v,
                le => le.TaxSystemMode,
                x => ((TaxSystemModeDictionaryItem)x).Id,
                x => ((TaxSystemModeDictionaryItem)x).Name,
                x => ((TaxSystemModeDictionaryItem)x).Description,
                x => ((TaxSystemModeDictionaryItem)x).ColorHex,
                x => ((TaxSystemModeDictionaryItem)x).IsActive,
                (x, v) => ((TaxSystemModeDictionaryItem)x).Name = v,
                (x, v) => ((TaxSystemModeDictionaryItem)x).Description = v,
                (x, v) => ((TaxSystemModeDictionaryItem)x).ColorHex = v,
                (x, v) => ((TaxSystemModeDictionaryItem)x).IsActive = v,
                x => _db.TaxSystemModes.Add((TaxSystemModeDictionaryItem)x),
                x => _db.TaxSystemModes.Remove((TaxSystemModeDictionaryItem)x)),
            "invoice-units" => new(
                ct => _db.InvoiceUnits.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.InvoiceUnits.Select(item => (object)item).ToListAsync(ct),
                (_, __) => { },
                _ => string.Empty,
                x => ((InvoiceUnitDictionaryItem)x).Id,
                x => ((InvoiceUnitDictionaryItem)x).Name,
                x => ((InvoiceUnitDictionaryItem)x).Description,
                x => ((InvoiceUnitDictionaryItem)x).ColorHex,
                x => ((InvoiceUnitDictionaryItem)x).IsActive,
                (x, v) => ((InvoiceUnitDictionaryItem)x).Name = v,
                (x, v) => ((InvoiceUnitDictionaryItem)x).Description = v,
                (x, v) => ((InvoiceUnitDictionaryItem)x).ColorHex = v,
                (x, v) => ((InvoiceUnitDictionaryItem)x).IsActive = v,
                x => _db.InvoiceUnits.Add((InvoiceUnitDictionaryItem)x),
                x => _db.InvoiceUnits.Remove((InvoiceUnitDictionaryItem)x)),
            "invoice-item-names" => new(
                ct => _db.InvoiceItemNames.AsNoTracking().Select(item => (object)item).ToListAsync(ct),
                ct => _db.InvoiceItemNames.Select(item => (object)item).ToListAsync(ct),
                (_, __) => { },
                _ => string.Empty,
                x => ((InvoiceItemNameDictionaryItem)x).Id,
                x => ((InvoiceItemNameDictionaryItem)x).Name,
                x => ((InvoiceItemNameDictionaryItem)x).Description,
                x => ((InvoiceItemNameDictionaryItem)x).ColorHex,
                x => ((InvoiceItemNameDictionaryItem)x).IsActive,
                (x, v) => ((InvoiceItemNameDictionaryItem)x).Name = v,
                (x, v) => ((InvoiceItemNameDictionaryItem)x).Description = v,
                (x, v) => ((InvoiceItemNameDictionaryItem)x).ColorHex = v,
                (x, v) => ((InvoiceItemNameDictionaryItem)x).IsActive = v,
                x => _db.InvoiceItemNames.Add((InvoiceItemNameDictionaryItem)x),
                x => _db.InvoiceItemNames.Remove((InvoiceItemNameDictionaryItem)x)),
            _ => null,
        };
    }

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
        if (m.CategoryId.HasValue && !await _db.ServiceCategories.AsNoTracking().AnyAsync(c => c.Id == m.CategoryId.Value))
        {
            return BadRequest($"Unknown serviceCategoryId {m.CategoryId.Value}");
        }
        _db.Services.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/services/{m.Id}", m);
    }
    [HttpPut("services/{id:int}")]
    public async Task<IActionResult> UpdateService(int id, [FromBody] ServiceDictionaryItem m)
    {
        var e = await _db.Services.FindAsync(id);
        if (e is null) return NotFound();
        if (m.CategoryId.HasValue && !await _db.ServiceCategories.AsNoTracking().AnyAsync(c => c.Id == m.CategoryId.Value))
        {
            return BadRequest($"Unknown serviceCategoryId {m.CategoryId.Value}");
        }
        e.Name = m.Name;
        e.Description = m.Description;
        e.Unit = m.Unit;
        e.DefaultPrice = m.DefaultPrice;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        e.CategoryId = m.CategoryId;
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
        if (await _db.InvoiceLineItems.AsNoTracking().AnyAsync(item => item.ServiceId == id))
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

    // ServiceCategoryDictionaryItem
    [HttpPost("service-categories")]
    public async Task<IActionResult> CreateServiceCategory([FromBody] ServiceCategoryDictionaryItem m)
    {
        _db.ServiceCategories.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/service-categories/{m.Id}", m);
    }
    [HttpPut("service-categories/{id:int}")]
    public async Task<IActionResult> UpdateServiceCategory(int id, [FromBody] ServiceCategoryDictionaryItem m)
    {
        var e = await _db.ServiceCategories.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("service-categories/{id:int}")]
    public async Task<IActionResult> DeleteServiceCategory(int id)
    {
        var e = await _db.ServiceCategories.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.Services.AsNoTracking().AnyAsync(s => s.CategoryId == id))
            return InUseError(e.Name);
        _db.ServiceCategories.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("service-categories/{id:int}/toggle")]
    public async Task<IActionResult> ToggleServiceCategory(int id)
    {
        var e = await _db.ServiceCategories.FindAsync(id);
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


    // TaxSystemDictionaryItem
    [HttpPost("tax-systems")]
    public async Task<IActionResult> CreateTaxSystem([FromBody] TaxSystemDictionaryItem m)
    {
        _db.TaxSystems.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/tax-systems/{m.Id}", m);
    }
    [HttpPut("tax-systems/{id:int}")]
    public async Task<IActionResult> UpdateTaxSystem(int id, [FromBody] TaxSystemDictionaryItem m)
    {
        var e = await _db.TaxSystems.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("tax-systems/{id:int}")]
    public async Task<IActionResult> DeleteTaxSystem(int id)
    {
        var e = await _db.TaxSystems.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.TaxSystem == e.Name))
            return InUseError(e.Name);
        _db.TaxSystems.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("tax-systems/{id:int}/toggle")]
    public async Task<IActionResult> ToggleTaxSystem(int id)
    {
        var e = await _db.TaxSystems.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    // TaxSystemModeDictionaryItem
    [HttpPost("tax-system-modes")]
    public async Task<IActionResult> CreateTaxSystemMode([FromBody] TaxSystemModeDictionaryItem m)
    {
        _db.TaxSystemModes.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/tax-system-modes/{m.Id}", m);
    }
    [HttpPut("tax-system-modes/{id:int}")]
    public async Task<IActionResult> UpdateTaxSystemMode(int id, [FromBody] TaxSystemModeDictionaryItem m)
    {
        var e = await _db.TaxSystemModes.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("tax-system-modes/{id:int}")]
    public async Task<IActionResult> DeleteTaxSystemMode(int id)
    {
        var e = await _db.TaxSystemModes.FindAsync(id);
        if (e is null) return NotFound();
        if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.TaxSystemMode == e.Name))
            return InUseError(e.Name);
        _db.TaxSystemModes.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("tax-system-modes/{id:int}/toggle")]
    public async Task<IActionResult> ToggleTaxSystemMode(int id)
    {
        var e = await _db.TaxSystemModes.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    [HttpPost("invoice-units")]
    public async Task<IActionResult> CreateInvoiceUnit([FromBody] InvoiceUnitDictionaryItem m)
    {
        _db.InvoiceUnits.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/invoice-units/{m.Id}", m);
    }
    [HttpPut("invoice-units/{id:int}")]
    public async Task<IActionResult> UpdateInvoiceUnit(int id, [FromBody] InvoiceUnitDictionaryItem m)
    {
        var e = await _db.InvoiceUnits.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name; e.Description = m.Description; e.ColorHex = m.ColorHex; e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("invoice-units/{id:int}")]
    public async Task<IActionResult> DeleteInvoiceUnit(int id)
    {
        var e = await _db.InvoiceUnits.FindAsync(id);
        if (e is null) return NotFound();
        _db.InvoiceUnits.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("invoice-units/{id:int}/toggle")]
    public async Task<IActionResult> ToggleInvoiceUnit(int id)
    {
        var e = await _db.InvoiceUnits.FindAsync(id);
        if (e is null) return NotFound();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    [HttpPost("invoice-item-names")]
    public async Task<IActionResult> CreateInvoiceItemName([FromBody] InvoiceItemNameDictionaryItem m)
    {
        m.Unit = string.IsNullOrWhiteSpace(m.Unit) ? "шт" : m.Unit.Trim();
        if (m.DefaultPrice < 0) m.DefaultPrice = 0;
        _db.InvoiceItemNames.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/invoice-item-names/{m.Id}", m);
    }
    [HttpPut("invoice-item-names/{id:int}")]
    public async Task<IActionResult> UpdateInvoiceItemName(int id, [FromBody] InvoiceItemNameDictionaryItem m)
    {
        var e = await _db.InvoiceItemNames.FindAsync(id);
        if (e is null) return NotFound();
        e.Name = m.Name;
        e.Unit = string.IsNullOrWhiteSpace(m.Unit) ? "шт" : m.Unit.Trim();
        e.DefaultPrice = m.DefaultPrice < 0 ? 0 : m.DefaultPrice;
        e.Description = m.Description;
        e.ColorHex = m.ColorHex;
        e.IsActive = m.IsActive;
        await _db.SaveChangesAsync();
        return Ok(e);
    }
    [HttpDelete("invoice-item-names/{id:int}")]
    public async Task<IActionResult> DeleteInvoiceItemName(int id)
    {
        var e = await _db.InvoiceItemNames.FindAsync(id);
        if (e is null) return NotFound();
        _db.InvoiceItemNames.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
    [HttpPost("invoice-item-names/{id:int}/toggle")]
    public async Task<IActionResult> ToggleInvoiceItemName(int id)
    {
        var e = await _db.InvoiceItemNames.FindAsync(id);
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

    // DocumentStatusEntity
    [HttpPost("document-statuses")]
    public async Task<IActionResult> CreateDocumentStatus([FromBody] DocumentStatusEntity m)
    {
        var seed = ResolveDocumentStatusSeed(m.Code);
        if (seed is null) return BadRequest("Неизвестный код статуса документа.");
        if (await _db.DocumentStatuses.AsNoTracking().AnyAsync(x => x.Code == seed.Value.Code))
        {
            return BadRequest("Статус документа с таким кодом уже существует.");
        }

        m.Code = seed.Value.Code;
        m.Name = string.IsNullOrWhiteSpace(m.Name) ? seed.Value.Name : m.Name.Trim();
        m.Description = m.Description?.Trim() ?? string.Empty;
        m.ColorHex = string.IsNullOrWhiteSpace(m.ColorHex) ? seed.Value.ColorHex : m.ColorHex.Trim();
        m.SortOrder = m.SortOrder == 0 ? seed.Value.SortOrder : m.SortOrder;
        ApplyMandatoryDocumentStatusRules(m, seed.Value);
        _db.DocumentStatuses.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/document-statuses/{m.Id}", m);
    }

    [HttpPut("document-statuses/{id:int}")]
    public async Task<IActionResult> UpdateDocumentStatus(int id, [FromBody] DocumentStatusEntity m)
    {
        var e = await _db.DocumentStatuses.FindAsync(id);
        if (e is null) return NotFound();

        var seed = ResolveDocumentStatusSeed(string.IsNullOrWhiteSpace(m.Code) ? e.Code : m.Code);
        if (seed is null) return BadRequest("Неизвестный код статуса документа.");

        if (!string.Equals(e.Code, seed.Value.Code, StringComparison.OrdinalIgnoreCase)
            && await _db.DocumentStatuses.AsNoTracking().AnyAsync(x => x.Code == seed.Value.Code))
        {
            return BadRequest("Статус документа с таким кодом уже существует.");
        }

        e.Code = seed.Value.Code;
        e.Name = string.IsNullOrWhiteSpace(m.Name) ? seed.Value.Name : m.Name.Trim();
        e.Description = m.Description?.Trim() ?? string.Empty;
        e.ColorHex = string.IsNullOrWhiteSpace(m.ColorHex) ? seed.Value.ColorHex : m.ColorHex.Trim();
        e.IsActive = m.IsActive;
        e.UseForInvoices = m.UseForInvoices;
        e.UseForActs = m.UseForActs;
        e.UseForContracts = m.UseForContracts;
        e.SortOrder = m.SortOrder == 0 ? seed.Value.SortOrder : m.SortOrder;
        ApplyMandatoryDocumentStatusRules(e, seed.Value);
        await _db.SaveChangesAsync();
        return Ok(e);
    }

    [HttpDelete("document-statuses/{id:int}")]
    public async Task<IActionResult> DeleteDocumentStatus(int id)
    {
        var e = await _db.DocumentStatuses.FindAsync(id);
        if (e is null) return NotFound();
        if (ResolveDocumentStatusSeed(e.Code) is not null)
        {
            return BadRequest("Системный статус документа нельзя удалить. Отключите его или снимите использование для нужных документов.");
        }

        _db.DocumentStatuses.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("document-statuses/{id:int}/toggle")]
    public async Task<IActionResult> ToggleDocumentStatus(int id)
    {
        var e = await _db.DocumentStatuses.FindAsync(id);
        if (e is null) return NotFound();
        var seed = ResolveDocumentStatusSeed(e.Code);
        if (seed is not null && DocumentWorkflowStatus.IsMandatory(seed.Value.Code))
        {
            ApplyMandatoryDocumentStatusRules(e, seed.Value);
            await _db.SaveChangesAsync();
            return Ok(new { e.Id, e.IsActive });
        }

        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        return Ok(new { e.Id, e.IsActive });
    }

    [HttpGet("simple/{kind}/duplicates")]
    public async Task<ActionResult<IReadOnlyList<SimpleDictionaryDuplicateGroupDto>>> GetSimpleDictionaryDuplicates(string kind)
    {
        var meta = ResolveSimpleDictionaryMeta(kind);
        if (meta is null) return NotFound("Неподдерживаемый справочник");

        var items = await meta.LoadAsNoTrackingAsync(CancellationToken.None);
        var result = items
            .Select(item => new SimpleDictionaryDuplicateItemDto
            {
                Id = meta.GetId(item),
                Name = meta.GetName(item),
                Description = meta.GetDescription(item),
                ColorHex = meta.GetColorHex(item),
                IsActive = meta.GetIsActive(item),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => CanonicalizeDictionaryName(item.Name))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .OrderBy(group => group.First().Name)
            .Select(group => new SimpleDictionaryDuplicateGroupDto
            {
                Canonical = group.Key,
                Items = group.OrderBy(item => item.Name).ThenBy(item => item.Id).ToList()
            })
            .ToList();

        return Ok(result);
    }

    [HttpPost("simple/{kind}/merge")]
    public async Task<ActionResult<SimpleDictionaryMergeResultDto>> MergeSimpleDictionaryDuplicates(string kind,
        [FromBody] MergeSimpleDictionaryDuplicatesRequest request,
        CancellationToken ct)
    {
        var meta = ResolveSimpleDictionaryMeta(kind);
        if (meta is null) return NotFound("Неподдерживаемый справочник");

        var sourceIds = (request.SourceIds ?? new List<int>()).Distinct().ToList();
        if (sourceIds.Count < 2)
            return BadRequest("Нужно выбрать минимум 2 значения для объединения");

        var allItems = await meta.LoadTrackingAsync(ct);
        var selected = allItems.Where(item => sourceIds.Contains(meta.GetId(item))).ToList();
        if (selected.Count != sourceIds.Count)
            return BadRequest("Часть выбранных значений не найдена");

        var primary = request.PreferredId.HasValue
            ? selected.FirstOrDefault(item => meta.GetId(item) == request.PreferredId.Value)
            : selected[0];
        if (primary is null)
            return BadRequest("PreferredId должен входить в список SourceIds");

        var targetName = string.IsNullOrWhiteSpace(request.Name) ? meta.GetName(primary) : request.Name.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
            return BadRequest("Итоговое имя не может быть пустым");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var targetDescription = string.IsNullOrWhiteSpace(request.Description)
                ? meta.GetDescription(primary)
                : request.Description.Trim();
            var targetColor = string.IsNullOrWhiteSpace(request.ColorHex)
                ? meta.GetColorHex(primary)
                : request.ColorHex.Trim();
            var targetIsActive = request.IsActive ?? meta.GetIsActive(primary);

            var created = CreateSimpleDictionaryEntity(kind, targetName, targetDescription, targetColor, targetIsActive);
            meta.AddEntity(created);

            var oldNames = selected
                .Select(meta.GetName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var legalEntitiesToUpdate = (await _db.LegalEntities.ToListAsync(ct))
                .Where(entity => oldNames.Contains((meta.ReadLegalEntityField(entity) ?? string.Empty).Trim(), StringComparer.Ordinal))
                .ToList();

            foreach (var entity in legalEntitiesToUpdate)
            {
                meta.AssignLegalEntityField(entity, targetName);
                entity.UpdatedAt = DateTime.UtcNow;
            }

            foreach (var item in selected)
            {
                meta.RemoveEntity(item);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(new SimpleDictionaryMergeResultDto
            {
                Kind = kind,
                CreatedId = GetSimpleDictionaryEntityId(created),
                Name = targetName,
                ReplacedInLegalEntities = legalEntitiesToUpdate.Count,
                RemovedIds = sourceIds,
            });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static object CreateSimpleDictionaryEntity(string kind, string name, string description, string colorHex, bool isActive)
        => kind switch
        {
            "countries" => new CountryDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "base-currencies" => new BaseCurrencyDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "service-languages" => new ServiceLanguageDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "organization-forms" => new OrganizationFormDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "timezones" => new TimezoneDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "tax-systems" => new TaxSystemDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "tax-system-modes" => new TaxSystemModeDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "invoice-units" => new InvoiceUnitDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            "invoice-item-names" => new InvoiceItemNameDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow },
            _ => throw new InvalidOperationException($"Unsupported kind: {kind}")
        };

    private static int GetSimpleDictionaryEntityId(object entity)
        => entity switch
        {
            CountryDictionaryItem x => x.Id,
            BaseCurrencyDictionaryItem x => x.Id,
            ServiceLanguageDictionaryItem x => x.Id,
            OrganizationFormDictionaryItem x => x.Id,
            TimezoneDictionaryItem x => x.Id,
            TaxSystemDictionaryItem x => x.Id,
            TaxSystemModeDictionaryItem x => x.Id,
            InvoiceUnitDictionaryItem x => x.Id,
            InvoiceItemNameDictionaryItem x => x.Id,
            _ => 0
        };
}

public sealed class MergeSimpleDictionaryDuplicatesRequest
{
    public List<int> SourceIds { get; set; } = new();
    public int? PreferredId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ColorHex { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class SimpleDictionaryDuplicateItemDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class SimpleDictionaryDuplicateGroupDto
{
    public string Canonical { get; set; } = string.Empty;
    public List<SimpleDictionaryDuplicateItemDto> Items { get; set; } = new();
}

public sealed class SimpleDictionaryMergeResultDto
{
    public string Kind { get; set; } = string.Empty;
    public int CreatedId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ReplacedInLegalEntities { get; set; }
    public List<int> RemovedIds { get; set; } = new();
}

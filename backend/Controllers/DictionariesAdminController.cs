using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    // ClientStatus
    [HttpPost("client-statuses")]
    public async Task<IActionResult> CreateClientStatus([FromBody] ClientStatus m)
    {
        _db.ClientStatuses.Add(m);
        await _db.SaveChangesAsync();
        return Created($"/api/dictionaries/client-statuses/{m.Id}", m);
    }

    [HttpPut("client-statuses/{id:int}")]
    public async Task<IActionResult> UpdateClientStatus(int id, [FromBody] ClientStatus m)
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
}

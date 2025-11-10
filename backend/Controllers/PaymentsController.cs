using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly PaymentContext _db;
    public PaymentsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        CancellationToken ct)
    {
        var q = _db.Payments.AsQueryable().WithPaymentIncludes();
        if (from.HasValue) q = q.Where(p => p.Date >= from.Value);
        if (to.HasValue) q = q.Where(p => p.Date <= to.Value);
        if (clientId.HasValue) q = q.Where(p => p.ClientId == clientId.Value);
        if (caseId.HasValue) q = q.Where(p => p.ClientCaseId == caseId.Value);
        var list = await q.AsNoTracking().OrderBy(p => p.Date).ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.Payments.WithPaymentIncludes().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Payment model)
    {
        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }
        model.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        PaymentBusinessLogic.PrepareForCreate(model, DateTime.UtcNow);
        _db.Payments.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        PaymentBusinessLogic.ApplyUpdate(e, model, DateTime.UtcNow);
        await _db.SaveChangesAsync();
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        _db.Payments.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

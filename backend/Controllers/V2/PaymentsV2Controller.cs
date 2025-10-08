using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/payments")]
[Authorize]
public class PaymentsV2Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public PaymentsV2Controller(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var q = _db.Payments.AsQueryable()
            .WithPaymentIncludes()
            .ApplyPaymentFilters(from, to, clientId, caseId, search)
            .ApplyPaymentSort(sortBy, sortDir);

        var total = await q.CountAsync(ct);
        var items = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.Payments
            .WithPaymentIncludes()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Payment model)
    {
        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type)
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        model.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        _db.Payments.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/v2/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type)
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        e.Date = model.Date;
        e.Amount = model.Amount;
        e.Type = model.Type;
        e.Status = model.Status;
        e.Description = model.Description;
        e.IsPaid = model.IsPaid;
        e.PaidDate = model.PaidDate;
        e.Notes = model.Notes;
        e.ClientId = model.ClientId;
        e.ClientCaseId = model.ClientCaseId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        e.AccountDate = model.AccountDate;

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

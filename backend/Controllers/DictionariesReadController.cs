using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/dictionaries")]
[Authorize]
public class DictionariesReadController : ControllerBase
{
    private readonly PaymentContext _db;
    public DictionariesReadController(PaymentContext db) => _db = db;

    [HttpGet("deal-types")]
    public async Task<IActionResult> DealTypes(CancellationToken ct)
        => Ok(await _db.DealTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));

    [HttpGet("income-types")]
    public async Task<IActionResult> IncomeTypes([FromQuery] PaymentType? paymentType, [FromQuery] bool? isActive, CancellationToken ct)
    {
        var q = _db.IncomeTypes.AsNoTracking().AsQueryable();
        if (isActive.HasValue) q = q.Where(i => i.IsActive == isActive.Value);
        if (paymentType.HasValue) q = q.Where(i => i.PaymentType == paymentType.Value);
        return Ok(await q.OrderBy(i => i.Name).ToListAsync(ct));
    }

    [HttpGet("payment-sources")]
    public async Task<IActionResult> PaymentSources(CancellationToken ct)
        => Ok(await _db.PaymentSources.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));

    [HttpGet("payment-statuses")]
    public async Task<IActionResult> PaymentStatuses(CancellationToken ct)
        => Ok(await _db.PaymentStatuses.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
}

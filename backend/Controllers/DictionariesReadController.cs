using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Helpers;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/dictionaries")]
[Authorize]
public class DictionariesReadController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly ILogger<DictionariesReadController> _logger;

    public DictionariesReadController(PaymentContext db, ILogger<DictionariesReadController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet("deal-types")]
    public async Task<IActionResult> DealTypes(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.DealTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("income-types")]
    public async Task<IActionResult> IncomeTypes([FromQuery] PaymentType? paymentType, [FromQuery] bool? isActive, CancellationToken ct)
    {
        var q = _db.IncomeTypes.AsNoTracking().AsQueryable();
        if (isActive.HasValue) q = q.Where(i => i.IsActive == isActive.Value);
        if (paymentType.HasValue) q = q.Where(i => i.PaymentType == paymentType.Value);
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => q.OrderBy(i => i.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("payment-sources")]
    public async Task<IActionResult> PaymentSources(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.PaymentSources.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("client-statuses")]
    public async Task<IActionResult> ClientStatuses(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.ClientStatuses.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("payment-statuses")]
    public async Task<IActionResult> PaymentStatuses(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.PaymentStatuses.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }
}

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


    [HttpGet("countries")]
    public async Task<IActionResult> Countries(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.Countries.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("base-currencies")]
    public async Task<IActionResult> BaseCurrencies(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.BaseCurrencies.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("service-languages")]
    public async Task<IActionResult> ServiceLanguages(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.ServiceLanguages.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("organization-forms")]
    public async Task<IActionResult> OrganizationForms(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.OrganizationForms.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("timezones")]
    public async Task<IActionResult> Timezones(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.Timezones.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }


    [HttpGet("tax-systems")]
    public async Task<IActionResult> TaxSystems(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.TaxSystems.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("tax-system-modes")]
    public async Task<IActionResult> TaxSystemModes(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.TaxSystemModes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("services")]
    public async Task<IActionResult> Services(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.Services.AsNoTracking()
                .Include(x => x.Category)
                .OrderBy(x => x.Name)
                .ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("service-categories")]
    public async Task<IActionResult> ServiceCategories(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.ServiceCategories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("invoice-units")]
    public async Task<IActionResult> InvoiceUnits(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.InvoiceUnits.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
            _logger,
            ct);
        return Ok(result);
    }

    [HttpGet("invoice-item-names")]
    public async Task<IActionResult> InvoiceItemNames(CancellationToken ct)
    {
        var result = await SqliteRetryHelper.ExecuteAsync(
            _ => _db.InvoiceItemNames.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
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

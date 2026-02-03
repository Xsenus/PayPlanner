using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-operations")]
[Authorize]
public class MoneyOperationsController : ControllerBase
{
    private readonly PaymentContext _db;
    public MoneyOperationsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] MoneyOperationSource? source,
        [FromQuery] MoneyOperationDirection? direction,
        [FromQuery] MoneyOperationAccountingType? accountingType,
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var q = _db.MoneyOperations.AsQueryable();

        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            q = q.Where(o => o.Date >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            q = q.Where(o => o.Date <= toDate);
        }

        if (source.HasValue)
        {
            q = q.Where(o => o.Source == source.Value);
        }

        if (direction.HasValue)
        {
            q = q.Where(o => o.Direction == direction.Value);
        }

        if (accountingType.HasValue)
        {
            q = q.Where(o => o.AccountingType == accountingType.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim().ToLower();
            q = q.Where(o =>
                (o.CounterpartyName ?? string.Empty).ToLower().Contains(query) ||
                (o.Purpose ?? string.Empty).ToLower().Contains(query) ||
                (o.CategoryName ?? string.Empty).ToLower().Contains(query));
        }

        var list = await q.AsNoTracking()
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.Id)
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.MoneyOperations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MoneyOperation model)
    {
        if (model.Amount < 0)
        {
            return BadRequest("Amount must be non-negative.");
        }

        model.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        model.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        model.CategoryName = string.IsNullOrWhiteSpace(model.CategoryName) ? null : model.CategoryName.Trim();
        model.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        model.PaymentOrderNumber = string.IsNullOrWhiteSpace(model.PaymentOrderNumber) ? null : model.PaymentOrderNumber.Trim();
        model.Purpose = string.IsNullOrWhiteSpace(model.Purpose) ? null : model.Purpose.Trim();
        model.PaidInvoiceNumber = string.IsNullOrWhiteSpace(model.PaidInvoiceNumber) ? null : model.PaidInvoiceNumber.Trim();
        model.ProjectName = string.IsNullOrWhiteSpace(model.ProjectName) ? null : model.ProjectName.Trim();
        model.DirectionName = string.IsNullOrWhiteSpace(model.DirectionName) ? null : model.DirectionName.Trim();
        model.SalesPointName = string.IsNullOrWhiteSpace(model.SalesPointName) ? null : model.SalesPointName.Trim();
        model.PaidAccountName = string.IsNullOrWhiteSpace(model.PaidAccountName) ? null : model.PaidAccountName.Trim();
        model.CreatedAt = DateTime.UtcNow;

        _db.MoneyOperations.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/money-operations/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MoneyOperation model)
    {
        var entity = await _db.MoneyOperations.FindAsync(id);
        if (entity is null) return NotFound();

        if (model.Amount < 0)
        {
            return BadRequest("Amount must be non-negative.");
        }

        entity.Source = model.Source;
        entity.Direction = model.Direction;
        entity.AccountingType = model.AccountingType;
        entity.Amount = model.Amount;
        entity.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim();
        entity.AccountName = string.IsNullOrWhiteSpace(model.AccountName) ? null : model.AccountName.Trim();
        entity.CounterpartyName = string.IsNullOrWhiteSpace(model.CounterpartyName) ? null : model.CounterpartyName.Trim();
        entity.CategoryName = string.IsNullOrWhiteSpace(model.CategoryName) ? null : model.CategoryName.Trim();
        entity.PaymentOrderNumber = string.IsNullOrWhiteSpace(model.PaymentOrderNumber) ? null : model.PaymentOrderNumber.Trim();
        entity.Date = model.Date;
        entity.RecognitionDate = model.RecognitionDate;
        entity.IsAdvancePayment = model.IsAdvancePayment;
        entity.Purpose = string.IsNullOrWhiteSpace(model.Purpose) ? null : model.Purpose.Trim();
        entity.PaidInvoiceNumber = string.IsNullOrWhiteSpace(model.PaidInvoiceNumber) ? null : model.PaidInvoiceNumber.Trim();
        entity.PaidAmount = model.PaidAmount;
        entity.ProjectName = string.IsNullOrWhiteSpace(model.ProjectName) ? null : model.ProjectName.Trim();
        entity.DirectionName = string.IsNullOrWhiteSpace(model.DirectionName) ? null : model.DirectionName.Trim();
        entity.SalesPointName = string.IsNullOrWhiteSpace(model.SalesPointName) ? null : model.SalesPointName.Trim();
        entity.PaymentPriority = model.PaymentPriority;
        entity.PaidAccountName = string.IsNullOrWhiteSpace(model.PaidAccountName) ? null : model.PaidAccountName.Trim();
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.MoneyOperations.FindAsync(id);
        if (entity is null) return NotFound();
        _db.MoneyOperations.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

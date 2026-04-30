using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/money-accounts")]
[Authorize]
public class MoneyAccountsController : ControllerBase
{
    private readonly PaymentContext _db;

    public MoneyAccountsController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MoneyAccount>>> Get(
        [FromQuery] MoneyOperationSource? source = null,
        [FromQuery] bool activeOnly = true,
        CancellationToken ct = default)
    {
        var query = _db.MoneyAccounts.AsNoTracking();

        if (source.HasValue)
        {
            query = query.Where(account => account.Source == source.Value);
        }

        if (activeOnly)
        {
            query = query.Where(account => account.IsActive);
        }

        var items = await query
            .OrderBy(account => account.Source)
            .ThenByDescending(account => account.IsDefault)
            .ThenBy(account => account.Name)
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<MoneyAccount>> GetById(int id, CancellationToken ct)
    {
        var account = await _db.MoneyAccounts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        return account is null ? NotFound() : Ok(account);
    }

    [HttpPost]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MoneyAccount>> Create([FromBody] MoneyAccount model, CancellationToken ct)
    {
        var normalized = Normalize(model);
        if (string.IsNullOrWhiteSpace(normalized.Name))
        {
            return BadRequest("Название денежного счёта обязательно.");
        }

        var duplicateExists = await _db.MoneyAccounts.AnyAsync(
            account => account.Source == normalized.Source && account.Name.ToLower() == normalized.Name.ToLower(),
            ct);
        if (duplicateExists)
        {
            return Conflict("Денежный счёт с таким названием уже существует для выбранного источника.");
        }

        var hasAccountsForSource = await _db.MoneyAccounts.AnyAsync(account => account.Source == normalized.Source, ct);
        normalized.IsDefault = normalized.IsDefault || !hasAccountsForSource;
        normalized.CreatedAt = DateTime.UtcNow;

        _db.MoneyAccounts.Add(normalized);
        await EnsureSingleDefaultAsync(normalized, ct);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = normalized.Id }, normalized);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<MoneyAccount>> Update(int id, [FromBody] MoneyAccount model, CancellationToken ct)
    {
        var entity = await _db.MoneyAccounts.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var normalized = Normalize(model);
        if (string.IsNullOrWhiteSpace(normalized.Name))
        {
            return BadRequest("Название денежного счёта обязательно.");
        }

        var duplicateExists = await _db.MoneyAccounts.AnyAsync(
            account => account.Id != id &&
                       account.Source == normalized.Source &&
                       account.Name.ToLower() == normalized.Name.ToLower(),
            ct);
        if (duplicateExists)
        {
            return Conflict("Денежный счёт с таким названием уже существует для выбранного источника.");
        }

        entity.Name = normalized.Name;
        entity.Source = normalized.Source;
        entity.Currency = normalized.Currency;
        entity.BankName = normalized.BankName;
        entity.BankAccountNumber = normalized.BankAccountNumber;
        entity.IsDefault = normalized.IsDefault;
        entity.IsActive = normalized.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await EnsureSingleDefaultAsync(entity, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.MoneyAccounts.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var isUsed = await _db.MoneyOperations.AnyAsync(operation => operation.MoneyAccountId == id, ct);
        if (isUsed)
        {
            entity.IsActive = false;
            entity.IsDefault = false;
            entity.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.MoneyAccounts.Remove(entity);
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task EnsureSingleDefaultAsync(MoneyAccount account, CancellationToken ct)
    {
        if (!account.IsDefault)
        {
            return;
        }

        var sameSourceDefaults = await _db.MoneyAccounts
            .Where(item => item.Id != account.Id && item.Source == account.Source && item.IsDefault)
            .ToListAsync(ct);
        foreach (var item in sameSourceDefaults)
        {
            item.IsDefault = false;
            item.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static MoneyAccount Normalize(MoneyAccount model)
        => new()
        {
            Id = model.Id,
            Name = (model.Name ?? string.Empty).Trim(),
            Source = model.Source,
            Currency = string.IsNullOrWhiteSpace(model.Currency) ? "RUB" : model.Currency.Trim().ToUpperInvariant(),
            BankName = string.IsNullOrWhiteSpace(model.BankName) ? null : model.BankName.Trim(),
            BankAccountNumber = string.IsNullOrWhiteSpace(model.BankAccountNumber) ? null : model.BankAccountNumber.Trim(),
            IsDefault = model.IsDefault,
            IsActive = model.IsActive,
            CreatedAt = model.CreatedAt,
            UpdatedAt = model.UpdatedAt,
        };
}

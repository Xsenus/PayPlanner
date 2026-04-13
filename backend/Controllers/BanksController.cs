using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/banks")]
[Authorize]
public class BanksController : ControllerBase
{
    private readonly PaymentContext _db;

    public BanksController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<BankDto>>> GetBanks(
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var query = _db.Banks.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(bank =>
                bank.Name.ToLower().Contains(term) ||
                (bank.Bik != null && bank.Bik.ToLower().Contains(term)) ||
                (bank.City != null && bank.City.ToLower().Contains(term)));
        }

        var items = await query
            .OrderBy(bank => bank.Name)
            .ThenBy(bank => bank.Bik)
            .Take(200)
            .Select(bank => Map(bank))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BankDto>> GetBank(int id, CancellationToken ct)
    {
        var bank = await _db.Banks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (bank is null)
        {
            return NotFound();
        }

        return Ok(Map(bank));
    }

    [HttpPost]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<BankDto>> CreateBank([FromBody] BankUpsertRequest request, CancellationToken ct)
    {
        var normalized = Normalize(request);

        var duplicateExists = await _db.Banks.AnyAsync(
            bank => bank.Name.ToLower() == normalized.Name.ToLower()
                && (bank.Bik ?? string.Empty) == (normalized.Bik ?? string.Empty),
            ct);

        if (duplicateExists)
        {
            return Conflict("Банк с таким названием и БИК уже существует.");
        }

        var entity = new Bank
        {
            Name = normalized.Name,
            Bik = normalized.Bik,
            CorrespondentAccount = normalized.CorrespondentAccount,
            City = normalized.City,
            IsForeign = normalized.IsForeign,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Banks.Add(entity);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetBank), new { id = entity.Id }, Map(entity));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<BankDto>> UpdateBank(int id, [FromBody] BankUpsertRequest request, CancellationToken ct)
    {
        var entity = await _db.Banks.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var normalized = Normalize(request);

        var duplicateExists = await _db.Banks.AnyAsync(
            bank => bank.Id != id
                && bank.Name.ToLower() == normalized.Name.ToLower()
                && (bank.Bik ?? string.Empty) == (normalized.Bik ?? string.Empty),
            ct);

        if (duplicateExists)
        {
            return Conflict("Банк с таким названием и БИК уже существует.");
        }

        entity.Name = normalized.Name;
        entity.Bik = normalized.Bik;
        entity.CorrespondentAccount = normalized.CorrespondentAccount;
        entity.City = normalized.City;
        entity.IsForeign = normalized.IsForeign;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(Map(entity));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> DeleteBank(int id, CancellationToken ct)
    {
        var entity = await _db.Banks.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var marker = $"\"bankId\":{id}";
        var isUsed = await _db.LegalEntities.AsNoTracking().AnyAsync(
            legalEntity =>
                (legalEntity.BankAccountsJson != null && EF.Functions.Like(legalEntity.BankAccountsJson, $"%{marker}%")) ||
                (legalEntity.CardsJson != null && EF.Functions.Like(legalEntity.CardsJson, $"%{marker}%")),
            ct);

        if (isUsed)
        {
            return Conflict("Банк используется в реквизитах юридических лиц и не может быть удален.");
        }

        _db.Banks.Remove(entity);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private static BankDto Map(Bank entity)
        => new()
        {
            Id = entity.Id,
            Name = entity.Name,
            Bik = entity.Bik,
            CorrespondentAccount = entity.CorrespondentAccount,
            City = entity.City,
            IsForeign = entity.IsForeign,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
        };

    private static BankUpsertRequest Normalize(BankUpsertRequest request)
        => new()
        {
            Name = request.Name.Trim(),
            Bik = NormalizeOptional(request.Bik),
            CorrespondentAccount = NormalizeOptional(request.CorrespondentAccount),
            City = NormalizeOptional(request.City),
            IsForeign = request.IsForeign,
        };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

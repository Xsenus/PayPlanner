using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CasesController : ControllerBase
{
    private readonly PaymentContext _db;
    public CasesController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? clientId, [FromQuery] int? legalEntityId, CancellationToken ct)
    {
        var q = _db.ClientCases.Include(c => c.Client).Include(c => c.LegalEntity).AsQueryable();
        if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);
        if (legalEntityId.HasValue) q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
        var items = await q.AsNoTracking()
            .OrderBy(c => c.ClientId ?? int.MaxValue)
            .ThenBy(c => c.LegalEntityId ?? int.MaxValue)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .Include(c => c.Payments)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, ct);
        if (validation is not null) return validation;
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        var e = await _db.ClientCases.FindAsync([id], ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, ct);
        if (validation is not null) return validation;

        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;

        await _db.SaveChangesAsync(ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.ClientCases.FindAsync(id);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync();
        foreach (var p in payments) p.ClientCaseId = null;

        await _db.SaveChangesAsync();

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(int? clientId, int? legalEntityId, CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients.AsNoTracking().AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities.AsNoTracking().AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/clients/{clientId:int}/powers-of-attorney")]
[Authorize]
public class PowerOfAttorneyController : ControllerBase
{
    private readonly PaymentContext _db;

    public PowerOfAttorneyController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(int clientId, CancellationToken ct)
        => Ok(await _db.PowersOfAttorney
            .AsNoTracking()
            .Where(x => x.ClientId == clientId)
            .OrderByDescending(x => x.IssuedAt)
            .ToListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(int clientId, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
    {
        var clientExists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
        if (!clientExists) return NotFound();

        var entity = new PowerOfAttorney
        {
            ClientId = clientId,
            Number = (model.Number ?? string.Empty).Trim(),
            IssuedAt = model.IssuedAt.GetValueOrDefault(),
            ValidUntil = model.ValidUntil,
            Issuer = Normalize(model.Issuer),
            Notes = Normalize(model.Notes),
            IsActive = model.IsActive,
        };

        _db.PowersOfAttorney.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/clients/{clientId}/powers-of-attorney/{entity.Id}", entity);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int clientId, int id, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
    {
        var entity = await _db.PowersOfAttorney.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Id == id, ct);
        if (entity is null) return NotFound();

        entity.Number = (model.Number ?? string.Empty).Trim();
        entity.IssuedAt = model.IssuedAt.GetValueOrDefault();
        entity.ValidUntil = model.ValidUntil;
        entity.Issuer = Normalize(model.Issuer);
        entity.Notes = Normalize(model.Notes);
        entity.IsActive = model.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int clientId, int id, CancellationToken ct)
    {
        var entity = await _db.PowersOfAttorney.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Id == id, ct);
        if (entity is null) return NotFound();

        _db.PowersOfAttorney.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

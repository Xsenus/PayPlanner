using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/clients/{clientId:int}/passport")]
[Authorize]
public class ClientPassportController : ControllerBase
{
    private readonly PaymentContext _db;

    public ClientPassportController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(int clientId, CancellationToken ct)
    {
        var exists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
        if (!exists) return NotFound();

        var passport = await _db.ClientPassports.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
        return passport is null ? NoContent() : Ok(passport);
    }

    [HttpPut]
    public async Task<IActionResult> Upsert(int clientId, [FromBody] ClientPassport model, CancellationToken ct)
    {
        var exists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
        if (!exists) return NotFound();

        var entity = await _db.ClientPassports.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (entity is null)
        {
            entity = new ClientPassport { ClientId = clientId };
            _db.ClientPassports.Add(entity);
        }

        entity.Series = Normalize(model.Series);
        entity.Number = Normalize(model.Number);
        entity.IssuedBy = Normalize(model.IssuedBy);
        entity.IssuedAt = model.IssuedAt;
        entity.DepartmentCode = Normalize(model.DepartmentCode);
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

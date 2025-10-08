using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/cases")]
[Authorize]
public class CasesV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public CasesV1Controller(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        CancellationToken ct)
    {
        var q = _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyCaseFilters(clientId, status, search)
            .ApplyCaseSort(sortBy, sortDir);

        return Ok(await q.AsNoTracking().ToListAsync(ct));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.ClientCases
            .WithCaseIncludes(includePayments: true)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model)
    {
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/v1/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model)
    {
        var e = await _db.ClientCases.FindAsync(id);
        if (e is null) return NotFound();

        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;

        await _db.SaveChangesAsync();
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
}

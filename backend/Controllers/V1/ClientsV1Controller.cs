using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/clients")]
[Authorize]
public class ClientsV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV1Controller(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var q = _db.Clients
            .Include(c => c.ClientStatus)
            .AsQueryable()
            .ApplyClientFilters(search, isActive)
            .ApplyClientSort(sortBy, sortDir);

        if (limit.HasValue && limit.Value > 0)
        {
            var size = Math.Min(limit.Value, 200);
            q = q.Take(size);
        }

        return Ok(await q.AsNoTracking().ToListAsync(ct));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients
            .Include(c => c.ClientStatus)
            .Include(c => c.Cases)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return client is not null ? Ok(client) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model)
    {
        _db.Clients.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/v1/clients/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model)
    {
        var e = await _db.Clients.FindAsync(id);
        if (e is null) return NotFound();

        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        e.IsActive = model.IsActive;

        await _db.SaveChangesAsync();
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.Clients.FindAsync(id);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync();
        foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
        await _db.SaveChangesAsync();

        _db.Clients.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/clients")]
[Authorize]
public class ClientsV2Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV2Controller(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var q = _db.Clients.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            q = q.Where(c =>
                (c.Name != null && EF.Functions.Like(c.Name, $"%{search}%")) ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{search}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{search}%")) ||
                (c.Company != null && EF.Functions.Like(c.Company, $"%{search}%")) ||
                (c.Address != null && EF.Functions.Like(c.Address, $"%{search}%")));
        }

        if (isActive.HasValue)
            q = q.Where(c => c.IsActive == isActive.Value);

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        q = (sortBy?.ToLowerInvariant()) switch
        {
            "createdat" => (desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt)),
            _ => (desc ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
        };

        var total = await q.CountAsync(ct);
        var items = await q.AsNoTracking().Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients
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
        return Created($"/api/v2/clients/{model.Id}", model);
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

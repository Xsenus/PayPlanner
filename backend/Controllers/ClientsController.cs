using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
        => Ok(await _db.Clients.Include(c => c.Cases).AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients.Include(c => c.Cases).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return client is null ? NotFound() : Ok(client);
    }

    [HttpGet("{id:int}/stats")]
    public async Task<IActionResult> GetStats(int id, [FromQuery] int? caseId, CancellationToken ct)
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is null) return NotFound();

        var q = _db.Payments.Where(p => p.ClientId == id)
            .Include(p => p.DealType).Include(p => p.IncomeType)
            .Include(p => p.PaymentSource).Include(p => p.PaymentStatusEntity).AsQueryable();

        if (caseId.HasValue) q = q.Where(p => p.ClientCaseId == caseId.Value);

        var payments = await q.OrderByDescending(p => p.Date).AsNoTracking().ToListAsync(ct);

        var stats = new ClientStats
        {
            ClientId = client.Id,
            ClientName = client.Name,
            TotalIncome = payments.Where(p => p.Type == PaymentType.Income && p.IsPaid).Sum(p => p.Amount),
            TotalExpenses = payments.Where(p => p.Type == PaymentType.Expense && p.IsPaid).Sum(p => p.Amount),
            TotalPayments = payments.Count,
            PaidPayments = payments.Count(p => p.IsPaid),
            PendingPayments = payments.Count(p => !p.IsPaid),
            LastPaymentDate = payments.FirstOrDefault()?.Date,
            RecentPayments = payments.ToList()
        };
        stats.NetAmount = stats.TotalIncome - stats.TotalExpenses;
        return Ok(stats);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model)
    {
        _db.Clients.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/clients/{model.Id}", model);
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

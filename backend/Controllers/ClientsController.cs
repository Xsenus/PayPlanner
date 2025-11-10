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
        => Ok(await _db.Clients
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
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

        var payments = await q
            .OrderByDescending(p => p.Date)
            .AsNoTracking()
            .ToListAsync(ct);

        var totalIncome = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.PaidAmount);
        var totalExpenses = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.PaidAmount);
        var outstandingIncome = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.OutstandingAmount);
        var outstandingExpenses = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.OutstandingAmount);
        var paidPayments = payments.Count(p => p.Status == PaymentStatus.Completed);
        var pendingPayments = payments.Count(p => p.Status != PaymentStatus.Completed && p.OutstandingAmount > 0m);
        var overduePayments = payments.Count(p => p.Status == PaymentStatus.Overdue && p.OutstandingAmount > 0m);
        var lastPaymentDate = payments
            .Where(p => p.LastPaymentDate.HasValue)
            .OrderByDescending(p => p.LastPaymentDate)
            .Select(p => p.LastPaymentDate)
            .FirstOrDefault()
            ?? payments.OrderByDescending(p => p.Date).Select(p => (DateTime?)p.Date).FirstOrDefault();

        var stats = new ClientStats
        {
            ClientId = client.Id,
            ClientName = client.Name,
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            OutstandingIncome = outstandingIncome,
            OutstandingExpenses = outstandingExpenses,
            TotalPayments = payments.Count,
            PaidPayments = paidPayments,
            PendingPayments = pendingPayments,
            OverduePayments = overduePayments,
            LastPaymentDate = lastPaymentDate,
            RecentPayments = payments.ToList()
        };
        stats.NetAmount = stats.TotalIncome - stats.TotalExpenses;
        return Ok(stats);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model)
    {
        if (model.LegalEntityId.HasValue)
        {
            var exists = await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == model.LegalEntityId.Value);
            if (!exists)
            {
                return BadRequest($"Юридическое лицо #{model.LegalEntityId.Value} не найдено");
            }
        }

        var entity = new Client
        {
            Name = model.Name,
            Email = model.Email,
            Phone = model.Phone,
            Company = model.Company,
            Address = model.Address,
            Notes = model.Notes,
            IsActive = model.IsActive,
            LegalEntityId = model.LegalEntityId,
        };

        _db.Clients.Add(entity);
        await _db.SaveChangesAsync();
        await _db.Entry(entity).Reference(c => c.LegalEntity).LoadAsync();
        return Created($"/api/clients/{entity.Id}", entity);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model)
    {
        var e = await _db.Clients.FindAsync(id);
        if (e is null) return NotFound();
        if (model.LegalEntityId.HasValue)
        {
            var exists = await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Id == model.LegalEntityId.Value);
            if (!exists)
            {
                return BadRequest($"Юридическое лицо #{model.LegalEntityId.Value} не найдено");
            }
        }
        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        e.IsActive = model.IsActive;
        e.LegalEntityId = model.LegalEntityId;
        await _db.SaveChangesAsync();
        await _db.Entry(e).Reference(c => c.LegalEntity).LoadAsync();
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

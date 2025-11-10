using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

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

    [HttpGet("lookup")]
    public async Task<ActionResult<IEnumerable<ClientLookupDto>>> Lookup(
        [FromQuery] string? search,
        [FromQuery] bool includeInactive = true,
        [FromQuery] int limit = 50,
        [FromQuery] List<int>? ids = null,
        CancellationToken ct = default)
    {
        limit = limit <= 0 ? 50 : Math.Min(limit, 200);

        var query = _db.Clients.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Name, pattern, "\\") ||
                (c.Company != null && EF.Functions.Like(c.Company, pattern, "\\")));
        }

        var results = await query
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Take(limit)
            .Select(c => new ClientLookupDto
            {
                Id = c.Id,
                Name = c.Name,
                Company = c.Company,
                IsActive = c.IsActive,
            })
            .ToListAsync(ct);

        if (ids is { Count: > 0 })
        {
            var knownIds = results.Select(c => c.Id).ToHashSet();
            var missing = ids.Where(id => !knownIds.Contains(id)).ToArray();

            if (missing.Length > 0)
            {
                var extra = await _db.Clients
                    .AsNoTracking()
                    .Where(c => missing.Contains(c.Id))
                    .Select(c => new ClientLookupDto
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Company = c.Company,
                        IsActive = c.IsActive,
                    })
                    .ToListAsync(ct);

                foreach (var item in extra)
                {
                    knownIds.Add(item.Id);
                    results.Add(item);
                }
            }
        }

        return Ok(results
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .ToList());
    }

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
    private static string EscapeLike(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}

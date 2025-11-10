using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly PaymentContext _db;
    public StatsController(PaymentContext db) => _db = db;

    [HttpGet("month")]
    public async Task<IActionResult> Month([FromQuery] int year, [FromQuery] int month, CancellationToken ct)
    {
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var payments = await _db.Payments
            .Where(p => p.Date >= startDate && p.Date <= endDate)
            .AsNoTracking()
            .ToListAsync(ct);

        decimal SumPaid(IEnumerable<Payment> source) => source.Sum(p => p.PaidAmount > 0 ? p.PaidAmount : p.Amount);

        var income = SumPaid(payments.Where(p => p.Type == PaymentType.Income && p.IsPaid));
        var expense = SumPaid(payments.Where(p => p.Type == PaymentType.Expense && p.IsPaid));
        var profit = income - expense;

        var completed = payments.Count(p => p.IsPaid);
        var pending = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Pending);
        var overdue = payments.Count(p => !p.IsPaid && p.Status == PaymentStatus.Overdue);
        var total = payments.Count;

        var completionRate = total > 0 ? Math.Round((double)completed / total * 100, 1) : 0;

        return Ok(new MonthlyStats
        {
            Income = income,
            Expense = expense,
            Profit = profit,
            CompletionRate = completionRate,
            Counts = new StatusCounts
            {
                Completed = completed,
                Pending = pending,
                Overdue = overdue,
                Total = total
            }
        });
    }
}

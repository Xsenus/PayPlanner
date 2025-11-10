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

        var income = payments.Where(p => p.Type == PaymentType.Income).Sum(p => p.PaidAmount);
        var expense = payments.Where(p => p.Type == PaymentType.Expense).Sum(p => p.PaidAmount);
        var profit = income - expense;

        var completed = payments.Count(p => p.Status == PaymentStatus.Completed);
        var pending = payments.Count(p => p.Status == PaymentStatus.Pending && p.OutstandingAmount > 0m);
        var overdue = payments.Count(p => p.Status == PaymentStatus.Overdue && p.OutstandingAmount > 0m);
        var total = payments.Count;

        var completionRate = total > 0 ? Math.Round((double)completed / total * 100, 1) : 0;

        var completedAmount = payments.Where(p => p.Status == PaymentStatus.Completed).Sum(p => p.PaidAmount);
        var pendingAmount = payments.Where(p => p.Status == PaymentStatus.Pending).Sum(p => p.OutstandingAmount);
        var overdueAmount = payments.Where(p => p.Status == PaymentStatus.Overdue).Sum(p => p.OutstandingAmount);

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
            },
            CompletedAmount = completedAmount,
            PendingAmount = pendingAmount,
            OverdueAmount = overdueAmount
        });
    }
}

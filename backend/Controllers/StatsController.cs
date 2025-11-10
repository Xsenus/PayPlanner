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
            .Where(p =>
                (p.Date >= startDate && p.Date <= endDate) ||
                (p.LastPaymentDate.HasValue && p.LastPaymentDate.Value >= startDate && p.LastPaymentDate.Value <= endDate) ||
                (p.OriginalDate.HasValue && p.OriginalDate.Value >= startDate && p.OriginalDate.Value <= endDate))
            .AsNoTracking()
            .ToListAsync(ct);

        decimal income = 0m;
        decimal expense = 0m;

        foreach (var payment in payments)
        {
            bool hasTimelinePayments = false;
            foreach (var entry in payment.Timeline)
            {
                if (entry.EventType != PaymentTimelineEventType.PartialPayment) continue;
                var effectiveDate = (entry.EffectiveDate ?? entry.Timestamp).Date;
                if (effectiveDate < startDate || effectiveDate > endDate) continue;

                var delta = entry.AmountDelta ?? 0m;
                if (delta == 0m) continue;
                hasTimelinePayments = true;

                if (payment.Type == PaymentType.Income)
                {
                    income += delta;
                }
                else if (payment.Type == PaymentType.Expense)
                {
                    expense += delta;
                }
            }

            if (!hasTimelinePayments && payment.IsPaid)
            {
                var paidDate = (payment.PaidDate ?? payment.LastPaymentDate ?? payment.Date).Date;
                if (paidDate >= startDate && paidDate <= endDate)
                {
                    if (payment.Type == PaymentType.Income)
                    {
                        income += payment.Amount;
                    }
                    else if (payment.Type == PaymentType.Expense)
                    {
                        expense += payment.Amount;
                    }
                }
            }
        }

        income = Math.Round(income, 2, MidpointRounding.AwayFromZero);
        expense = Math.Round(expense, 2, MidpointRounding.AwayFromZero);
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

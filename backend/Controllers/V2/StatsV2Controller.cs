using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/stats")]
[Authorize]
public class StatsV2Controller : ControllerBase
{
    private readonly StatsSummaryService _svc;
    private readonly PaymentContext _db;

    public StatsV2Controller(StatsSummaryService svc, PaymentContext db)
    {
        _svc = svc;
        _db = db;
    }

    // Совместимость со старым фронтом:
    // GET /api/v2/stats/summary?from=...&to=...&clientId=&caseId=&period=&type=&status=&q=&r=
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? period,
        [FromQuery] PaymentType? type,
        [FromQuery] PaymentStatus? status,
        [FromQuery] string? q,
        CancellationToken ct)
    {
        var res = await _svc.GetAsync(clientId, caseId, from, to, period, type, status, q, ct);
        return Ok(res);
    }

    // Опционально: диапазон по месяцам (если фронт вызывает /v2/stats/months)
    [HttpGet("months")]
    public async Task<IActionResult> Months(
        [FromQuery] int startYear,
        [FromQuery] int startMonth,
        [FromQuery] int endYear,
        [FromQuery] int endMonth,
        CancellationToken ct = default)
    {
        var start = new DateTime(startYear, startMonth, 1);
        var end = new DateTime(endYear, endMonth, 1);
        if (end < start) (start, end) = (end, start);

        var items = new List<object>();
        for (var d = start; d <= end; d = d.AddMonths(1))
        {
            var mStart = new DateTime(d.Year, d.Month, 1);
            var mEnd = mStart.AddMonths(1).AddDays(-1);

            var payments = await _db.Payments
                .Where(p => p.Date >= mStart && p.Date <= mEnd)
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

            items.Add(new
            {
                year = d.Year,
                month = d.Month,
                period = $"{d.Year:D4}-{d.Month:D2}",
                income,
                expense,
                profit,
                completionRate,
                counts = new { completed, pending, overdue, total },
                completedAmount,
                pendingAmount,
                overdueAmount
            });
        }

        return Ok(new
        {
            start = new { year = start.Year, month = start.Month },
            end = new { year = end.Year, month = end.Month },
            items
        });
    }
}

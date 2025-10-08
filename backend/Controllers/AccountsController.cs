using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly PaymentContext _db;
    public AccountsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] string? q,
        [FromQuery] bool withDate = false,
        [FromQuery] bool dedupe = false,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var query = _db.Payments.AsNoTracking().Where(p => p.Account != null && p.Account != "");
        if (clientId.HasValue) query = query.Where(p => p.ClientId == clientId.Value);
        if (caseId.HasValue) query = query.Where(p => p.ClientCaseId == caseId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(p => p.Account!.ToLower().Contains(term));
        }

        if (!withDate)
        {
            var accounts = await query
                .GroupBy(p => p.Account!)
                .Select(g => new { Account = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Account)
                .Select(x => x.Account)
                .Take(take)
                .ToListAsync(ct);

            return Ok(accounts);
        }

        var pairsQuery = query.Select(p => new { Account = p.Account!, AccountDate = p.AccountDate, SortDate = p.AccountDate ?? p.Date });
        if (dedupe) pairsQuery = pairsQuery.Distinct();
        var result = await pairsQuery
            .OrderByDescending(x => x.SortDate).ThenBy(x => x.Account)
            .Select(x => new { account = x.Account, accountDate = x.AccountDate })
            .Take(take)
            .ToListAsync(ct);

        return Ok(result);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/legal-entities/{legalEntityId:int}/balance")]
public class LegalEntityBalancesController : ControllerBase
{
    private readonly PaymentContext _db;

    public LegalEntityBalancesController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetSummary(int legalEntityId, CancellationToken ct)
    {
        if (!await _db.LegalEntities.AsNoTracking().AnyAsync(entity => entity.Id == legalEntityId, ct))
        {
            return NotFound();
        }

        var balance = await ClientBalanceService.GetLegalEntityBalanceAsync(_db, legalEntityId, ct);
        return Ok(new ClientBalanceSummaryDto
        {
            LegalEntityId = legalEntityId,
            Balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
        });
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(int legalEntityId, CancellationToken ct)
    {
        if (!await _db.LegalEntities.AsNoTracking().AnyAsync(entity => entity.Id == legalEntityId, ct))
        {
            return NotFound();
        }

        var items = await _db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item => item.LegalEntityId == legalEntityId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new ClientBalanceTransactionDto
            {
                Id = item.Id,
                ClientId = item.ClientId,
                LegalEntityId = item.LegalEntityId,
                InvoiceId = item.InvoiceId,
                PaymentId = item.PaymentId,
                Amount = item.Amount,
                Type = item.Type,
                Direction = item.Direction,
                Description = item.Description,
                ExternalKey = item.ExternalKey,
                CreatedAt = item.CreatedAt,
                CreatedByUserName = item.CreatedByUserName,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyCredit(
        int legalEntityId,
        [FromBody] ApplyClientBalanceRequest request,
        CancellationToken ct)
    {
        var result = await ClientBalanceService.ApplyLegalEntityCreditAsync(
            _db,
            legalEntityId,
            request.PaymentId,
            request.Amount,
            DateTime.UtcNow,
            HttpContext,
            ct,
            description: request.Description,
            sourcePaymentId: request.SourcePaymentId);

        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        await _db.SaveChangesAsync(ct);

        var balance = await ClientBalanceService.GetLegalEntityBalanceAsync(_db, legalEntityId, ct);
        return Ok(new
        {
            appliedAmount = result.AppliedAmount,
            balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
        });
    }
}

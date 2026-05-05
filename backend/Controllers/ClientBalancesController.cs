using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/clients/{clientId:int}/balance")]
public class ClientBalancesController : ControllerBase
{
    private readonly PaymentContext _db;

    public ClientBalancesController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetSummary(int clientId, CancellationToken ct)
    {
        if (!await _db.Clients.AsNoTracking().AnyAsync(client => client.Id == clientId, ct))
        {
            return NotFound();
        }

        var balance = await ClientBalanceService.GetBalanceAsync(_db, clientId, ct);
        return Ok(new ClientBalanceSummaryDto
        {
            ClientId = clientId,
            Balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
        });
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions(int clientId, CancellationToken ct)
    {
        if (!await _db.Clients.AsNoTracking().AnyAsync(client => client.Id == clientId, ct))
        {
            return NotFound();
        }

        var items = await _db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item => item.ClientId == clientId)
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
                CreatedAt = item.CreatedAt,
                CreatedByUserName = item.CreatedByUserName,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyCredit(
        int clientId,
        [FromBody] ApplyClientBalanceRequest request,
        CancellationToken ct)
    {
        var result = await ClientBalanceService.ApplyCreditAsync(
            _db,
            clientId,
            request.PaymentId,
            request.Amount,
            DateTime.UtcNow,
            HttpContext,
            ct);

        if (!result.Success)
        {
            return BadRequest(new { message = result.Error });
        }

        await _db.SaveChangesAsync(ct);

        var balance = await ClientBalanceService.GetBalanceAsync(_db, clientId, ct);
        return Ok(new
        {
            appliedAmount = result.AppliedAmount,
            balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
        });
    }
}

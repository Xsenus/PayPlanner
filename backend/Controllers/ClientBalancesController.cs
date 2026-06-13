using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
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

    private Task<DataVisibilityAccess?> ResolveClientVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);

    private async Task<bool> CanEditPaymentsAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return User?.IsInRole("admin") == true || User?.Identity?.IsAuthenticated != true;
        }

        if (User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Payments, ct);
        return permission?.CanEdit == true;
    }

    private async Task<IActionResult?> RequireVisibleClientAsync(int clientId, CancellationToken ct)
    {
        var visibility = await ResolveClientVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var isVisible = await _db.Clients
            .AsNoTracking()
            .ApplyClientVisibility(visibility)
            .AnyAsync(client => client.Id == clientId, ct);

        return isVisible ? null : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> GetSummary(int clientId, CancellationToken ct)
    {
        if (await RequireVisibleClientAsync(clientId, ct) is { } forbidden)
        {
            return forbidden;
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
        if (await RequireVisibleClientAsync(clientId, ct) is { } forbidden)
        {
            return forbidden;
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
                ExternalKey = item.ExternalKey,
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
        if (await RequireVisibleClientAsync(clientId, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanEditPaymentsAsync(ct))
        {
            return Forbid();
        }

        var result = await ClientBalanceService.ApplyCreditAsync(
            _db,
            clientId,
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

        var balance = await ClientBalanceService.GetBalanceAsync(_db, clientId, ct);
        return Ok(new
        {
            appliedAmount = result.AppliedAmount,
            balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
        });
    }
}

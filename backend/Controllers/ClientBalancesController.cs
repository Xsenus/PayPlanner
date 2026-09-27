using System.Data;
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

    private async Task<bool> CanViewFullBalanceAsync(CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);
        return visibility?.SeesAll == true;
    }

    private Task<bool> CanEditPaymentsAsync(CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(
            User,
            RolePermissionCatalog.Payments,
            permission => permission.CanEdit,
            ct);

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

        if (!await CanViewFullBalanceAsync(ct))
        {
            return Forbid();
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

        if (!await CanViewFullBalanceAsync(ct))
        {
            return Forbid();
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

        if (!await CanEditPaymentsAsync(ct) || !await CanViewFullBalanceAsync(ct))
        {
            return Forbid();
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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
            await transaction.CommitAsync(ct);
            return Ok(new
            {
                appliedAmount = result.AppliedAmount,
                balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero),
            });
        }
        catch (Exception exception) when (FinancialConcurrencyGuard.IsSerializationFailure(exception))
        {
            _db.ChangeTracker.Clear();
            return Conflict(new { message = FinancialConcurrencyGuard.ConflictMessage });
        }
    }
}

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
[Route("api/legal-entities/{legalEntityId:int}/balance")]
public class LegalEntityBalancesController : ControllerBase
{
    private readonly PaymentContext _db;

    public LegalEntityBalancesController(PaymentContext db)
    {
        _db = db;
    }

    private Task<DataVisibilityAccess?> ResolveLegalEntityVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.LegalEntities, ct);

    private Task<bool> CanViewPaymentsAsync(CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(
            User,
            RolePermissionCatalog.Payments,
            permission => permission.CanView,
            ct);

    private Task<bool> CanEditPaymentsAsync(CancellationToken ct)
        => _db.HasUserSectionPermissionAsync(
            User,
            RolePermissionCatalog.Payments,
            permission => permission.CanEdit,
            ct);

    private async Task<IActionResult?> RequireVisibleLegalEntityAsync(int legalEntityId, CancellationToken ct)
    {
        var visibility = await ResolveLegalEntityVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var isVisible = await _db.LegalEntities
            .AsNoTracking()
            .ApplyLegalEntityVisibility(visibility)
            .AnyAsync(entity => entity.Id == legalEntityId, ct);

        return isVisible ? null : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> GetSummary(int legalEntityId, CancellationToken ct)
    {
        if (!await CanViewPaymentsAsync(ct))
        {
            return Forbid();
        }

        if (await RequireVisibleLegalEntityAsync(legalEntityId, ct) is { } forbidden)
        {
            return forbidden;
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
        if (!await CanViewPaymentsAsync(ct))
        {
            return Forbid();
        }

        if (await RequireVisibleLegalEntityAsync(legalEntityId, ct) is { } forbidden)
        {
            return forbidden;
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
        if (await RequireVisibleLegalEntityAsync(legalEntityId, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanEditPaymentsAsync(ct))
        {
            return Forbid();
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
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

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
        if (await RequireVisibleLegalEntityAsync(legalEntityId, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanViewFullBalanceAsync(ct))
        {
            return Forbid();
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
        if (await RequireVisibleLegalEntityAsync(legalEntityId, ct) is { } forbidden)
        {
            return forbidden;
        }

        if (!await CanViewFullBalanceAsync(ct))
        {
            return Forbid();
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

    [HttpGet("applications/{requestId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetApplication(int legalEntityId, Guid requestId, [FromQuery] int paymentId, CancellationToken ct)
        => await PayPlanner.Api.Services.Payments.BalanceApplicationService.InspectAsync(_db, null, legalEntityId, requestId, paymentId, HttpContext,
            async token =>
            {
                if (await RequireVisibleLegalEntityAsync(legalEntityId, token) is { } denied) return denied;
                if (!await CanEditPaymentsAsync(token) || !await CanViewFullBalanceAsync(token)) return Forbid();
                return null;
            }, ct);

    [HttpPost("applications/{requestId:guid}/cancel")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> CancelApplication(int legalEntityId, Guid requestId, [FromBody] CancelBalanceApplicationRequest request, CancellationToken ct)
        => await PayPlanner.Api.Services.Payments.BalanceApplicationService.CancelAsync(_db, null, legalEntityId, requestId, request.PaymentId, HttpContext,
            async token =>
            {
                if (await RequireVisibleLegalEntityAsync(legalEntityId, token) is { } denied) return denied;
                if (!await CanEditPaymentsAsync(token) || !await CanViewFullBalanceAsync(token)) return Forbid();
                return null;
            }, ct);

    [HttpPost("applications/{requestId:guid}/apply")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApplyManaged(int legalEntityId, Guid requestId, [FromBody] ApplyClientBalanceRequest request, CancellationToken ct)
    {
        if (request.RequestId.HasValue && request.RequestId.Value != requestId)
            return BadRequest(new { message = "Идентификатор зачёта не совпадает с выбранной попыткой." });
        request.RequestId = requestId;
        return await ApplyCredit(legalEntityId, request, ct);
    }

    [HttpPost("apply")]
    public Task<IActionResult> ApplyCredit(
        int legalEntityId,
        [FromBody] ApplyClientBalanceRequest request,
        CancellationToken ct)
    {
        async Task<IActionResult?> AuthorizeAsync(CancellationToken token)
        {
            if (await RequireVisibleLegalEntityAsync(legalEntityId, token) is { } denied) return denied;
            return !await CanEditPaymentsAsync(token) || !await CanViewFullBalanceAsync(token) ? Forbid() : null;
        }
        return request.RequestId.HasValue
            ? PayPlanner.Api.Services.Payments.BalanceApplicationService.ApplyAsync(_db, null, legalEntityId, request, HttpContext, AuthorizeAsync, ct)
            : PayPlanner.Api.Services.Payments.BalanceApplicationService.ApplyLegacyAsync(_db, null, legalEntityId, request, HttpContext, AuthorizeAsync, ct);
    }
}

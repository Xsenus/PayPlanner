using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/portal/admin")]
public sealed class ClientPortalAdminController(IClientPortalService service, PaymentContext db) : ControllerBase
{
    [HttpPost("access")]
    public async Task<ActionResult<ClientPortalAdminAccountDto>> SaveAccess(
        [FromBody] ClientPortalAccessRequest request,
        CancellationToken ct)
    {
        var actorId = GetActorId();
        if (!actorId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await service.CreateOrUpdateAccessAsync(
                request,
                actorId.Value,
                User.Identity?.Name ?? "Администратор",
                ct));
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpGet("accounts/{accountId:int}")]
    public async Task<ActionResult<ClientPortalAdminAccountDto>> GetAccount(int accountId, CancellationToken ct)
    {
        try
        {
            var account = await service.GetAccountAsync(accountId, ct);
            return account is null ? NotFound() : Ok(account);
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpGet("cases/{caseId:int}/accounts")]
    public async Task<ActionResult<List<ClientPortalCaseAccessDto>>> GetCaseAccounts(int caseId, CancellationToken ct)
    {
        try
        {
            return Ok(await service.GetCaseAccountsAsync(caseId, ct));
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpGet("cases/{caseId:int}/audit")]
    public async Task<ActionResult<List<ClientPortalAuditDto>>> GetCaseAudit(int caseId, CancellationToken ct)
    {
        try
        {
            return Ok(await service.GetCaseAuditAsync(caseId, ct));
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpPost("cases/{caseId:int}/preview")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ClientPortalCaseDto>> PreviewCase(
        int caseId,
        [FromBody] ClientPortalCasePreviewRequest request,
        CancellationToken ct)
    {
        var actorId = GetActorId();
        if (actorId is not > 0)
        {
            return Unauthorized();
        }

        var access = await db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
        if (access?.Visibility.IsAdmin != true || access.Permission?.CanView != true)
        {
            return Forbid();
        }
        if (!await db.ClientCases.AsNoTracking().ApplyClientCaseVisibility(access.Visibility).AnyAsync(item => item.Id == caseId, ct))
        {
            return NotFound();
        }

        try
        {
            var preview = await service.PreviewCaseAsync(
                caseId, request, actorId.Value, User.Identity?.Name ?? "Администратор", ct);
            return preview is null ? NotFound() : Ok(preview);
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpPost("accounts/{accountId:int}/login")]
    public async Task<ActionResult<ClientPortalChallengeCreatedDto>> SendLogin(
        int accountId, [FromQuery] int? caseId, CancellationToken ct)
    {
        try
        {
            return Ok(await service.SendLoginAsync(accountId, ct, caseId));
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpDelete("accounts/{accountId:int}")]
    public async Task<IActionResult> Revoke(
        int accountId,
        [FromBody] ClientPortalRevokeRequest? request,
        CancellationToken ct)
    {
        var actorId = GetActorId();
        if (!actorId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            await service.RevokeAccountAsync(
                accountId,
                actorId.Value,
                User.Identity?.Name ?? "Администратор",
                request?.Reason,
                ct);
            return NoContent();
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    [HttpDelete("accounts/{accountId:int}/cases/{caseId:int}")]
    public async Task<IActionResult> RevokeCaseAccess(
        int accountId,
        int caseId,
        [FromBody] ClientPortalRevokeRequest? request,
        CancellationToken ct)
    {
        var actorId = GetActorId();
        if (!actorId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            await service.RevokeCaseAccessAsync(accountId, caseId, actorId.Value, request?.Reason, ct);
            return NoContent();
        }
        catch (ClientPortalException ex)
        {
            return MapError(ex);
        }
    }

    private int? GetActorId()
        => int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private ActionResult MapError(ClientPortalException ex)
        => ex.Code switch
        {
            "validation_error" or "access_mismatch" => BadRequest(new { ex.Code, message = ex.Message }),
            "case_not_found" or "account_not_found" or "account_unavailable" or "grant_not_found" => NotFound(new { ex.Code, message = ex.Message }),
            "feature_disabled" => StatusCode(StatusCodes.Status409Conflict, new { ex.Code, message = ex.Message }),
            "delivery_failed" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { ex.Code, message = ex.Message }),
            _ => Conflict(new { ex.Code, message = ex.Message }),
        };
}

public sealed class ClientPortalRevokeRequest
{
    public string? Reason { get; set; }
}

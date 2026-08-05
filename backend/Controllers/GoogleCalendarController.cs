using System.Text;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/google-calendar")]
public sealed class GoogleCalendarController : ControllerBase
{
    private readonly IGoogleCalendarSyncService _googleCalendarSyncService;

    public GoogleCalendarController(IGoogleCalendarSyncService googleCalendarSyncService)
    {
        _googleCalendarSyncService = googleCalendarSyncService;
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar)]
    [HttpGet("status")]
    public async Task<ActionResult<GoogleCalendarStatusDto>> GetStatus(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _googleCalendarSyncService.GetStatusAsync(userId.Value, Request, ct));
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)]
    [HttpPost("auth/start")]
    public async Task<ActionResult<GoogleCalendarStartAuthResponseDto>> StartAuth(
        [FromBody] StartGoogleCalendarAuthRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _googleCalendarSyncService.StartAuthAsync(userId.Value, request, Request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "GoogleCalendarAuthStartFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar)]
    [HttpGet("auth/status/{state}")]
    public async Task<ActionResult<GoogleCalendarAuthStatusDto>> GetAuthStatus(string state, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _googleCalendarSyncService.GetAuthStatusAsync(userId.Value, state, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "GoogleCalendarAuthStateNotFound", detail: ex.Message, statusCode: 404);
        }
    }

    [AllowAnonymous]
    [HttpGet("auth/callback")]
    [EnableRateLimiting(AnonymousIntegrationRateLimitPolicies.OAuthCallback)]
    public async Task<IActionResult> Callback(
        [FromQuery, StringLength(128)] string? state,
        [FromQuery, StringLength(4096)] string? code,
        [FromQuery, StringLength(256)] string? error,
        [FromQuery(Name = "error_description"), StringLength(1000)] string? errorDescription,
        CancellationToken ct)
    {
        var html = await _googleCalendarSyncService.HandleCallbackAsync(state, code, error, errorDescription, Request, ct);
        return Content(html, "text/html", Encoding.UTF8);
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)]
    [HttpPut("settings")]
    public async Task<ActionResult<GoogleCalendarStatusDto>> UpdateSettings(
        [FromBody] UpdateGoogleCalendarSettingsRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _googleCalendarSyncService.UpdateSettingsAsync(userId.Value, request, Request, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "GoogleCalendarConnectionNotFound", detail: ex.Message, statusCode: 404);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(title: "GoogleCalendarUserManagedDisabled", detail: ex.Message, statusCode: 403);
        }
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)]
    [HttpPost("sync-now")]
    public async Task<ActionResult<GoogleCalendarSyncNowResultDto>> SyncNow(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _googleCalendarSyncService.SyncNowAsync(userId.Value, Request, ct));
        }
        catch (CalendarManualSyncCooldownException ex)
        {
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((ex.AvailableAt - DateTime.UtcNow).TotalSeconds)).ToString();
            return Problem(title: "GoogleCalendarManualSyncCooldown", detail: ex.Message, statusCode: 429);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "GoogleCalendarSyncFailed", detail: ex.Message, statusCode: 400);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(title: "GoogleCalendarUserManagedDisabled", detail: ex.Message, statusCode: 403);
        }
    }

    [Authorize]
    [RequireSectionPermission(RolePermissionCatalog.Calendar, RequireSectionPermissionOperations.Edit)]
    [HttpDelete("connection")]
    public async Task<ActionResult<GoogleCalendarStatusDto>> Disconnect(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _googleCalendarSyncService.DisconnectAsync(userId.Value, Request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "GoogleCalendarDisconnectFailed", detail: ex.Message, statusCode: 409);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(title: "GoogleCalendarUserManagedDisabled", detail: ex.Message, statusCode: 403);
        }
    }

    [AllowAnonymous]
    [HttpPost("webhook")]
    [EnableRateLimiting(AnonymousIntegrationRateLimitPolicies.Webhook)]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        try
        {
            await _googleCalendarSyncService.HandleWebhookAsync(Request, ct);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(title: "GoogleCalendarWebhookUnauthorized", detail: ex.Message, statusCode: 401);
        }
    }
}

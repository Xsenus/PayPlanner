using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
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
    public async Task<IActionResult> Callback(
        [FromQuery] string? state,
        [FromQuery] string? code,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct)
    {
        var html = await _googleCalendarSyncService.HandleCallbackAsync(state, code, error, errorDescription, Request, ct);
        return Content(html, "text/html", Encoding.UTF8);
    }

    [Authorize]
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
    }

    [Authorize]
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
        catch (InvalidOperationException ex)
        {
            return Problem(title: "GoogleCalendarSyncFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [Authorize]
    [HttpDelete("connection")]
    public async Task<ActionResult<GoogleCalendarStatusDto>> Disconnect(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _googleCalendarSyncService.DisconnectAsync(userId.Value, Request, ct));
    }

    [AllowAnonymous]
    [HttpPost("webhook")]
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

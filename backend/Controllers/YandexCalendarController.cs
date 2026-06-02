using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/yandex-calendar")]
public sealed class YandexCalendarController : ControllerBase
{
    private readonly IYandexCalendarSyncService _yandexCalendarSyncService;

    public YandexCalendarController(IYandexCalendarSyncService yandexCalendarSyncService)
    {
        _yandexCalendarSyncService = yandexCalendarSyncService;
    }

    [Authorize]
    [HttpGet("status")]
    public async Task<ActionResult<YandexCalendarStatusDto>> GetStatus(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return Ok(await _yandexCalendarSyncService.GetStatusAsync(userId.Value, Request, ct));
    }

    [Authorize]
    [HttpPost("auth/start")]
    public async Task<ActionResult<YandexCalendarStartAuthResponseDto>> StartAuth(
        [FromBody] StartYandexCalendarAuthRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _yandexCalendarSyncService.StartAuthAsync(userId.Value, request, Request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "YandexCalendarAuthStartFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [Authorize]
    [HttpGet("auth/status/{state}")]
    public async Task<ActionResult<YandexCalendarAuthStatusDto>> GetAuthStatus(string state, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _yandexCalendarSyncService.GetAuthStatusAsync(userId.Value, state, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "YandexCalendarAuthStateNotFound", detail: ex.Message, statusCode: 404);
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
        var html = await _yandexCalendarSyncService.HandleCallbackAsync(state, code, error, errorDescription, Request, ct);
        return Content(html, "text/html", Encoding.UTF8);
    }

    [Authorize]
    [HttpPut("settings")]
    public async Task<ActionResult<YandexCalendarStatusDto>> UpdateSettings(
        [FromBody] UpdateYandexCalendarSettingsRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _yandexCalendarSyncService.UpdateSettingsAsync(userId.Value, request, Request, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "YandexCalendarConnectionNotFound", detail: ex.Message, statusCode: 404);
        }
    }

    [Authorize]
    [HttpPost("sync-now")]
    public async Task<ActionResult<YandexCalendarSyncNowResultDto>> SyncNow(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _yandexCalendarSyncService.SyncNowAsync(userId.Value, Request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "YandexCalendarSyncFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [Authorize]
    [HttpDelete("connection")]
    public async Task<ActionResult<YandexCalendarStatusDto>> Disconnect(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await _yandexCalendarSyncService.DisconnectAsync(userId.Value, Request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "YandexCalendarDisconnectFailed", detail: ex.Message, statusCode: 409);
        }
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class ExternalAuthController : ControllerBase
{
    private readonly ExternalAuthService _externalAuthService;

    public ExternalAuthController(ExternalAuthService externalAuthService)
    {
        _externalAuthService = externalAuthService;
    }

    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProviders(CancellationToken cancellationToken)
        => Ok(await _externalAuthService.GetPublicSettingsAsync(Request, cancellationToken));

    [HttpPost("external/start")]
    [AllowAnonymous]
    public async Task<IActionResult> StartExternal([FromBody] ExternalAuthStartRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _externalAuthService.StartExternalFlowAsync(
                request.Provider,
                request.ReturnUrl,
                request.Intent,
                User.GetUserId(),
                Request,
                cancellationToken);

            return Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return Problem(title: "ExternalAuthStartFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("external/status/{state}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetExternalStatus(string state, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.GetExternalStatusForClientAsync(state, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "ExternalAuthStateNotFound", detail: ex.Message, statusCode: 404);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "ExternalAuthStatusFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("external/callback/{provider}")]
    [AllowAnonymous]
    public async Task<IActionResult> ExternalCallback(
        string provider,
        [FromQuery] string? state,
        [FromQuery] string? code,
        [FromQuery(Name = "device_id")] string? deviceId,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken cancellationToken)
    {
        var html = await _externalAuthService.HandleExternalCallbackForClientAsync(
            provider,
            state,
            code,
            deviceId,
            error,
            errorDescription,
            Request,
            cancellationToken);

        return Content(html, "text/html", Encoding.UTF8);
    }

    [HttpPost("telegram/widget")]
    [AllowAnonymous]
    public async Task<IActionResult> CompleteTelegramWidget(
        [FromBody] TelegramWidgetLoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.CompleteTelegramWidgetFlowAsync(request, User.GetUserId(), cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramWidgetAuthFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPost("telegram/bot/start")]
    [AllowAnonymous]
    public async Task<IActionResult> StartTelegramBot(
        [FromBody] TelegramBotAuthStartRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.StartTelegramBotFlowAsync(request.ReturnUrl, request.Intent, User.GetUserId(), Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramBotAuthStartFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("telegram/bot/status/{state}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetTelegramBotStatus(string state, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.GetTelegramBotStatusForClientAsync(state, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(title: "TelegramBotAuthStateNotFound", detail: ex.Message, statusCode: 404);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramBotAuthStatusFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPost("telegram/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> TelegramWebhook([FromBody] JsonDocument request, CancellationToken cancellationToken)
    {
        try
        {
            await _externalAuthService.HandleTelegramWebhookFlowAsync(request, Request, cancellationToken);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Problem(title: "TelegramWebhookUnauthorized", detail: ex.Message, statusCode: 401);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramWebhookFailed", detail: ex.Message, statusCode: 400);
        }
    }
}

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class ExternalAuthController : ControllerBase
{
    private const string ExternalUnavailableDetail = "Выбранный способ входа сейчас недоступен. Повторите попытку позже.";
    private const string AuthStateNotFoundDetail = "Запрос авторизации не найден или уже устарел.";
    private const string AuthStatusFailedDetail = "Не удалось проверить состояние авторизации. Начните вход заново.";
    private const string TelegramAuthFailedDetail = "Не удалось подтвердить вход через Telegram. Повторите попытку.";
    private readonly ExternalAuthService _externalAuthService;

    public ExternalAuthController(ExternalAuthService externalAuthService)
    {
        _externalAuthService = externalAuthService;
    }

    /// <summary>Возвращает только готовые к использованию способы внешнего входа.</summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalSettingsPolicy)]
    public async Task<IActionResult> GetProviders(CancellationToken cancellationToken)
        => Ok(await _externalAuthService.GetPublicSettingsAsync(Request, cancellationToken));

    /// <summary>Создаёт короткоживущий OAuth-запрос для входа или привязки аккаунта.</summary>
    [HttpPost("external/start")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalStartPolicy)]
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
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            return Problem(title: "ExternalAuthStartFailed", detail: ExternalUnavailableDetail, statusCode: 400);
        }
    }

    /// <summary>Проверяет одноразовое состояние OAuth-запроса и выдаёт результат только один раз.</summary>
    [HttpGet("external/status/{state}")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalStatusPolicy)]
    public async Task<IActionResult> GetExternalStatus(
        [StringLength(100, MinimumLength = 1)] string state,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.GetExternalStatusForClientAsync(state, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Problem(title: "ExternalAuthStateNotFound", detail: AuthStateNotFoundDetail, statusCode: 404);
        }
        catch (InvalidOperationException)
        {
            return Problem(title: "ExternalAuthStatusFailed", detail: AuthStatusFailedDetail, statusCode: 400);
        }
    }

    /// <summary>Принимает callback OAuth-провайдера и возвращает безопасную HTML-страницу для popup.</summary>
    [HttpGet("external/callback/{provider}")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalCallbackPolicy)]
    public async Task<IActionResult> ExternalCallback(
        [StringLength(20, MinimumLength = 1)] string provider,
        [FromQuery, StringLength(100)] string? state,
        [FromQuery, StringLength(4096)] string? code,
        [FromQuery(Name = "device_id"), StringLength(512)] string? deviceId,
        [FromQuery, StringLength(256)] string? error,
        [FromQuery(Name = "error_description"), StringLength(1000)] string? errorDescription,
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

    /// <summary>Проверяет подписанный Telegram Login Widget payload и завершает вход.</summary>
    [HttpPost("telegram/widget")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalStartPolicy)]
    public async Task<IActionResult> CompleteTelegramWidget(
        [FromBody] TelegramWidgetLoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.CompleteTelegramWidgetFlowAsync(request, User.GetUserId(), cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return Problem(title: "TelegramWidgetAuthFailed", detail: TelegramAuthFailedDetail, statusCode: 400);
        }
    }

    /// <summary>Создаёт одноразовую ссылку для входа или привязки через Telegram-бота.</summary>
    [HttpPost("telegram/bot/start")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalStartPolicy)]
    public async Task<IActionResult> StartTelegramBot(
        [FromBody] TelegramBotAuthStartRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.StartTelegramBotFlowAsync(request.ReturnUrl, request.Intent, User.GetUserId(), Request, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return Problem(title: "TelegramBotAuthStartFailed", detail: ExternalUnavailableDetail, statusCode: 400);
        }
    }

    /// <summary>Проверяет одноразовое состояние входа через Telegram-бота.</summary>
    [HttpGet("telegram/bot/status/{state}")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.ExternalStatusPolicy)]
    public async Task<IActionResult> GetTelegramBotStatus(
        [StringLength(100, MinimumLength = 1)] string state,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.GetTelegramBotStatusForClientAsync(state, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Problem(title: "TelegramBotAuthStateNotFound", detail: AuthStateNotFoundDetail, statusCode: 404);
        }
        catch (InvalidOperationException)
        {
            return Problem(title: "TelegramBotAuthStatusFailed", detail: AuthStatusFailedDetail, statusCode: 400);
        }
    }

    /// <summary>Принимает подтверждённое секретным заголовком обновление Telegram.</summary>
    [HttpPost("telegram/webhook")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimitPolicies.TelegramWebhookPolicy)]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> TelegramWebhook([FromBody] JsonDocument request, CancellationToken cancellationToken)
    {
        try
        {
            await _externalAuthService.HandleTelegramWebhookFlowAsync(request, Request, cancellationToken);
            return Ok(new { ok = true });
        }
        catch (UnauthorizedAccessException)
        {
            return Problem(title: "TelegramWebhookUnauthorized", detail: "Запрос Telegram не подтверждён.", statusCode: 401);
        }
        catch (InvalidOperationException)
        {
            return Problem(title: "TelegramWebhookFailed", detail: TelegramAuthFailedDetail, statusCode: 400);
        }
    }
}

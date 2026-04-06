using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/auth/admin/providers")]
[Authorize(Policy = "Admin")]
public class AuthProvidersAdminController : ControllerBase
{
    private readonly ExternalAuthService _externalAuthService;

    public AuthProvidersAdminController(ExternalAuthService externalAuthService)
    {
        _externalAuthService = externalAuthService;
    }

    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
        => Ok(await _externalAuthService.GetAdminSettingsAsync(Request, cancellationToken));

    [HttpPut]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] AuthProviderSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.UpdateSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "AuthProviderSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("google")]
    public async Task<IActionResult> UpdateGoogleSettings(
        [FromBody] ExternalOAuthProviderSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SaveGoogleSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "GoogleSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("vk")]
    public async Task<IActionResult> UpdateVkSettings(
        [FromBody] ExternalOAuthProviderSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SaveVkSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "VkSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("yandex")]
    public async Task<IActionResult> UpdateYandexSettings(
        [FromBody] ExternalOAuthProviderSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SaveYandexSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "YandexSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("telegram/bot")]
    public async Task<IActionResult> UpdateTelegramBotSettings(
        [FromBody] TelegramLoginBotSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SaveTelegramBotSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramBotSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPut("telegram/widget")]
    public async Task<IActionResult> UpdateTelegramWidgetSettings(
        [FromBody] TelegramWidgetSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SaveTelegramWidgetSettingsAsync(request, Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramWidgetSettingsSaveFailed", detail: ex.Message, statusCode: 400);
        }
    }

    [HttpPost("telegram/sync")]
    public async Task<IActionResult> SyncTelegram(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _externalAuthService.SyncTelegramBotAsync(Request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(title: "TelegramSyncFailed", detail: ex.Message, statusCode: 400);
        }
    }
}

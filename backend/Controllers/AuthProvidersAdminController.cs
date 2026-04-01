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

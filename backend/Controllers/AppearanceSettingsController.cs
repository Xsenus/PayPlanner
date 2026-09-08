using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/appearance-settings")]
[Authorize]
public sealed class AppearanceSettingsController : ControllerBase
{
    public const string SettingKey = "appearance.globalTheme.v1";
    public const string DefaultThemeKey = "standard";

    private static readonly HashSet<string> AllowedThemeKeys = new(StringComparer.Ordinal)
    {
        "standard",
        "business",
        "ocean",
        "emerald",
        "warm",
        "violet",
        "graphite",
        "midnight",
    };

    private readonly PaymentContext _db;
    private readonly RoleManagementAccessService _roleAccess;

    public AppearanceSettingsController(PaymentContext db, RoleManagementAccessService roleAccess)
    {
        _db = db;
        _roleAccess = roleAccess;
    }

    [HttpGet]
    public async Task<ActionResult<AppearanceSettingsDto>> Get(CancellationToken ct)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == SettingKey)
            .Select(item => new { item.Value, item.UpdatedAt })
            .SingleOrDefaultAsync(ct);

        return Ok(new AppearanceSettingsDto(
            NormalizeThemeKey(setting?.Value),
            setting?.UpdatedAt));
    }

    [HttpPut]
    public async Task<ActionResult<AppearanceSettingsDto>> Update(
        [FromBody] UpdateAppearanceSettingsRequest request,
        CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin)
        {
            return Forbid();
        }

        var requestedThemeKey = request.ThemeKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(requestedThemeKey) || !AllowedThemeKeys.Contains(requestedThemeKey))
        {
            return BadRequest(new { message = "Выбрана неизвестная тема оформления." });
        }

        var now = DateTime.UtcNow;
        var setting = await _db.SystemSettings.SingleOrDefaultAsync(item => item.Key == SettingKey, ct);
        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = SettingKey,
                Value = requestedThemeKey,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = requestedThemeKey;
            setting.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new AppearanceSettingsDto(requestedThemeKey, setting.UpdatedAt));
    }

    private static string NormalizeThemeKey(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && AllowedThemeKeys.Contains(normalized)
            ? normalized
            : DefaultThemeKey;
    }
}

public sealed record AppearanceSettingsDto(string ThemeKey, DateTime? UpdatedAt);

public sealed record UpdateAppearanceSettingsRequest(string? ThemeKey);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/calendar-sync")]
public sealed class CalendarSyncController : ControllerBase
{
    private const string SettingsKey = "calendarSync.settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsSnapshot<AppFeatureFlagsOptions> _featureFlags;
    private readonly PaymentContext _db;

    public CalendarSyncController(
        IOptionsSnapshot<AppFeatureFlagsOptions> featureFlags,
        PaymentContext db)
    {
        _featureFlags = featureFlags;
        _db = db;
    }

    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<CalendarSyncProviderDto>> GetProviders()
        => Ok(CalendarSyncProviderRegistry.BuildProviders(_featureFlags.Value.Legal.ExternalCalendarSync));

    [HttpGet("readiness")]
    public async Task<ActionResult<CalendarSyncReadinessDto>> GetReadiness(CancellationToken ct)
        => Ok(CalendarSyncProviderRegistry.BuildReadiness(
            _featureFlags.Value.Legal.ExternalCalendarSync,
            await GetSettingsAsync(ct)));

    [HttpGet("settings")]
    public async Task<ActionResult<CalendarSyncSettingsDto>> GetSettings(CancellationToken ct)
        => Ok(await GetSettingsAsync(ct));

    [HttpPut("settings")]
    public async Task<ActionResult<CalendarSyncSettingsDto>> UpdateSettings(
        [FromBody] UpdateCalendarSyncSettingsRequest request,
        CancellationToken ct)
    {
        var normalized = CalendarSyncProviderRegistry.NormalizeSettings(new CalendarSyncSettingsDto
        {
            SyncMode = request.SyncMode ?? string.Empty,
            ConflictResolutionMode = request.ConflictResolutionMode ?? string.Empty,
            RequireManualConfirmation = request.RequireManualConfirmation ?? true,
            DryRunConfirmed = request.DryRunConfirmed ?? false,
            Notes = request.Notes ?? string.Empty,
            UpdatedAt = DateTime.UtcNow,
        });
        normalized.UpdatedAt = DateTime.UtcNow;

        var setting = await _db.SystemSettings.FirstOrDefaultAsync(item => item.Key == SettingsKey, ct);
        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = SettingsKey,
                CreatedAt = normalized.UpdatedAt.Value,
            };
            _db.SystemSettings.Add(setting);
        }

        setting.Value = JsonSerializer.Serialize(normalized, JsonOptions);
        setting.UpdatedAt = normalized.UpdatedAt.Value;
        await _db.SaveChangesAsync(ct);

        return Ok(normalized);
    }

    private async Task<CalendarSyncSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == SettingsKey, ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            if (settings is not null)
            {
                settings.UpdatedAt ??= setting.UpdatedAt;
                return CalendarSyncProviderRegistry.NormalizeSettings(settings);
            }
        }
        catch (JsonException)
        {
            // Invalid stored settings should not break the integration diagnostics screen.
        }

        var defaults = CalendarSyncProviderRegistry.BuildDefaultSettings();
        defaults.UpdatedAt = setting.UpdatedAt;
        return defaults;
    }
}

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
    public async Task<ActionResult<IReadOnlyList<CalendarSyncProviderDto>>> GetProviders(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        return Ok(CalendarSyncProviderRegistry.BuildProviders(settings.ExternalCalendarSyncEnabled == true));
    }

    [HttpGet("readiness")]
    public async Task<ActionResult<CalendarSyncReadinessDto>> GetReadiness(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        return Ok(CalendarSyncProviderRegistry.BuildReadiness(settings.ExternalCalendarSyncEnabled == true, settings));
    }

    [HttpGet("settings")]
    public async Task<ActionResult<CalendarSyncSettingsDto>> GetSettings(CancellationToken ct)
        => Ok(await GetSettingsAsync(ct));

    [HttpPut("settings")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<CalendarSyncSettingsDto>> UpdateSettings(
        [FromBody] UpdateCalendarSyncSettingsRequest request,
        CancellationToken ct)
    {
        var previousSettings = await GetSettingsAsync(ct);
        var normalized = CalendarSyncProviderRegistry.NormalizeSettings(new CalendarSyncSettingsDto
        {
            ExternalCalendarSyncEnabled = request.ExternalCalendarSyncEnabled ?? previousSettings.ExternalCalendarSyncEnabled,
            SyncMode = request.SyncMode ?? string.Empty,
            ConflictResolutionMode = request.ConflictResolutionMode ?? string.Empty,
            UserControlMode = request.UserControlMode ?? string.Empty,
            DefaultSyncFromDate = request.DefaultSyncFromDate,
            InactiveUserAction = request.InactiveUserAction ?? string.Empty,
            RequireManualConfirmation = request.RequireManualConfirmation ?? true,
            DryRunConfirmed = request.DryRunConfirmed ?? false,
            Notes = request.Notes ?? string.Empty,
            UpdatedAt = DateTime.UtcNow,
        }, _featureFlags.Value.Legal.ExternalCalendarSync);
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
        ApplyAdministrativeDefaults(normalized);
        _db.ObjectAuditEvents.Add(CalendarExportAudit.CreateSettingsUpdatedEvent(User, previousSettings, normalized));
        await _db.SaveChangesAsync(ct);

        return Ok(normalized);
    }

    [HttpGet("logs")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<CalendarSyncLogPageDto>> GetLogs(
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] string? operation,
        [FromQuery] int? userId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var googleLogs = provider is null or "" or "google" or "google-calendar"
            ? await _db.GoogleCalendarSyncLogs
                .AsNoTracking()
                .Include(item => item.Connection)
                .ThenInclude(connection => connection!.User)
                .Select(item => new CalendarSyncLogEntryDto
                {
                    Provider = "google-calendar",
                    ProviderLabel = "Google Calendar",
                    Id = item.Id,
                    ConnectionId = item.ConnectionId,
                    UserId = item.Connection == null ? null : item.Connection.UserId,
                    UserName = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.FullName,
                    UserEmail = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.Email,
                    TaskJournalItemId = item.TaskJournalItemId,
                    Direction = item.Direction,
                    Operation = item.Operation,
                    Status = item.Status,
                    Message = item.Message,
                    CreatedAt = item.CreatedAt,
                })
                .ToListAsync(ct)
            : [];

        var yandexLogs = provider is null or "" or "yandex" or "yandex-calendar"
            ? await _db.YandexCalendarSyncLogs
                .AsNoTracking()
                .Include(item => item.Connection)
                .ThenInclude(connection => connection!.User)
                .Select(item => new CalendarSyncLogEntryDto
                {
                    Provider = "yandex-calendar",
                    ProviderLabel = "Яндекс Календарь",
                    Id = item.Id,
                    ConnectionId = item.ConnectionId,
                    UserId = item.Connection == null ? null : item.Connection.UserId,
                    UserName = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.FullName,
                    UserEmail = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.Email,
                    TaskJournalItemId = item.TaskJournalItemId,
                    Direction = item.Direction,
                    Operation = item.Operation,
                    Status = item.Status,
                    Message = item.Message,
                    CreatedAt = item.CreatedAt,
                })
                .ToListAsync(ct)
            : [];

        var logs = googleLogs.Concat(yandexLogs).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            logs = logs.Where(item => string.Equals(item.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(operation))
        {
            logs = logs.Where(item => item.Operation.Contains(operation.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (userId.HasValue)
        {
            logs = logs.Where(item => item.UserId == userId.Value);
        }

        var orderedLogs = logs.OrderByDescending(item => item.CreatedAt).ToList();
        return Ok(new CalendarSyncLogPageDto
        {
            Items = orderedLogs.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = orderedLogs.Count,
            Page = page,
            PageSize = pageSize,
        });
    }

    private async Task<CalendarSyncSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var setting = await _db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == SettingsKey, ct);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.Value.Legal.ExternalCalendarSync);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            if (settings is not null)
            {
                settings.UpdatedAt ??= setting.UpdatedAt;
                return CalendarSyncProviderRegistry.NormalizeSettings(settings, _featureFlags.Value.Legal.ExternalCalendarSync);
            }
        }
        catch (JsonException)
        {
            // Invalid stored settings should not break the integration diagnostics screen.
        }

        var defaults = CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.Value.Legal.ExternalCalendarSync);
        defaults.UpdatedAt = setting.UpdatedAt;
        return defaults;
    }

    private void ApplyAdministrativeDefaults(CalendarSyncSettingsDto settings)
    {
        if (!string.Equals(settings.UserControlMode, "adminManaged", StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var connection in _db.GoogleCalendarConnections)
        {
            connection.SyncFromDate = settings.DefaultSyncFromDate;
            connection.ConflictResolutionMode = MapGoogleConflictMode(settings.ConflictResolutionMode);
            connection.UpdatedAt = now;
        }

        foreach (var connection in _db.YandexCalendarConnections)
        {
            connection.SyncFromDate = settings.DefaultSyncFromDate;
            connection.ConflictResolutionMode = MapYandexConflictMode(settings.ConflictResolutionMode);
            connection.UpdatedAt = now;
        }
    }

    private static string MapGoogleConflictMode(string mode)
        => mode switch
        {
            "crmWins" => "crmWins",
            "externalWins" => "googleWins",
            "latestWins" => "latestWins",
            _ => "latestWins",
        };

    private static string MapYandexConflictMode(string mode)
        => mode switch
        {
            "crmWins" => "crmWins",
            "externalWins" => "yandexWins",
            "latestWins" => "latestWins",
            _ => "latestWins",
        };
}

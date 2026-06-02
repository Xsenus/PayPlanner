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
        return Ok(CalendarSyncProviderRegistry.BuildProviders(
            settings.GoogleCalendarSyncEnabled == true,
            settings.YandexCalendarSyncEnabled == true));
    }

    [HttpGet("readiness")]
    public async Task<ActionResult<CalendarSyncReadinessDto>> GetReadiness(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        return Ok(CalendarSyncProviderRegistry.BuildReadiness(
            settings.GoogleCalendarSyncEnabled == true,
            settings.YandexCalendarSyncEnabled == true,
            settings));
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
        var googleCalendarSyncEnabled = request.GoogleCalendarSyncEnabled
            ?? (request.ExternalCalendarSyncEnabled.HasValue ? request.ExternalCalendarSyncEnabled.Value : previousSettings.GoogleCalendarSyncEnabled);
        var yandexCalendarSyncEnabled = request.YandexCalendarSyncEnabled
            ?? (request.ExternalCalendarSyncEnabled.HasValue ? request.ExternalCalendarSyncEnabled.Value : previousSettings.YandexCalendarSyncEnabled);
        var normalized = CalendarSyncProviderRegistry.NormalizeSettings(new CalendarSyncSettingsDto
        {
            GoogleCalendarSyncEnabled = googleCalendarSyncEnabled,
            YandexCalendarSyncEnabled = yandexCalendarSyncEnabled,
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
                .Select(item => new CalendarSyncLogRaw
                {
                    Provider = "google-calendar",
                    ProviderLabel = "Google Calendar",
                    Id = item.Id,
                    ConnectionId = item.ConnectionId,
                    UserId = item.Connection == null ? null : item.Connection.UserId,
                    UserName = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.FullName,
                    UserEmail = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.Email,
                    TaskJournalItemId = item.TaskJournalItemId,
                    TaskTitle = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.Title,
                    TaskObjectName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.ObjectName,
                    TaskAssigneeName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.AssigneeName,
                    CalendarName = item.CalendarSyncCalendar == null ? string.Empty : item.CalendarSyncCalendar.Name,
                    CalendarOwnerName = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.FullName,
                    CalendarOwnerEmail = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.Email,
                    Direction = item.Direction,
                    Operation = item.Operation,
                    Status = item.Status,
                    Message = item.Message,
                    DetailsJson = item.DetailsJson,
                    CreatedAt = item.CreatedAt,
                })
                .ToListAsync(ct)
            : [];

        var yandexLogs = provider is null or "" or "yandex" or "yandex-calendar"
            ? await _db.YandexCalendarSyncLogs
                .AsNoTracking()
                .Select(item => new CalendarSyncLogRaw
                {
                    Provider = "yandex-calendar",
                    ProviderLabel = "Яндекс Календарь",
                    Id = item.Id,
                    ConnectionId = item.ConnectionId,
                    UserId = item.Connection == null ? null : item.Connection.UserId,
                    UserName = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.FullName,
                    UserEmail = item.Connection == null || item.Connection.User == null ? string.Empty : item.Connection.User.Email,
                    TaskJournalItemId = item.TaskJournalItemId,
                    TaskTitle = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.Title,
                    TaskObjectName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.ObjectName,
                    TaskAssigneeName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.AssigneeName,
                    CalendarName = item.CalendarSyncCalendar == null ? string.Empty : item.CalendarSyncCalendar.Name,
                    CalendarOwnerName = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.FullName,
                    CalendarOwnerEmail = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.Email,
                    Direction = item.Direction,
                    Operation = item.Operation,
                    Status = item.Status,
                    Message = item.Message,
                    DetailsJson = item.DetailsJson,
                    CreatedAt = item.CreatedAt,
                })
                .ToListAsync(ct)
            : [];

        var logs = googleLogs.Concat(yandexLogs).Select(MapCalendarSyncLog).AsEnumerable();
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

    private static CalendarSyncLogEntryDto MapCalendarSyncLog(CalendarSyncLogRaw item)
    {
        var details = ParseCalendarSyncDetails(item.DetailsJson);
        var taskTitle = FirstNonEmpty(item.TaskTitle, details.GetValueOrDefault("taskTitle"));
        var taskObjectName = FirstNonEmpty(item.TaskObjectName, details.GetValueOrDefault("taskObjectName"));
        var calendarName = FirstNonEmpty(item.CalendarName, details.GetValueOrDefault("calendarName"), ExtractQuotedValue(item.Message));
        var userName = FirstNonEmpty(
            item.UserName,
            item.CalendarOwnerName,
            details.GetValueOrDefault("userName"),
            details.GetValueOrDefault("calendarOwnerName"),
            details.GetValueOrDefault("taskAssigneeName"));
        var userEmail = FirstNonEmpty(
            item.UserEmail,
            item.CalendarOwnerEmail,
            details.GetValueOrDefault("userEmail"),
            details.GetValueOrDefault("calendarOwnerEmail"));

        var targetType = ResolveTargetType(item.Operation, taskTitle, calendarName);
        var targetLabel = targetType switch
        {
            "task" => string.IsNullOrWhiteSpace(taskTitle) ? $"Задача #{item.TaskJournalItemId}" : $"Задача: {taskTitle}",
            "calendar" => string.IsNullOrWhiteSpace(calendarName) ? "Календарь" : $"Календарь: {calendarName}",
            "connection" => string.IsNullOrWhiteSpace(userName) ? "Подключение" : $"Подключение: {userName}",
            _ => "Системное действие",
        };

        return new CalendarSyncLogEntryDto
        {
            Provider = item.Provider,
            ProviderLabel = item.ProviderLabel,
            Id = item.Id,
            ConnectionId = item.ConnectionId,
            UserId = item.UserId,
            UserName = userName,
            UserEmail = userEmail,
            TaskJournalItemId = item.TaskJournalItemId,
            TaskTitle = taskTitle,
            TaskObjectName = taskObjectName,
            CalendarName = calendarName,
            TargetType = targetType,
            TargetLabel = targetLabel,
            Direction = item.Direction,
            Operation = item.Operation,
            Status = item.Status,
            Message = item.Message,
            Details = details.GetValueOrDefault("detail") ?? string.Empty,
            CreatedAt = item.CreatedAt,
        };
    }

    private static string ResolveTargetType(string operation, string taskTitle, string calendarName)
    {
        if (!string.IsNullOrWhiteSpace(taskTitle) || operation.Contains("event", StringComparison.OrdinalIgnoreCase) || operation.Contains("task", StringComparison.OrdinalIgnoreCase))
        {
            return "task";
        }

        if (!string.IsNullOrWhiteSpace(calendarName) || operation.Contains("calendar", StringComparison.OrdinalIgnoreCase))
        {
            return "calendar";
        }

        if (operation.Contains("oauth", StringComparison.OrdinalIgnoreCase) || operation.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            return "connection";
        }

        return "system";
    }

    private static Dictionary<string, string> ParseCalendarSyncDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return document.RootElement
                .EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ExtractQuotedValue(string value)
    {
        var start = value.IndexOf('«');
        var end = value.IndexOf('»', start + 1);
        return start >= 0 && end > start
            ? value.Substring(start + 1, end - start - 1)
            : string.Empty;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

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

    private sealed class CalendarSyncLogRaw
    {
        public string Provider { get; set; } = string.Empty;
        public string ProviderLabel { get; set; } = string.Empty;
        public int Id { get; set; }
        public int? ConnectionId { get; set; }
        public int? UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public int? TaskJournalItemId { get; set; }
        public string TaskTitle { get; set; } = string.Empty;
        public string TaskObjectName { get; set; } = string.Empty;
        public string TaskAssigneeName { get; set; } = string.Empty;
        public string CalendarName { get; set; } = string.Empty;
        public string CalendarOwnerName { get; set; } = string.Empty;
        public string CalendarOwnerEmail { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? DetailsJson { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}

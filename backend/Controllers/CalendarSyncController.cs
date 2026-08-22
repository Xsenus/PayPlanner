using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
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
    private readonly GoogleCalendarBackgroundSyncService? _googleBackgroundSync;
    private readonly YandexCalendarBackgroundSyncService? _yandexBackgroundSync;

    public CalendarSyncController(
        IOptionsSnapshot<AppFeatureFlagsOptions> featureFlags,
        PaymentContext db,
        GoogleCalendarBackgroundSyncService? googleBackgroundSync = null,
        YandexCalendarBackgroundSyncService? yandexBackgroundSync = null)
    {
        _featureFlags = featureFlags;
        _db = db;
        _googleBackgroundSync = googleBackgroundSync;
        _yandexBackgroundSync = yandexBackgroundSync;
    }

    private async Task<ForbidResult?> RequireAdminAccessAsync(CancellationToken ct)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (!await _db.IsUserAdminOrLegacyFallbackAsync(User, ct))
        {
            return Forbid();
        }

        return null;
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
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        var previousSettings = await GetSettingsAsync(ct);
        var googleCalendarSyncEnabled = request.GoogleCalendarSyncEnabled
            ?? (request.ExternalCalendarSyncEnabled.HasValue ? request.ExternalCalendarSyncEnabled.Value : previousSettings.GoogleCalendarSyncEnabled);
        var yandexCalendarSyncEnabled = request.YandexCalendarSyncEnabled
            ?? (request.ExternalCalendarSyncEnabled.HasValue ? request.ExternalCalendarSyncEnabled.Value : previousSettings.YandexCalendarSyncEnabled);
        var normalized = CalendarSyncProviderRegistry.NormalizeSettings(new CalendarSyncSettingsDto
        {
            GoogleCalendarSyncEnabled = googleCalendarSyncEnabled,
            YandexCalendarSyncEnabled = yandexCalendarSyncEnabled,
            GoogleSyncIntervalMinutes = request.GoogleSyncIntervalMinutes ?? previousSettings.GoogleSyncIntervalMinutes,
            YandexSyncIntervalMinutes = request.YandexSyncIntervalMinutes ?? previousSettings.YandexSyncIntervalMinutes,
            ManualFullSyncCooldownMinutes = request.ManualFullSyncCooldownMinutes ?? previousSettings.ManualFullSyncCooldownMinutes,
            CompletedTaskCalendarAction = request.CompletedTaskCalendarAction ?? previousSettings.CompletedTaskCalendarAction,
            CompletedTaskGoogleColorId = request.CompletedTaskGoogleColorId ?? previousSettings.CompletedTaskGoogleColorId,
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
        _googleBackgroundSync?.NotifySettingsChanged();
        _yandexBackgroundSync?.NotifySettingsChanged();

        return Ok(normalized);
    }

    [HttpGet("logs")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<CalendarSyncLogPageDto>> GetLogs(
        [FromQuery] string? provider,
        [FromQuery] string? status,
        [FromQuery] string? operation,
        [FromQuery] int? userId,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (await RequireAdminAccessAsync(ct) is { } forbidden)
        {
            return forbidden;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        var normalizedOperation = string.IsNullOrWhiteSpace(operation) ? null : operation.Trim().ToLowerInvariant();
        var normalizedCreatedFrom = createdFrom.HasValue ? NormalizeFilterDate(createdFrom.Value) : (DateTime?)null;
        var normalizedCreatedTo = createdTo.HasValue ? NormalizeFilterDate(createdTo.Value) : (DateTime?)null;
        var googleQuery = provider is null or "" or "google" or "google-calendar"
            ? ApplyCalendarLogFilters(CreateGoogleLogQuery(), normalizedStatus, normalizedOperation, userId, normalizedCreatedFrom, normalizedCreatedTo)
            : null;
        var yandexQuery = provider is null or "" or "yandex" or "yandex-calendar"
            ? ApplyCalendarLogFilters(CreateYandexLogQuery(), normalizedStatus, normalizedOperation, userId, normalizedCreatedFrom, normalizedCreatedTo)
            : null;

        var trimmedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (trimmedSearch is not null)
        {
            var normalizedSearch = trimmedSearch.ToLowerInvariant();
            var matchingTaskIds = await FindMatchingCalendarTaskIdsAsync(trimmedSearch, normalizedSearch, ct);
            googleQuery = ApplyResolvedCalendarLogSearch(
                googleQuery,
                "Google Calendar",
                trimmedSearch,
                normalizedSearch,
                matchingTaskIds);
            yandexQuery = ApplyResolvedCalendarLogSearch(
                yandexQuery,
                "Яндекс Календарь",
                trimmedSearch,
                normalizedSearch,
                matchingTaskIds);
        }

        var rawLogs = new List<CalendarSyncLogRaw>();
        var total = 0;
        var candidateCount = (int)Math.Min(int.MaxValue, (long)page * pageSize);
        if (googleQuery is not null)
        {
            total = AddToTotal(total, await googleQuery.CountAsync(ct));
            rawLogs.AddRange(await LoadRecentLogsAsync(googleQuery, candidateCount, ct));
        }

        if (yandexQuery is not null)
        {
            total = AddToTotal(total, await yandexQuery.CountAsync(ct));
            rawLogs.AddRange(await LoadRecentLogsAsync(yandexQuery, candidateCount, ct));
        }

        var logs = rawLogs.Select(MapCalendarSyncLog).ToList();

        var pageOffset = (int)Math.Min(int.MaxValue, ((long)page - 1) * pageSize);
        var orderedLogs = logs
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Provider, StringComparer.Ordinal)
            .ThenByDescending(item => item.Id)
            .Skip(pageOffset)
            .Take(pageSize)
            .ToList();
        return Ok(new CalendarSyncLogPageDto
        {
            Items = orderedLogs,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    private IQueryable<CalendarSyncLogRaw> CreateGoogleLogQuery()
        => _db.GoogleCalendarSyncLogs
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
                TaskAssigneeUserId = item.TaskJournalItem == null ? null : item.TaskJournalItem.AssigneeUserId,
                TaskTitle = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.Title,
                TaskObjectName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.ObjectName,
                TaskAssigneeName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.AssigneeName,
                CalendarName = item.CalendarSyncCalendar == null ? string.Empty : item.CalendarSyncCalendar.Name,
                CalendarOwnerUserId = item.CalendarSyncCalendar == null ? null : item.CalendarSyncCalendar.OwnerUserId,
                CalendarOwnerName = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.FullName,
                CalendarOwnerEmail = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.Email,
                Direction = item.Direction,
                Operation = item.Operation,
                Status = item.Status,
                Message = item.Message,
                DetailsJson = item.DetailsJson,
                CreatedAt = item.CreatedAt,
            });

    private IQueryable<CalendarSyncLogRaw> CreateYandexLogQuery()
        => _db.YandexCalendarSyncLogs
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
                TaskAssigneeUserId = item.TaskJournalItem == null ? null : item.TaskJournalItem.AssigneeUserId,
                TaskTitle = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.Title,
                TaskObjectName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.ObjectName,
                TaskAssigneeName = item.TaskJournalItem == null ? string.Empty : item.TaskJournalItem.AssigneeName,
                CalendarName = item.CalendarSyncCalendar == null ? string.Empty : item.CalendarSyncCalendar.Name,
                CalendarOwnerUserId = item.CalendarSyncCalendar == null ? null : item.CalendarSyncCalendar.OwnerUserId,
                CalendarOwnerName = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.FullName,
                CalendarOwnerEmail = item.CalendarSyncCalendar == null || item.CalendarSyncCalendar.OwnerUser == null ? string.Empty : item.CalendarSyncCalendar.OwnerUser.Email,
                Direction = item.Direction,
                Operation = item.Operation,
                Status = item.Status,
                Message = item.Message,
                DetailsJson = item.DetailsJson,
                CreatedAt = item.CreatedAt,
            });

    private static IQueryable<CalendarSyncLogRaw> ApplyCalendarLogFilters(
        IQueryable<CalendarSyncLogRaw> query,
        string? status,
        string? operation,
        int? userId,
        DateTime? createdFrom,
        DateTime? createdTo)
    {
        if (status is not null)
        {
            query = query.Where(item => item.Status.ToLower() == status);
        }

        if (operation is not null)
        {
            query = query.Where(item => item.Operation.ToLower().Contains(operation));
        }

        if (userId.HasValue)
        {
            query = query.Where(item => item.UserId == userId.Value || item.CalendarOwnerUserId == userId.Value || item.TaskAssigneeUserId == userId.Value);
        }

        if (createdFrom.HasValue)
        {
            query = query.Where(item => item.CreatedAt >= createdFrom.Value);
        }

        if (createdTo.HasValue)
        {
            query = query.Where(item => item.CreatedAt <= createdTo.Value);
        }

        return query;
    }

    private static IQueryable<CalendarSyncLogRaw> ApplyCalendarLogSearch(
        IQueryable<CalendarSyncLogRaw> query,
        string search,
        string normalizedSearch)
        => query.Where(item =>
            item.ProviderLabel.Contains(search) || item.ProviderLabel.ToLower().Contains(normalizedSearch) ||
            item.UserName.Contains(search) || item.UserName.ToLower().Contains(normalizedSearch) ||
            item.UserEmail.Contains(search) || item.UserEmail.ToLower().Contains(normalizedSearch) ||
            item.CalendarName.Contains(search) || item.CalendarName.ToLower().Contains(normalizedSearch) ||
            item.CalendarOwnerName.Contains(search) || item.CalendarOwnerName.ToLower().Contains(normalizedSearch) ||
            item.TaskTitle.Contains(search) || item.TaskTitle.ToLower().Contains(normalizedSearch) ||
            item.TaskObjectName.Contains(search) || item.TaskObjectName.ToLower().Contains(normalizedSearch) ||
            item.Message.Contains(search) || item.Message.ToLower().Contains(normalizedSearch) ||
            (item.DetailsJson ?? string.Empty).Contains(search) ||
            (item.DetailsJson ?? string.Empty).ToLower().Contains(normalizedSearch));

    private static IQueryable<CalendarSyncLogRaw>? ApplyResolvedCalendarLogSearch(
        IQueryable<CalendarSyncLogRaw>? query,
        string providerLabel,
        string search,
        string normalizedSearch,
        int[] matchingTaskIds)
    {
        if (query is null || providerLabel.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        if (matchingTaskIds.Length > 0)
        {
            return query.Where(item =>
                item.TaskJournalItemId.HasValue && matchingTaskIds.Contains(item.TaskJournalItemId.Value));
        }

        return ApplyCalendarLogSearch(query, search, normalizedSearch);
    }

    private async Task<int[]> FindMatchingCalendarTaskIdsAsync(
        string search,
        string normalizedSearch,
        CancellationToken ct)
    {
        const int maxResolvedTasks = 5_000;
        var taskIds = await _db.TaskJournalItems
            .AsNoTracking()
            .Where(item =>
                item.Title.Contains(search) || item.Title.ToLower().Contains(normalizedSearch) ||
                item.ObjectName.Contains(search) || item.ObjectName.ToLower().Contains(normalizedSearch) ||
                item.AssigneeName.Contains(search) || item.AssigneeName.ToLower().Contains(normalizedSearch))
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .Take(maxResolvedTasks + 1)
            .ToArrayAsync(ct);

        // При слишком широком совпадении сохраняем общий поиск по журналу, чтобы ограниченная
        // выборка идентификаторов не дала пользователю неполный результат.
        return taskIds.Length <= maxResolvedTasks ? taskIds : [];
    }

    private static Task<List<CalendarSyncLogRaw>> LoadRecentLogsAsync(
        IQueryable<CalendarSyncLogRaw> query,
        int candidateCount,
        CancellationToken ct)
        => query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(candidateCount)
            .ToListAsync(ct);

    private static int AddToTotal(int current, int addition)
        => (int)Math.Min(int.MaxValue, (long)current + addition);

    private static CalendarSyncLogEntryDto MapCalendarSyncLog(CalendarSyncLogRaw item)
    {
        var details = ParseCalendarSyncDetails(item.DetailsJson);
        var taskTitle = FirstNonEmpty(item.TaskTitle, details.GetValueOrDefault("taskTitle"));
        var taskObjectName = FirstNonEmpty(item.TaskObjectName, details.GetValueOrDefault("taskObjectName"));
        var taskAssigneeName = FirstNonEmpty(item.TaskAssigneeName, details.GetValueOrDefault("taskAssigneeName"));
        var calendarName = FirstNonEmpty(item.CalendarName, details.GetValueOrDefault("calendarName"), ExtractQuotedValue(item.Message));
        var calendarOwnerName = FirstNonEmpty(
            item.CalendarOwnerName,
            details.GetValueOrDefault("calendarOwnerName"),
            InferCalendarOwnerName(calendarName));
        var calendarOwnerEmail = FirstNonEmpty(item.CalendarOwnerEmail, details.GetValueOrDefault("calendarOwnerEmail"));
        var userName = FirstNonEmpty(
            item.UserName,
            details.GetValueOrDefault("userName"),
            calendarOwnerName,
            taskAssigneeName);
        var userEmail = FirstNonEmpty(
            item.UserEmail,
            details.GetValueOrDefault("userEmail"),
            calendarOwnerEmail);

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
            CalendarOwnerUserId = item.CalendarOwnerUserId,
            TaskAssigneeUserId = item.TaskAssigneeUserId,
            TaskTitle = taskTitle,
            TaskObjectName = taskObjectName,
            TaskAssigneeName = taskAssigneeName,
            CalendarName = calendarName,
            CalendarOwnerName = calendarOwnerName,
            CalendarOwnerEmail = calendarOwnerEmail,
            TargetType = targetType,
            TargetLabel = targetLabel,
            Direction = item.Direction,
            DirectionLabel = BuildDirectionLabel(item.Direction, item.ProviderLabel),
            Operation = item.Operation,
            OperationLabel = BuildOperationLabel(item.Operation),
            Status = item.Status,
            StatusLabel = BuildStatusLabel(item.Status),
            Message = SanitizeCalendarLogText(item.Message),
            Details = SanitizeCalendarLogDetails(details.GetValueOrDefault("detail")),
            ChangeSummary = BuildChangeSummary(details),
            ActorLabel = BuildActorLabel(userName, userEmail),
            CalendarRoleLabel = BuildCalendarRoleLabel(calendarOwnerName, calendarOwnerEmail, taskAssigneeName),
            CreatedAt = item.CreatedAt,
        };
    }

    private static DateTime NormalizeFilterDate(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();

    private static string BuildChangeSummary(Dictionary<string, string> details)
    {
        var changeSummary = details.GetValueOrDefault("changes");
        if (!string.IsNullOrWhiteSpace(changeSummary))
        {
            return IsTechnicalCalendarLogText(changeSummary) ? string.Empty : SanitizeCalendarLogText(changeSummary);
        }

        var detail = details.GetValueOrDefault("detail");
        return detail?.Contains(":", StringComparison.Ordinal) == true
            ? SanitizeCalendarLogDetails(detail)
            : string.Empty;
    }

    private static string SanitizeCalendarLogDetails(string? value)
        => IsTechnicalCalendarLogText(value) ? string.Empty : SanitizeCalendarLogText(value);

    private static string SanitizeCalendarLogText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (IsTechnicalCalendarLogText(trimmed))
        {
            return "Сохранённые данные подключения недоступны. Подключите календарь заново.";
        }

        const int maxUserVisibleLength = 2_000;
        return trimmed.Length <= maxUserVisibleLength
            ? trimmed
            : $"{trimmed[..maxUserVisibleLength]}…";
    }

    private static bool IsTechnicalCalendarLogText(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Contains("key ring", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("dataprotectionwarning", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("CryptographicException", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("IDataProtector", StringComparison.OrdinalIgnoreCase));

    private static string BuildActorLabel(string userName, string userEmail)
    {
        var name = FirstNonEmpty(userName, "Система");
        return string.IsNullOrWhiteSpace(userEmail) ? name : $"{name} ({userEmail})";
    }

    private static string BuildCalendarRoleLabel(string calendarOwnerName, string calendarOwnerEmail, string taskAssigneeName)
    {
        if (string.IsNullOrWhiteSpace(calendarOwnerName))
        {
            return string.Empty;
        }

        var owner = string.IsNullOrWhiteSpace(calendarOwnerEmail)
            ? calendarOwnerName
            : $"{calendarOwnerName} ({calendarOwnerEmail})";
        return string.Equals(calendarOwnerName, taskAssigneeName, StringComparison.OrdinalIgnoreCase)
            ? $"Календарь исполнителя: {owner}"
            : $"Календарь сотрудника: {owner}";
    }

    private static string BuildDirectionLabel(string direction, string providerLabel)
        => direction switch
        {
            "crm-to-google" or "crm-to-yandex" => $"CRM → {providerLabel}",
            "google-to-crm" or "yandex-to-crm" => $"{providerLabel} → CRM",
            "system" => "Системное действие",
            _ => direction,
        };

    private static string BuildStatusLabel(string status)
        => status switch
        {
            "success" => "Успешно",
            "error" => "Ошибка",
            "warning" => "Внимание",
            "skipped" => "Пропущено",
            _ => string.IsNullOrWhiteSpace(status) ? "Неизвестно" : status,
        };

    private static string BuildOperationLabel(string operation)
        => operation switch
        {
            "create-event" => "Создание события",
            "update-event" => "Обновление события",
            "delete-event" => "Удаление события",
            "update-task" => "Обновление задачи в CRM",
            "detach" => "Отвязка удаленного события",
            "skip" => "Изменение пропущено по правилу конфликтов",
            "upsert" => "Создание или обновление события",
            "import" => "Чтение изменений календаря",
            "create-calendar" => "Создание календаря сотрудника",
            "delete-calendar" => "Удаление календаря сотрудника",
            "delete-calendar-missing" => "Календарь уже отсутствует",
            "cleanup-user-calendars" => "Очистка календарей сотрудника",
            "settings-updated" => "Настройки изменены",
            "disable-sync" => "Синхронизация отключена",
            "sync-now" => "Ручной запуск синхронизации",
            "oauth-connected" => "Календарь подключен",
            _ => string.IsNullOrWhiteSpace(operation) ? "Действие не указано" : operation,
        };

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

    private static string InferCalendarOwnerName(string calendarName)
    {
        const string calendarPrefix = "PayPlanner - ";
        if (string.IsNullOrWhiteSpace(calendarName))
        {
            return string.Empty;
        }

        var normalized = calendarName.Trim().Trim('«', '»', '"');
        return normalized.StartsWith(calendarPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[calendarPrefix.Length..].Trim()
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
        public int? CalendarOwnerUserId { get; set; }
        public int? TaskAssigneeUserId { get; set; }
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

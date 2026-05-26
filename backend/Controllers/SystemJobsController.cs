using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/system/jobs")]
[Authorize(Policy = "Admin")]
public sealed class SystemJobsController : ControllerBase
{
    private const string DatabaseBackupEnabledKey = "database-backup.enabled";
    private const string DatabaseBackupScheduleKey = "database-backup.schedule-local";
    private const string CaseControlEnabledKey = "tasks.automation.caseControl.enabled";
    private const string CaseControlInactiveDaysKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseControlLimitKey = "tasks.automation.caseControl.limit";
    private const string UserActivityRetentionDaysKey = "user-activity.retention-days";

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;

    public SystemJobsController(
        PaymentContext db,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags)
    {
        _db = db;
        _configuration = configuration;
        _featureFlags = featureFlags;
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<SystemJobDiagnosticsDto>> GetDiagnostics(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == DatabaseBackupEnabledKey ||
                item.Key == DatabaseBackupScheduleKey ||
                item.Key == CaseControlEnabledKey ||
                item.Key == CaseControlInactiveDaysKey ||
                item.Key == CaseControlLimitKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.EnabledSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == UserActivityRetentionDaysKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var items = new List<SystemJobDiagnosticsItemDto>
        {
            await BuildDatabaseBackupJobAsync(settings, ct),
            await BuildTaskReminderJobAsync(now, ct),
            await BuildCaseControlJobAsync(settings, now, ct),
            await BuildCourtSyncJobAsync(settings, ct),
            await BuildMoneyBalanceReconciliationJobAsync(ct),
            await BuildUserActivityRetentionJobAsync(settings, ct),
        };

        return Ok(new SystemJobDiagnosticsDto
        {
            GeneratedAt = now,
            Items = items,
        });
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildDatabaseBackupJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var enabled = ParseBool(settings.GetValueOrDefault(DatabaseBackupEnabledKey), _configuration.GetValue("DatabaseBackup:Enabled", true));
        var schedule = settings.GetValueOrDefault(DatabaseBackupScheduleKey);
        if (string.IsNullOrWhiteSpace(schedule))
        {
            schedule = _configuration["DatabaseBackup:ScheduleLocal"];
        }

        if (string.IsNullOrWhiteSpace(schedule))
        {
            schedule = "03:00,15:00";
        }

        var lastBackup = await _db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item => item.ObjectType == "database-backup")
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.Description })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "database-backup",
            Name = "Резервное копирование базы данных",
            Description = "Проверяет расписание и создает автоматические резервные копии базы.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Расписание: {schedule}",
            LastActivityAt = lastBackup?.CreatedAt,
            LastActivityDescription = lastBackup?.Description,
            NextRunHint = enabled ? "Следующая проверка расписания выполняется фоновым сервисом." : "Автоматические резервные копии отключены.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildTaskReminderJobAsync(DateTime now, CancellationToken ct)
    {
        var enabled = _configuration.GetValue("Notifications:TaskReminders:Enabled", true);
        var pollSeconds = Math.Clamp(_configuration.GetValue("Notifications:TaskReminders:PollSeconds", 60), 15, 3600);
        var pendingCount = enabled
            ? await _db.TaskJournalItems
                .AsNoTracking()
                .CountAsync(task =>
                    task.AssigneeUserId.HasValue &&
                    task.Status != "done" &&
                    task.Status != "cancelled" &&
                    task.DueAt <= now.AddDays(7) &&
                    task.RemindersJson != "[]" &&
                    task.ChannelsJson != "[]",
                    ct)
            : 0;

        var lastReminder = await _db.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.EventKey.Contains(":reminder:"))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.Subject })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "task-reminders",
            Name = "Напоминания по задачам",
            Description = "Отправляет напоминания исполнителям по срокам задач и просрочкам.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Проверка каждые {pollSeconds} сек.",
            PendingCount = pendingCount,
            LastActivityAt = lastReminder?.CreatedAt,
            LastActivityDescription = lastReminder?.Subject,
            NextRunHint = enabled ? "Сервис проверяет задачи с напоминаниями на ближайшие 7 дней." : "Отправка напоминаний выключена в конфигурации.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildCaseControlJobAsync(
        IReadOnlyDictionary<string, string> settings,
        DateTime now,
        CancellationToken ct)
    {
        var enabled = ParseBool(settings.GetValueOrDefault(CaseControlEnabledKey), true);
        var inactivityDays = Math.Clamp(ParseInt(settings.GetValueOrDefault(CaseControlInactiveDaysKey), 14), 1, 365);
        var limit = Math.Clamp(ParseInt(settings.GetValueOrDefault(CaseControlLimitKey), 25), 1, 100);
        var cutoff = now.AddDays(-inactivityDays);
        var pendingCount = enabled
            ? await _db.ClientCases
                .AsNoTracking()
                .CountAsync(clientCase =>
                    clientCase.Status == ClientCaseStatus.Open &&
                    clientCase.InactivityRemindersEnabled &&
                    (clientCase.LastViewedAt ?? clientCase.CreatedAt) <= cutoff &&
                    (clientCase.ActivityConfirmedAt ?? clientCase.CreatedAt) <= cutoff,
                    ct)
            : 0;

        var lastAutomation = await _db.ObjectAuditEvents
            .AsNoTracking()
            .Where(item =>
                item.ObjectType == "client-case" &&
                item.Action == "Automation" &&
                item.MetadataJson != null &&
                item.MetadataJson.Contains("caseInactivityControlTaskCreated"))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, item.Description })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "case-inactivity-control",
            Name = "Автоконтроль неактивных дел",
            Description = "Создает контрольные задачи по делам, которые долго остаются без активности.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = "Проверка каждые 5 минут",
            PendingCount = pendingCount,
            LastProcessedCount = limit,
            LastActivityAt = lastAutomation?.CreatedAt,
            LastActivityDescription = lastAutomation?.Description,
            NextRunHint = enabled ? $"Порог неактивности: {inactivityDays} дн., лимит за запуск: {limit}." : "Автоматическое создание контрольных задач выключено.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildCourtSyncJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var featureEnabled = _featureFlags.CurrentValue.Legal.ExternalCourtSync;
        var settingEnabled = ParseBool(
            settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.EnabledSettingKey),
            _configuration.GetValue("LegalIntegrations:CourtSyncScheduler:Enabled", false));
        var intervalMinutes = ClientCaseCourtSyncSchedulerService.NormalizeIntervalMinutes(ParseInt(
            settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey),
            _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:IntervalMinutes") ?? ClientCaseCourtSyncSchedulerService.DefaultIntervalMinutes));
        var batchSize = ClientCaseCourtSyncSchedulerService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey),
            _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:BatchSize") ?? ClientCaseCourtSyncSchedulerService.DefaultBatchSize));
        var enabled = featureEnabled && settingEnabled;
        var pendingCount = enabled
            ? await _db.ClientCases
                .AsNoTracking()
                .CountAsync(item => item.Status == ClientCaseStatus.Open && item.CourtUrl != string.Empty, ct)
            : 0;

        var lastCourtSync = await _db.ClientCases
            .AsNoTracking()
            .Where(item => item.EventHistory.Contains("судебн"))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new { item.CreatedAt, Description = item.CaseNumber })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "court-sync",
            Name = "Синхронизация судебных источников",
            Description = "Проверяет дела с привязкой к судебным источникам и переносит найденные изменения в карточку дела.",
            Enabled = enabled,
            Status = enabled ? "active" : settingEnabled ? "blocked" : "disabled",
            StatusText = enabled ? "Включено" : settingEnabled ? "Ожидает feature flag" : "Выключено",
            IntervalText = $"Каждые {intervalMinutes} мин.",
            PendingCount = pendingCount,
            LastProcessedCount = batchSize,
            LastActivityAt = lastCourtSync?.CreatedAt,
            LastActivityDescription = lastCourtSync is null ? null : $"Последнее дело с судебной активностью: {lastCourtSync.Description}",
            NextRunHint = enabled ? $"За один запуск обрабатывается до {batchSize} дел." : "Фоновая судебная синхронизация не запускается автоматически.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildMoneyBalanceReconciliationJobAsync(CancellationToken ct)
    {
        var lastResult = await _db.MoneyBalanceReconciliationResults
            .AsNoTracking()
            .OrderByDescending(item => item.CheckedAt)
            .Select(item => new { item.CheckedAt, item.Date, item.IssueCount })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "money-balance-reconciliation",
            Name = "Сверка денежных остатков",
            Description = "Раз в час проверяет, выполнена ли ежедневная сверка остатков за предыдущий день.",
            Enabled = true,
            Status = "active",
            StatusText = "Включено",
            IntervalText = "Проверка каждый час",
            LastProcessedCount = lastResult?.IssueCount,
            LastActivityAt = lastResult?.CheckedAt,
            LastActivityDescription = lastResult is null
                ? null
                : $"Последняя сверка за {lastResult.Date:dd.MM.yyyy}, расхождений: {lastResult.IssueCount}.",
            NextRunHint = "Сервис сверяет предыдущий календарный день один раз в сутки.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildUserActivityRetentionJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var retentionDays = ParseNullableInt(settings.GetValueOrDefault(UserActivityRetentionDaysKey));
        var enabled = retentionDays.HasValue;
        var oldestLogAt = await _db.UserActivityLogs
            .AsNoTracking()
            .OrderBy(item => item.CreatedAt)
            .Select(item => (DateTime?)item.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "user-activity-retention",
            Name = "Очистка журнала действий",
            Description = "Удаляет устаревшие записи пользовательской активности по настроенному сроку хранения.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Без ограничения хранения",
            IntervalText = "Проверка каждые 12 часов",
            LastActivityAt = oldestLogAt,
            LastActivityDescription = oldestLogAt is null ? null : $"Самая старая запись журнала: {oldestLogAt:dd.MM.yyyy HH:mm}.",
            NextRunHint = enabled ? $"Хранение журнала: {retentionDays} дн." : "Автоочистка не удаляет записи без заданного срока хранения.",
        };
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
}

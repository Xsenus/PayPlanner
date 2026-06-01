using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.UserActivity;

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
    private static readonly HashSet<string> CaseControlActiveTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    };

    private readonly PaymentContext _db;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly DatabaseBackupService? _databaseBackupService;
    private readonly TaskJournalReminderNotificationService? _taskReminderRunner;
    private readonly NotificationRetrySchedulerService? _notificationRetryRunner;
    private readonly NotificationDeliveryRetentionService? _notificationDeliveryRetentionRunner;
    private readonly CalendarSyncSchedulerService? _calendarSyncRunner;
    private readonly TaskJournalAutomationSchedulerService? _caseControlRunner;
    private readonly ClientCaseCourtSyncSchedulerService? _courtSyncRunner;
    private readonly MoneyBalanceReconciliationSchedulerService? _moneyBalanceRunner;
    private readonly UserActivityRetentionService? _userActivityRetentionRunner;
    private readonly YandexDiskCrmSyncSchedulerService? _yandexDiskCrmSyncRunner;

    public SystemJobsController(
        PaymentContext db,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        ISystemJobRunStateService? jobRunState = null,
        DatabaseBackupService? databaseBackupService = null,
        TaskJournalReminderNotificationService? taskReminderRunner = null,
        NotificationRetrySchedulerService? notificationRetryRunner = null,
        NotificationDeliveryRetentionService? notificationDeliveryRetentionRunner = null,
        CalendarSyncSchedulerService? calendarSyncRunner = null,
        TaskJournalAutomationSchedulerService? caseControlRunner = null,
        ClientCaseCourtSyncSchedulerService? courtSyncRunner = null,
        MoneyBalanceReconciliationSchedulerService? moneyBalanceRunner = null,
        UserActivityRetentionService? userActivityRetentionRunner = null,
        YandexDiskCrmSyncSchedulerService? yandexDiskCrmSyncRunner = null)
    {
        _db = db;
        _configuration = configuration;
        _featureFlags = featureFlags;
        _jobRunState = jobRunState;
        _databaseBackupService = databaseBackupService;
        _taskReminderRunner = taskReminderRunner;
        _notificationRetryRunner = notificationRetryRunner;
        _notificationDeliveryRetentionRunner = notificationDeliveryRetentionRunner;
        _calendarSyncRunner = calendarSyncRunner;
        _caseControlRunner = caseControlRunner;
        _courtSyncRunner = courtSyncRunner;
        _moneyBalanceRunner = moneyBalanceRunner;
        _userActivityRetentionRunner = userActivityRetentionRunner;
        _yandexDiskCrmSyncRunner = yandexDiskCrmSyncRunner;
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
                item.Key == NotificationRetrySchedulerService.EnabledSettingKey ||
                item.Key == NotificationRetrySchedulerService.IntervalMinutesSettingKey ||
                item.Key == NotificationRetrySchedulerService.DelayMinutesSettingKey ||
                item.Key == NotificationRetrySchedulerService.BatchSizeSettingKey ||
                item.Key == NotificationRetrySchedulerService.MaxAttemptsSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.EnabledSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.DelayMinutesSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.MaxAttemptsSettingKey ||
                item.Key == NotificationDeliveryRetentionService.EnabledSettingKey ||
                item.Key == NotificationDeliveryRetentionService.RetentionDaysSettingKey ||
                item.Key == NotificationDeliveryRetentionService.IntervalHoursSettingKey ||
                item.Key == NotificationDeliveryRetentionService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.EnabledSettingKey ||
                item.Key == CalendarSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == CalendarSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.LookAheadDaysSettingKey ||
                item.Key == CaseControlEnabledKey ||
                item.Key == CaseControlInactiveDaysKey ||
                item.Key == CaseControlLimitKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.EnabledSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == UserActivityRetentionDaysKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        var rawItems = new List<SystemJobDiagnosticsItemDto>
        {
            await BuildDatabaseBackupJobAsync(settings, ct),
            await BuildTaskReminderJobAsync(now, ct),
            await BuildNotificationRetryJobAsync(settings, ct),
            await BuildYandexDiskCrmSyncJobAsync(settings, ct),
            await BuildNotificationDeliveryRetentionJobAsync(settings, ct),
            await BuildExternalCalendarSyncJobAsync(settings, ct),
            await BuildCaseControlJobAsync(settings, now, ct),
            await BuildCourtSyncJobAsync(settings, ct),
            await BuildMoneyBalanceReconciliationJobAsync(ct),
            await BuildUserActivityRetentionJobAsync(settings, ct),
        };
        var lastRuns = _jobRunState is null
            ? new Dictionary<string, SystemJobRunState>(StringComparer.OrdinalIgnoreCase)
            : await _jobRunState.GetLastRunsAsync(rawItems.Select(item => item.Key), ct);

        return Ok(new SystemJobDiagnosticsDto
        {
            GeneratedAt = now,
            Items = rawItems.Select(item => WithLastRun(item, lastRuns)).ToList(),
        });
    }

    [HttpPost("{jobKey}/run")]
    public async Task<ActionResult<SystemJobManualRunResultDto>> RunJob(string jobKey, CancellationToken ct)
    {
        var item = await BuildSingleJobAsync(jobKey, ct);
        if (item is null)
        {
            return NotFound(new { message = "Фоновый сервис не найден." });
        }

        if (!item.ManualRunEnabled)
        {
            return BadRequest(new { message = item.ManualRunHint ?? "Ручной запуск для этого сервиса сейчас недоступен." });
        }

        var startedAt = DateTime.UtcNow;
        int? processedCount = null;
        int? skippedCount = null;
        string summary;

        try
        {
            switch (item.Key)
            {
                case SystemJobKeys.DatabaseBackup:
                    EnsureRunner(_databaseBackupService);
                    await _databaseBackupService!.CreateManualBackupAsync(ct);
                    processedCount = 1;
                    summary = "Создана ручная резервная копия базы данных.";
                    await RecordManualSuccessAsync(item.Key, startedAt, processedCount, skippedCount, summary, ct);
                    break;

                case SystemJobKeys.TaskReminders:
                    EnsureRunner(_taskReminderRunner);
                    processedCount = await _taskReminderRunner!.RunOnceAsync(ct);
                    if (processedCount < 0)
                    {
                        throw new InvalidOperationException("Проверка напоминаний завершилась с ошибкой.");
                    }

                    summary = processedCount > 0
                        ? $"Отправлено напоминаний по задачам: {processedCount}."
                        : "Проверка напоминаний выполнена, новых отправок не было.";
                    break;

                case SystemJobKeys.NotificationRetry:
                    EnsureRunner(_notificationRetryRunner);
                    var notificationRetryResult = await _notificationRetryRunner!.RunOnceAsync(ct);
                    processedCount = notificationRetryResult.RetriedCount;
                    skippedCount = notificationRetryResult.SkippedCount;
                    summary = notificationRetryResult.Enabled
                        ? processedCount > 0
                            ? $"Повторно отправлены уведомления: {processedCount}, пропущено: {skippedCount}."
                            : "Проверка ошибок доставки выполнена, уведомлений для повтора не найдено."
                        : "Повторная отправка уведомлений выключена в настройках.";
                    break;

                case SystemJobKeys.YandexDiskCrmSync:
                    EnsureRunner(_yandexDiskCrmSyncRunner);
                    var yandexDiskCrmSyncResult = await _yandexDiskCrmSyncRunner!.RunOnceAsync(ct);
                    processedCount = yandexDiskCrmSyncResult.SyncedCount;
                    skippedCount = yandexDiskCrmSyncResult.SkippedCount;
                    summary = yandexDiskCrmSyncResult.Enabled
                        ? processedCount > 0
                            ? $"Повторно отправлено CRM-документов на Я.Диск: {processedCount}, пропущено: {skippedCount}."
                            : "Проверка очереди Я.Диска выполнена, документов для повторной отправки не найдено."
                        : "Фоновая синхронизация CRM с Я.Диском выключена в настройках.";
                    break;

                case SystemJobKeys.NotificationDeliveryRetention:
                    EnsureRunner(_notificationDeliveryRetentionRunner);
                    var retentionResult = await _notificationDeliveryRetentionRunner!.RunOnceAsync(ct);
                    processedCount = retentionResult.DeletedCount;
                    summary = retentionResult.Enabled
                        ? processedCount > 0
                            ? $"Удалено устаревших технических записей доставки уведомлений: {processedCount}."
                            : "Проверка срока хранения технических логов уведомлений выполнена, удаление не требовалось."
                        : "Очистка технических логов уведомлений выключена в настройках.";
                    break;

                case SystemJobKeys.ExternalCalendarSync:
                    EnsureRunner(_calendarSyncRunner);
                    var calendarSyncResult = await _calendarSyncRunner!.RunOnceAsync(ct);
                    processedCount = calendarSyncResult.ProcessedCount;
                    skippedCount = calendarSyncResult.SkippedCount;
                    summary = calendarSyncResult.Enabled
                        ? calendarSyncResult.ReadyToConnect
                            ? processedCount > 0
                                ? $"Проверено событий для внешних календарей: {processedCount}."
                                : "Проверка внешних календарей выполнена, событий для синхронизации не найдено."
                            : calendarSyncResult.Message ?? "Внешние календари еще не готовы к запуску."
                        : "Фоновая проверка внешних календарей выключена в настройках.";
                    break;

                case SystemJobKeys.CaseInactivityControl:
                    EnsureRunner(_caseControlRunner);
                    var caseControlResult = await _caseControlRunner!.RunOnceAsync(ct);
                    processedCount = caseControlResult.CreatedCount + caseControlResult.EscalatedCount;
                    skippedCount = caseControlResult.SkippedCount;
                    if (!string.IsNullOrWhiteSpace(caseControlResult.Error))
                    {
                        throw new InvalidOperationException(caseControlResult.Error);
                    }

                    summary = caseControlResult.Enabled
                        ? processedCount > 0
                            ? $"Автоконтроль неактивных дел обработал записей: {processedCount}. Новых задач: {caseControlResult.CreatedCount}, повторных эскалаций: {caseControlResult.EscalatedCount}."
                            : "Проверка неактивных дел выполнена, новые контрольные задачи не требовались."
                        : "Автоконтроль неактивных дел выключен в настройках.";
                    break;

                case SystemJobKeys.CourtSync:
                    EnsureRunner(_courtSyncRunner);
                    processedCount = await _courtSyncRunner!.RunOnceAsync(ct);
                    summary = processedCount > 0
                        ? $"Проверено дел по судебным источникам: {processedCount}."
                        : "Проверка судебной синхронизации выполнена, подходящих дел не найдено.";
                    await RecordManualSuccessAsync(item.Key, startedAt, processedCount, skippedCount, summary, ct);
                    break;

                case SystemJobKeys.MoneyBalanceReconciliation:
                    EnsureRunner(_moneyBalanceRunner);
                    var targetDate = DateTime.UtcNow.Date.AddDays(-1);
                    var moneyResult = await _moneyBalanceRunner!.RunForDateAsync(targetDate, ct);
                    processedCount = (moneyResult?.Created ?? 0) + (moneyResult?.Updated ?? 0);
                    summary = moneyResult is null
                        ? $"Сверка денежных остатков за {targetDate:dd.MM.yyyy} не вернула результат."
                        : $"Сверка за {targetDate:dd.MM.yyyy}: создано {moneyResult.Created}, обновлено {moneyResult.Updated}, расхождений {moneyResult.IssueCount}.";
                    await RecordManualSuccessAsync(item.Key, startedAt, processedCount, skippedCount, summary, ct);
                    break;

                case SystemJobKeys.UserActivityRetention:
                    EnsureRunner(_userActivityRetentionRunner);
                    processedCount = await _userActivityRetentionRunner!.RunOnceAsync(ct);
                    summary = processedCount > 0
                        ? $"Удалено устаревших записей журнала действий: {processedCount}."
                        : "Проверка срока хранения журнала действий выполнена, удаление не требовалось.";
                    break;

                default:
                    return NotFound(new { message = "Фоновый сервис не найден." });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordManualFailureAsync(item.Key, startedAt, ex, ct);
            return Problem(
                detail: ex.Message,
                title: "Не удалось выполнить ручной запуск фонового сервиса.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var finishedAt = DateTime.UtcNow;
        var diagnosticsResult = await GetDiagnostics(ct);
        var diagnosticsOk = diagnosticsResult.Result as OkObjectResult;
        var diagnostics = diagnosticsOk?.Value as SystemJobDiagnosticsDto ?? new SystemJobDiagnosticsDto();

        return Ok(new SystemJobManualRunResultDto
        {
            JobKey = item.Key,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            ProcessedCount = processedCount,
            SkippedCount = skippedCount,
            Summary = summary,
            Diagnostics = diagnostics,
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
            ManualRunEnabled = true,
            ManualRunHint = "Можно вручную создать резервную копию базы данных.",
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
            NextRunHint = enabled ? "Сервис проверяет задачи с напоминаниями на ближайшие 7 дней и подтягивает ближайшие сроки дел в контрольные задачи." : "Отправка напоминаний выключена в конфигурации.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить и отправить наступившие напоминания." : "Ручной запуск недоступен, пока напоминания по задачам выключены.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildNotificationRetryJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var enabled = ParseBool(
            settings.GetValueOrDefault(NotificationRetrySchedulerService.EnabledSettingKey),
            _configuration.GetValue("Notifications:Retry:Enabled", true));
        var intervalMinutes = NotificationRetrySchedulerService.NormalizeInterval(ParseInt(
            settings.GetValueOrDefault(NotificationRetrySchedulerService.IntervalMinutesSettingKey),
            _configuration.GetValue("Notifications:Retry:IntervalMinutes", NotificationRetrySchedulerService.DefaultIntervalMinutes)));
        var delayMinutes = NotificationRetrySchedulerService.NormalizeDelay(ParseInt(
            settings.GetValueOrDefault(NotificationRetrySchedulerService.DelayMinutesSettingKey),
            _configuration.GetValue("Notifications:Retry:DelayMinutes", NotificationRetrySchedulerService.DefaultDelayMinutes)));
        var batchSize = NotificationRetrySchedulerService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(NotificationRetrySchedulerService.BatchSizeSettingKey),
            _configuration.GetValue("Notifications:Retry:BatchSize", NotificationRetrySchedulerService.DefaultBatchSize)));
        var maxAttempts = NotificationRetrySchedulerService.NormalizeMaxAttempts(ParseInt(
            settings.GetValueOrDefault(NotificationRetrySchedulerService.MaxAttemptsSettingKey),
            _configuration.GetValue("Notifications:Retry:MaxAttempts", NotificationRetrySchedulerService.DefaultMaxAttempts)));
        var pendingCount = _notificationRetryRunner is null
            ? await CountFailedExternalDeliveriesAsync(enabled, delayMinutes, ct)
            : await _notificationRetryRunner.CountEligibleRetriesAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.NotificationRetry,
            Name = "Повторная отправка уведомлений",
            Description = "Повторяет отправку email и Telegram-уведомлений, которые завершились временной ошибкой канала.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Каждые {intervalMinutes} мин., задержка перед повтором {delayMinutes} мин.",
            PendingCount = pendingCount,
            LastProcessedCount = batchSize,
            NextRunHint = enabled
                ? $"За один запуск обрабатывается до {batchSize} ошибок, максимум попыток: {maxAttempts}."
                : "Автоматическая повторная отправка уведомлений выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить ошибки доставки и повторить отправку." : "Ручной запуск недоступен, пока повторная отправка уведомлений выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildYandexDiskCrmSyncJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var enabled = ParseBool(
            settings.GetValueOrDefault(YandexDiskCrmSyncSchedulerService.EnabledSettingKey),
            _configuration.GetValue("YandexDisk:CrmSync:Scheduler:Enabled", true));
        var intervalMinutes = YandexDiskCrmSyncSchedulerService.NormalizeInterval(ParseInt(
            settings.GetValueOrDefault(YandexDiskCrmSyncSchedulerService.IntervalMinutesSettingKey),
            _configuration.GetValue("YandexDisk:CrmSync:Scheduler:IntervalMinutes", YandexDiskCrmSyncSchedulerService.DefaultIntervalMinutes)));
        var delayMinutes = YandexDiskCrmSyncSchedulerService.NormalizeDelay(ParseInt(
            settings.GetValueOrDefault(YandexDiskCrmSyncSchedulerService.DelayMinutesSettingKey),
            _configuration.GetValue("YandexDisk:CrmSync:Scheduler:DelayMinutes", YandexDiskCrmSyncSchedulerService.DefaultDelayMinutes)));
        var batchSize = YandexDiskCrmSyncSchedulerService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(YandexDiskCrmSyncSchedulerService.BatchSizeSettingKey),
            _configuration.GetValue("YandexDisk:CrmSync:Scheduler:BatchSize", YandexDiskCrmSyncSchedulerService.DefaultBatchSize)));
        var maxAttempts = YandexDiskCrmSyncSchedulerService.NormalizeMaxAttempts(ParseInt(
            settings.GetValueOrDefault(YandexDiskCrmSyncSchedulerService.MaxAttemptsSettingKey),
            _configuration.GetValue("YandexDisk:CrmSync:Scheduler:MaxAttempts", YandexDiskCrmSyncSchedulerService.DefaultMaxAttempts)));
        var pendingCount = _yandexDiskCrmSyncRunner is null
            ? await CountPendingYandexDiskCrmSyncItemsAsync(enabled, delayMinutes, maxAttempts, ct)
            : await _yandexDiskCrmSyncRunner.CountEligibleItemsAsync(ct);

        var lastSync = await _db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .Where(item => item.LastSyncedAt != null || item.Status == YandexDiskCrmSyncStatus.Failed)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => new { item.UpdatedAt, item.FileName, item.Status, item.LastError })
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.YandexDiskCrmSync,
            Name = "Синхронизация CRM с Я.Диском",
            Description = "Повторяет отправку CRM-документов на Я.Диск, если предыдущая попытка завершилась ошибкой.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Каждые {intervalMinutes} мин., задержка перед повтором {delayMinutes} мин.",
            PendingCount = pendingCount,
            LastProcessedCount = batchSize,
            LastActivityAt = lastSync?.UpdatedAt,
            LastActivityDescription = lastSync is null
                ? null
                : lastSync.Status == YandexDiskCrmSyncStatus.Failed
                    ? $"Последняя ошибка Я.Диска: {lastSync.LastError}"
                    : $"Последний отправленный документ: {lastSync.FileName}",
            NextRunHint = enabled
                ? $"За один запуск обрабатывается до {batchSize} документов, максимум попыток: {maxAttempts}."
                : "Фоновая отправка CRM-документов на Я.Диск выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить очередь и повторить отправку CRM-документов на Я.Диск." : "Ручной запуск недоступен, пока фоновая синхронизация CRM с Я.Диском выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildNotificationDeliveryRetentionJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var enabled = ParseBool(
            settings.GetValueOrDefault(NotificationDeliveryRetentionService.EnabledSettingKey),
            _configuration.GetValue("Notifications:DeliveryRetention:Enabled", true));
        var retentionDays = NotificationDeliveryRetentionService.NormalizeRetentionDays(ParseInt(
            settings.GetValueOrDefault(NotificationDeliveryRetentionService.RetentionDaysSettingKey),
            _configuration.GetValue("Notifications:DeliveryRetention:RetentionDays", NotificationDeliveryRetentionService.DefaultRetentionDays)));
        var intervalHours = NotificationDeliveryRetentionService.NormalizeIntervalHours(ParseInt(
            settings.GetValueOrDefault(NotificationDeliveryRetentionService.IntervalHoursSettingKey),
            _configuration.GetValue("Notifications:DeliveryRetention:IntervalHours", NotificationDeliveryRetentionService.DefaultIntervalHours)));
        var batchSize = NotificationDeliveryRetentionService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(NotificationDeliveryRetentionService.BatchSizeSettingKey),
            _configuration.GetValue("Notifications:DeliveryRetention:BatchSize", NotificationDeliveryRetentionService.DefaultBatchSize)));
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var pendingCount = _notificationDeliveryRetentionRunner is null
            ? await _db.NotificationDeliveries
                .AsNoTracking()
                .Where(item => item.CreatedAt < cutoff && item.Status != NotificationDeliveryStatus.Pending)
                .CountAsync(ct)
            : await _notificationDeliveryRetentionRunner.CountEligibleDeliveriesAsync(ct);
        var oldestDeliveryAt = await _db.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Status != NotificationDeliveryStatus.Pending)
            .OrderBy(item => item.CreatedAt)
            .Select(item => (DateTime?)item.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.NotificationDeliveryRetention,
            Name = "Очистка технических логов уведомлений",
            Description = "Удаляет устаревшие технические записи доставки уведомлений по настроенному сроку хранения.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Каждые {intervalHours} ч., хранение {retentionDays} дн.",
            PendingCount = pendingCount,
            LastProcessedCount = batchSize,
            LastActivityAt = oldestDeliveryAt,
            LastActivityDescription = oldestDeliveryAt is null ? null : $"Самая старая техническая запись: {oldestDeliveryAt:dd.MM.yyyy HH:mm}.",
            NextRunHint = enabled
                ? $"За один запуск удаляется до {batchSize} устаревших записей; ожидают очистки: {pendingCount}."
                : "Автоочистка технических логов уведомлений выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную выполнить очистку устаревших технических записей доставки." : "Ручной запуск недоступен, пока автоочистка выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildExternalCalendarSyncJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var schedulerEnabled = ParseBool(
            settings.GetValueOrDefault(CalendarSyncSchedulerService.EnabledSettingKey),
            _configuration.GetValue("CalendarSync:Scheduler:Enabled", false));
        var intervalMinutes = CalendarSyncSchedulerService.NormalizeInterval(ParseInt(
            settings.GetValueOrDefault(CalendarSyncSchedulerService.IntervalMinutesSettingKey),
            _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", CalendarSyncSchedulerService.DefaultIntervalMinutes)));
        var batchSize = CalendarSyncSchedulerService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(CalendarSyncSchedulerService.BatchSizeSettingKey),
            _configuration.GetValue("CalendarSync:Scheduler:BatchSize", CalendarSyncSchedulerService.DefaultBatchSize)));
        var lookAheadDays = CalendarSyncSchedulerService.NormalizeLookAheadDays(ParseInt(
            settings.GetValueOrDefault(CalendarSyncSchedulerService.LookAheadDaysSettingKey),
            _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", CalendarSyncSchedulerService.DefaultLookAheadDays)));
        var state = _calendarSyncRunner is null
            ? new CalendarSyncSchedulerState(
                schedulerEnabled,
                _featureFlags.CurrentValue.Legal.ExternalCalendarSync,
                CalendarSyncProviderRegistry.BuildReadiness(_featureFlags.CurrentValue.Legal.ExternalCalendarSync),
                0,
                null,
                intervalMinutes,
                batchSize,
                lookAheadDays)
            : await _calendarSyncRunner.BuildDiagnosticsStateAsync(ct);

        var enabled = state.SchedulerEnabled && state.FeatureEnabled;
        var ready = state.Readiness.ReadyToConnect;
        var blockedCount = state.BlockingRequiredItems;
        var status = !state.FeatureEnabled
            ? "blocked"
            : !state.SchedulerEnabled
                ? "disabled"
                : ready
                    ? "active"
                    : "warning";
        var statusText = !state.FeatureEnabled
            ? "Ожидает feature flag"
            : !state.SchedulerEnabled
                ? "Выключено"
                : ready
                    ? "Готово к проверке"
                    : "Требует подготовки";

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.ExternalCalendarSync,
            Name = "Внешние календари Google/Яндекс",
            Description = "Проверяет готовность внешней календарной синхронизации и события-кандидаты без изменения внешних календарей до настройки OAuth.",
            Enabled = enabled,
            Status = status,
            StatusText = statusText,
            IntervalText = $"Каждые {state.IntervalMinutes} мин., горизонт {state.LookAheadDays} дн.",
            PendingCount = state.CandidateCount,
            LastProcessedCount = state.BatchSize,
            LastActivityAt = state.OldestCandidateAt,
            LastActivityDescription = state.OldestCandidateAt is null ? null : $"Ближайшее событие-кандидат: {state.OldestCandidateAt:dd.MM.yyyy HH:mm}.",
            NextRunHint = ready
                ? $"За один запуск проверяется до {state.BatchSize} событий. Внешняя запись остается подготовительным контуром до OAuth."
                : $"Не закрыто обязательных пунктов готовности: {blockedCount}.",
            ManualRunEnabled = state.SchedulerEnabled && state.FeatureEnabled,
            ManualRunHint = state.FeatureEnabled
                ? state.SchedulerEnabled
                    ? "Можно вручную выполнить подготовительную проверку внешних календарей."
                    : "Ручной запуск недоступен, пока фоновая проверка внешних календарей выключена."
                : "Ручной запуск недоступен, пока feature flag внешних календарей выключен.",
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
                    (clientCase.ActivityConfirmedAt ?? clientCase.CreatedAt) <= cutoff &&
                    (_db.TaskJournalComments
                        .Where(comment => comment.TaskJournalItem.ClientCaseId == clientCase.Id)
                        .Max(comment => (DateTime?)(comment.UpdatedAt ?? comment.CreatedAt)) ?? clientCase.CreatedAt) <= cutoff &&
                    (_db.ClientCaseDocuments
                        .Where(document => document.ClientCaseId == clientCase.Id)
                        .Max(document => (DateTime?)document.UploadedAt) ?? clientCase.CreatedAt) <= cutoff &&
                    (_db.ClientCaseCourtEvents
                        .Where(courtEvent => courtEvent.ClientCaseId == clientCase.Id)
                        .Max(courtEvent => (DateTime?)(courtEvent.UpdatedAt ?? courtEvent.CreatedAt)) ?? clientCase.CreatedAt) <= cutoff &&
                    !_db.TaskJournalItems.Any(task =>
                        task.ClientCaseId == clientCase.Id &&
                        CaseControlActiveTaskStatuses.Contains(task.Status)),
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
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить неактивные дела и создать контрольные задачи." : "Ручной запуск недоступен, пока автоконтроль неактивных дел выключен.",
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
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную запустить синхронизацию судебных источников." : "Ручной запуск доступен только после включения настройки и feature flag судебной синхронизации.",
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
            ManualRunEnabled = true,
            ManualRunHint = "Можно вручную запустить сверку денежных остатков за предыдущий день.",
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
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную выполнить очистку журнала действий по текущему сроку хранения." : "Ручной запуск недоступен, пока срок хранения журнала не задан.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto?> BuildSingleJobAsync(string jobKey, CancellationToken ct)
    {
        var normalized = (jobKey ?? string.Empty).Trim();
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == DatabaseBackupEnabledKey ||
                item.Key == DatabaseBackupScheduleKey ||
                item.Key == NotificationRetrySchedulerService.EnabledSettingKey ||
                item.Key == NotificationRetrySchedulerService.IntervalMinutesSettingKey ||
                item.Key == NotificationRetrySchedulerService.DelayMinutesSettingKey ||
                item.Key == NotificationRetrySchedulerService.BatchSizeSettingKey ||
                item.Key == NotificationRetrySchedulerService.MaxAttemptsSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.EnabledSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.DelayMinutesSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == YandexDiskCrmSyncSchedulerService.MaxAttemptsSettingKey ||
                item.Key == NotificationDeliveryRetentionService.EnabledSettingKey ||
                item.Key == NotificationDeliveryRetentionService.RetentionDaysSettingKey ||
                item.Key == NotificationDeliveryRetentionService.IntervalHoursSettingKey ||
                item.Key == NotificationDeliveryRetentionService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.EnabledSettingKey ||
                item.Key == CalendarSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == CalendarSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.LookAheadDaysSettingKey ||
                item.Key == CaseControlEnabledKey ||
                item.Key == CaseControlInactiveDaysKey ||
                item.Key == CaseControlLimitKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.EnabledSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == ClientCaseCourtSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == UserActivityRetentionDaysKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, ct);

        return normalized switch
        {
            SystemJobKeys.DatabaseBackup => await BuildDatabaseBackupJobAsync(settings, ct),
            SystemJobKeys.TaskReminders => await BuildTaskReminderJobAsync(DateTime.UtcNow, ct),
            SystemJobKeys.NotificationRetry => await BuildNotificationRetryJobAsync(settings, ct),
            SystemJobKeys.YandexDiskCrmSync => await BuildYandexDiskCrmSyncJobAsync(settings, ct),
            SystemJobKeys.NotificationDeliveryRetention => await BuildNotificationDeliveryRetentionJobAsync(settings, ct),
            SystemJobKeys.ExternalCalendarSync => await BuildExternalCalendarSyncJobAsync(settings, ct),
            SystemJobKeys.CaseInactivityControl => await BuildCaseControlJobAsync(settings, DateTime.UtcNow, ct),
            SystemJobKeys.CourtSync => await BuildCourtSyncJobAsync(settings, ct),
            SystemJobKeys.MoneyBalanceReconciliation => await BuildMoneyBalanceReconciliationJobAsync(ct),
            SystemJobKeys.UserActivityRetention => await BuildUserActivityRetentionJobAsync(settings, ct),
            _ => null,
        };
    }

    private async Task<int> CountFailedExternalDeliveriesAsync(
        bool enabled,
        int delayMinutes,
        CancellationToken ct)
    {
        if (!enabled)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes);
        return await _db.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(item =>
                item.Status == NotificationDeliveryStatus.Failed &&
                item.CreatedAt <= cutoff &&
                (item.Channel == NotificationChannels.Email || item.Channel == NotificationChannels.Telegram),
                ct);
    }

    private async Task<int> CountPendingYandexDiskCrmSyncItemsAsync(
        bool enabled,
        int delayMinutes,
        int maxAttempts,
        CancellationToken ct)
    {
        if (!enabled)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes);
        return await _db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .CountAsync(item =>
                (item.Status == YandexDiskCrmSyncStatus.Pending || item.Status == YandexDiskCrmSyncStatus.Failed)
                && item.AttemptCount < maxAttempts
                && item.UpdatedAt <= cutoff,
                ct);
    }

    private async Task RecordManualSuccessAsync(
        string jobKey,
        DateTime startedAt,
        int? processedCount,
        int? skippedCount,
        string summary,
        CancellationToken ct)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(jobKey, startedAt, processedCount, skippedCount, summary, ct);
        }
    }

    private async Task RecordManualFailureAsync(
        string jobKey,
        DateTime startedAt,
        Exception exception,
        CancellationToken ct)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordFailureAsync(jobKey, startedAt, exception, "Ручной запуск фонового сервиса завершился с ошибкой.", ct);
        }
    }

    private static void EnsureRunner(object? runner)
    {
        if (runner is null)
        {
            throw new InvalidOperationException("Ручной запуск сервиса недоступен в текущей конфигурации приложения.");
        }
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static SystemJobDiagnosticsItemDto WithLastRun(
        SystemJobDiagnosticsItemDto item,
        IReadOnlyDictionary<string, SystemJobRunState> lastRuns)
    {
        if (!lastRuns.TryGetValue(item.Key, out var state))
        {
            return item;
        }

        return new SystemJobDiagnosticsItemDto
        {
            Key = item.Key,
            Name = item.Name,
            Description = item.Description,
            Enabled = item.Enabled,
            Status = item.Status,
            StatusText = item.StatusText,
            IntervalText = item.IntervalText,
            PendingCount = item.PendingCount,
            LastProcessedCount = item.LastProcessedCount,
            LastActivityAt = item.LastActivityAt,
            LastActivityDescription = item.LastActivityDescription,
            NextRunHint = item.NextRunHint,
            LastRunStatus = state.Status,
            LastRunStatusText = state.Status == "success" ? "Успешно" : state.Status == "error" ? "Ошибка" : "Неизвестно",
            LastRunStartedAt = state.StartedAt,
            LastRunFinishedAt = state.FinishedAt,
            LastRunError = state.ErrorMessage,
            ManualRunEnabled = item.ManualRunEnabled,
            ManualRunHint = item.ManualRunHint,
        };
    }
}

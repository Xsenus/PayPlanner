using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
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
public sealed class SystemJobsController : ControllerBase, IAsyncActionFilter
{
    private const string DatabaseBackupEnabledKey = "database-backup.enabled";
    private const string DatabaseBackupScheduleKey = "database-backup.schedule-local";
    private const string DatabaseBackupS3EnabledKey = "database-backup.s3.enabled";
    private const string DatabaseBackupS3ConnectionIdsKey = "database-backup.s3.connection-ids";
    private const string DatabaseBackupS3ResultsKey = "database-backup.s3.results";
    private const string DatabaseBackupYandexDiskEnabledKey = "database-backup.yandex-disk.enabled";
    private const string DatabaseBackupYandexDiskResultsKey = "database-backup.yandex-disk.results";
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private const string CaseControlEnabledKey = "tasks.automation.caseControl.enabled";
    private const string CaseControlInactiveDaysKey = "tasks.automation.caseControl.inactiveDays";
    private const string CaseControlLimitKey = "tasks.automation.caseControl.limit";
    private const string UserActivityRetentionDaysKey = "user-activity.retention-days";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
    private readonly CalendarSyncLogRetentionService? _calendarSyncLogRetentionRunner;
    private readonly PaymentAdvanceReviewMonitoringService? _advanceReviewMonitoringRunner;
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
        PaymentAdvanceReviewMonitoringService? advanceReviewMonitoringRunner = null,
        CalendarSyncSchedulerService? calendarSyncRunner = null,
        TaskJournalAutomationSchedulerService? caseControlRunner = null,
        ClientCaseCourtSyncSchedulerService? courtSyncRunner = null,
        MoneyBalanceReconciliationSchedulerService? moneyBalanceRunner = null,
        UserActivityRetentionService? userActivityRetentionRunner = null,
        YandexDiskCrmSyncSchedulerService? yandexDiskCrmSyncRunner = null,
        CalendarSyncLogRetentionService? calendarSyncLogRetentionRunner = null)
    {
        _db = db;
        _configuration = configuration;
        _featureFlags = featureFlags;
        _jobRunState = jobRunState;
        _databaseBackupService = databaseBackupService;
        _taskReminderRunner = taskReminderRunner;
        _notificationRetryRunner = notificationRetryRunner;
        _notificationDeliveryRetentionRunner = notificationDeliveryRetentionRunner;
        _advanceReviewMonitoringRunner = advanceReviewMonitoringRunner;
        _calendarSyncRunner = calendarSyncRunner;
        _caseControlRunner = caseControlRunner;
        _courtSyncRunner = courtSyncRunner;
        _moneyBalanceRunner = moneyBalanceRunner;
        _userActivityRetentionRunner = userActivityRetentionRunner;
        _yandexDiskCrmSyncRunner = yandexDiskCrmSyncRunner;
        _calendarSyncLogRetentionRunner = calendarSyncLogRetentionRunner;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<SystemJobDiagnosticsDto>> GetDiagnostics(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct);
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == DatabaseBackupEnabledKey ||
                item.Key == DatabaseBackupScheduleKey ||
                item.Key == DatabaseBackupS3EnabledKey ||
                item.Key == DatabaseBackupS3ConnectionIdsKey ||
                item.Key == DatabaseBackupS3ResultsKey ||
                item.Key == DatabaseBackupYandexDiskEnabledKey ||
                item.Key == DatabaseBackupYandexDiskResultsKey ||
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
                item.Key == CalendarSyncLogRetentionService.EnabledSettingKey ||
                item.Key == CalendarSyncLogRetentionService.RetentionDaysSettingKey ||
                item.Key == CalendarSyncLogRetentionService.IntervalHoursSettingKey ||
                item.Key == CalendarSyncLogRetentionService.BatchSizeSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.EnabledSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.IntervalHoursSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.WarningAgeDaysSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.CriticalAgeDaysSettingKey ||
                item.Key == CalendarSyncSchedulerService.EnabledSettingKey ||
                item.Key == CalendarSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == CalendarSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.LookAheadDaysSettingKey ||
                item.Key == CalendarSyncSettingsKey ||
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
            await BuildNotificationDeliveryRetentionJobAsync(settings, timeZone, ct),
            await BuildAdvanceReviewMonitoringJobAsync(settings, ct),
            await BuildExternalCalendarSyncJobAsync(settings, timeZone, ct),
            await BuildCalendarSyncLogRetentionJobAsync(settings, timeZone, ct),
            await BuildCaseControlJobAsync(settings, now, ct),
            await BuildCourtSyncJobAsync(settings, ct),
            await BuildMoneyBalanceReconciliationJobAsync(ct),
            await BuildUserActivityRetentionJobAsync(settings, timeZone, ct),
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

                case SystemJobKeys.CalendarSyncLogRetention:
                    EnsureRunner(_calendarSyncLogRetentionRunner);
                    var calendarLogRetentionResult = await _calendarSyncLogRetentionRunner!.RunOnceAsync(ct);
                    processedCount = calendarLogRetentionResult.TotalDeletedCount;
                    summary = calendarLogRetentionResult.Enabled
                        ? processedCount > 0
                            ? $"Удалено устаревших записей синхронизации календарей: Google — {calendarLogRetentionResult.GoogleDeletedCount}, Яндекс — {calendarLogRetentionResult.YandexDeletedCount}."
                            : "Срок хранения журналов синхронизации календарей проверен, удаление не требовалось."
                        : "Очистка журналов синхронизации календарей выключена в настройках.";
                    break;

                case SystemJobKeys.PaymentAdvanceReviewMonitoring:
                    EnsureRunner(_advanceReviewMonitoringRunner);
                    var advanceReviewResult = await _advanceReviewMonitoringRunner!.RunOnceAsync(ct);
                    processedCount = advanceReviewResult.MarkedCount + advanceReviewResult.NotifiedCount + advanceReviewResult.EscalatedCount;
                    skippedCount = advanceReviewResult.ProcessedCount - advanceReviewResult.MarkedCount;
                    summary = advanceReviewResult.Enabled
                        ? processedCount > 0
                            ? $"Мониторинг авансов проверил платежей: {advanceReviewResult.ProcessedCount}, поставил на контроль: {advanceReviewResult.MarkedCount}, создал уведомлений: {advanceReviewResult.NotifiedCount}, эскалаций: {advanceReviewResult.EscalatedCount}."
                            : "Мониторинг авансов выполнен, новых платежей для разбора не найдено."
                        : "Мониторинг нераспределенных авансов выключен в настройках.";
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
                    var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct);
                    var targetDate = MoneyBalanceReconciliationSchedulerService.ResolveReconciliationTargetDate(DateTime.UtcNow, timeZone);
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
        var s3Enabled = ParseBool(settings.GetValueOrDefault(DatabaseBackupS3EnabledKey), false);
        var s3ConnectionCount = CountS3Connections(settings.GetValueOrDefault(DatabaseBackupS3ConnectionIdsKey));
        var recentS3Result = GetRecentS3UploadResult(settings.GetValueOrDefault(DatabaseBackupS3ResultsKey));
        var yandexDiskEnabled = ParseBool(settings.GetValueOrDefault(DatabaseBackupYandexDiskEnabledKey), false);
        var recentYandexDiskResult = GetRecentYandexDiskUploadResult(settings.GetValueOrDefault(DatabaseBackupYandexDiskResultsKey));
        var cloudDestinations = new List<string>();
        if (s3Enabled && s3ConnectionCount > 0)
        {
            cloudDestinations.Add($"S3-подключений: {s3ConnectionCount}");
        }

        if (yandexDiskEnabled)
        {
            cloudDestinations.Add("Яндекс.Диск");
        }

        var description = cloudDestinations.Count > 0
            ? $"Проверяет расписание, создает резервные копии базы и отправляет их в облачные хранилища: {string.Join(", ", cloudDestinations)}."
            : "Проверяет расписание и создает автоматические резервные копии базы.";
        var status = enabled
            ? recentS3Result?.Status == "failed" || recentYandexDiskResult?.Status == "failed" ? "warning" : "active"
            : "disabled";
        var statusText = enabled
            ? recentS3Result?.Status == "failed" || recentYandexDiskResult?.Status == "failed" ? "Есть ошибка облачной выгрузки" : "Включено"
            : "Выключено";
        var lastActivityDescription = recentYandexDiskResult is not null && (recentS3Result is null || recentYandexDiskResult.UpdatedAt >= recentS3Result.UpdatedAt)
            ? recentYandexDiskResult.Status == "success"
                ? $"Последняя выгрузка backup на Яндекс.Диск успешна: {recentYandexDiskResult.TargetPath}"
                : recentYandexDiskResult.Status == "failed"
                    ? $"Последняя ошибка Яндекс.Диска backup: {recentYandexDiskResult.Error}"
                    : $"Выгрузка backup на Яндекс.Диск ожидает выполнения: {recentYandexDiskResult.TargetPath}"
            : recentS3Result is null
            ? lastBackup?.Description
            : recentS3Result.Status == "success"
                ? $"Последняя S3-выгрузка backup успешна: {recentS3Result.TargetPath}"
                : recentS3Result.Status == "failed"
                    ? $"Последняя ошибка S3 backup: {recentS3Result.Error}"
                    : $"S3-выгрузка backup ожидает выполнения: {recentS3Result.TargetPath}";

        return new SystemJobDiagnosticsItemDto
        {
            Key = "database-backup",
            Name = "Резервное копирование базы данных",
            Description = description,
            Enabled = enabled,
            Status = status,
            StatusText = statusText,
            IntervalText = $"Расписание: {schedule}",
            LastActivityAt = new[] { recentS3Result?.UpdatedAt, recentYandexDiskResult?.UpdatedAt, lastBackup?.CreatedAt }.Where(value => value.HasValue).Max(),
            LastActivityDescription = lastActivityDescription,
            NextRunHint = enabled
                ? cloudDestinations.Count > 0
                    ? $"Следующая проверка расписания создаст локальную копию и отправит ее в облачные хранилища: {string.Join(", ", cloudDestinations)}."
                    : "Следующая проверка расписания выполняется фоновым сервисом."
                : "Автоматические резервные копии отключены.",
            ManualRunEnabled = true,
            ManualRunHint = "Можно вручную создать резервную копию базы данных.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildTaskReminderJobAsync(DateTime now, CancellationToken ct)
    {
        var enabled = _configuration.GetValue("Notifications:TaskReminders:Enabled", true);
        var pollSeconds = Math.Clamp(_configuration.GetValue("Notifications:TaskReminders:PollSeconds", 60), 15, 3600);
        var lookAhead = now.AddDays(7);
        var eligibleCounts = _db.UseObjectAuditModel
            ? _db.Database.SqlQuery<int>($$"""
                SELECT
                    CAST(COUNT(*) AS INTEGER) AS "Value"
                FROM "TaskJournalItems"
                WHERE {{enabled}}
                  AND NOT "IsDeleted"
                  AND "AssigneeUserId" IS NOT NULL
                  AND "Status" <> 'done'
                  AND "Status" <> 'cancelled'
                  AND "DueAt" <= {{lookAhead}}
                  AND "RemindersJson" <> '[]'
                  AND "ChannelsJson" <> '[]'
                """)
            : _db.Database.SqlQuery<int>($$"""
                SELECT
                    CAST(COUNT(*) AS INTEGER) AS "Value"
                FROM "TaskJournalItems"
                WHERE {{enabled}}
                  AND "AssigneeUserId" IS NOT NULL
                  AND "Status" <> 'done'
                  AND "Status" <> 'cancelled'
                  AND "DueAt" <= {{lookAhead}}
                  AND "RemindersJson" <> '[]'
                  AND "ChannelsJson" <> '[]'
                """);
        var statistics = await (
                from eligibleCount in eligibleCounts
                from lastReminder in _db.NotificationDeliveries
                    .AsNoTracking()
                    .Where(item => item.EventKey.Contains(":reminder:"))
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(1)
                    .DefaultIfEmpty()
                select new TaskReminderStatistics
                {
                    EligibleCount = eligibleCount,
                    CreatedAt = lastReminder == null ? null : lastReminder.CreatedAt,
                    Subject = lastReminder == null ? null : lastReminder.Subject,
                })
            .SingleAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "task-reminders",
            Name = "Напоминания по задачам",
            Description = "Отправляет напоминания исполнителям по срокам задач и просрочкам.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Проверка каждые {pollSeconds} сек.",
            PendingCount = statistics.EligibleCount,
            LastActivityAt = statistics.CreatedAt,
            LastActivityDescription = statistics.Subject,
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
            : await _notificationRetryRunner.CountEligibleRetriesAsync(enabled, delayMinutes, maxAttempts, ct);

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
        var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes);
        var statistics = await _db.Database
            .SqlQuery<YandexDiskCrmSyncStatistics>($$"""
                SELECT
                    CAST(CASE WHEN {{enabled}} THEN
                        (SELECT COUNT(*)
                         FROM "YandexDiskCrmSyncStates"
                         WHERE "Status" IN ({{YandexDiskCrmSyncStatus.Pending}}, {{YandexDiskCrmSyncStatus.Failed}})
                           AND "AttemptCount" < {{maxAttempts}}
                           AND "UpdatedAt" <= {{cutoff}})
                        ELSE 0 END AS INTEGER) AS "EligibleCount",
                    "LastSync"."UpdatedAt",
                    "LastSync"."FileName",
                    "LastSync"."Status",
                    "LastSync"."LastError"
                FROM (SELECT 1) AS "Base"
                LEFT JOIN (
                    SELECT "UpdatedAt", "FileName", "Status", "LastError"
                    FROM "YandexDiskCrmSyncStates"
                    WHERE "LastSyncedAt" IS NOT NULL OR "Status" = {{YandexDiskCrmSyncStatus.Failed}}
                    ORDER BY "UpdatedAt" DESC
                    LIMIT 1
                ) AS "LastSync" ON 1 = 1
                """)
            .SingleAsync(ct);
        var pendingCount = statistics.EligibleCount;

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
            LastActivityAt = statistics.UpdatedAt,
            LastActivityDescription = statistics.UpdatedAt is null
                ? null
                : statistics.Status == YandexDiskCrmSyncStatus.Failed
                    ? $"Последняя ошибка Я.Диска: {statistics.LastError}"
                    : $"Последний отправленный документ: {statistics.FileName}",
            NextRunHint = enabled
                ? $"За один запуск обрабатывается до {batchSize} документов, максимум попыток: {maxAttempts}."
                : "Фоновая отправка CRM-документов на Я.Диск выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить очередь и повторить отправку CRM-документов на Я.Диск." : "Ручной запуск недоступен, пока фоновая синхронизация CRM с Я.Диском выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildNotificationDeliveryRetentionJobAsync(
        IReadOnlyDictionary<string, string> settings,
        TimeZoneInfo timeZone,
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
        var retentionStatistics = await _db.Database
            .SqlQuery<RetentionStatistics>($$"""
                SELECT
                    CAST((SELECT COUNT(*)
                          FROM "NotificationDeliveries"
                          WHERE "CreatedAt" < {{cutoff}} AND "Status" <> {{NotificationDeliveryStatus.Pending}}) AS INTEGER) AS "EligibleCount",
                    (SELECT "CreatedAt"
                     FROM "NotificationDeliveries"
                     WHERE "Status" <> {{NotificationDeliveryStatus.Pending}}
                     ORDER BY "CreatedAt"
                     LIMIT 1) AS "OldestAt"
                """)
            .SingleAsync(ct);
        var pendingCount = enabled ? retentionStatistics.EligibleCount : 0;
        var oldestDeliveryAt = retentionStatistics.OldestAt;

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
            LastActivityDescription = oldestDeliveryAt is null ? null : $"Самая старая техническая запись: {FormatSystemJobDateTime(oldestDeliveryAt.Value, timeZone)}.",
            NextRunHint = enabled
                ? $"За один запуск удаляется до {batchSize} устаревших записей; ожидают очистки: {pendingCount}."
                : "Автоочистка технических логов уведомлений выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную выполнить очистку устаревших технических записей доставки." : "Ручной запуск недоступен, пока автоочистка выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildAdvanceReviewMonitoringJobAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken ct)
    {
        var enabled = ParseBool(
            settings.GetValueOrDefault(PaymentAdvanceReviewMonitoringService.EnabledSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:Enabled", true));
        var intervalHours = PaymentAdvanceReviewMonitoringService.NormalizeIntervalHours(ParseInt(
            settings.GetValueOrDefault(PaymentAdvanceReviewMonitoringService.IntervalHoursSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:IntervalHours", PaymentAdvanceReviewMonitoringService.DefaultIntervalHours)));
        var batchSize = PaymentAdvanceReviewMonitoringService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:BatchSize", PaymentAdvanceReviewMonitoringService.DefaultBatchSize)));
        var warningAgeDays = PaymentAdvanceReviewMonitoringService.NormalizeWarningAgeDays(ParseInt(
            settings.GetValueOrDefault(PaymentAdvanceReviewMonitoringService.WarningAgeDaysSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:WarningAgeDays", PaymentAdvanceReviewMonitoringService.DefaultWarningAgeDays)));
        var criticalAgeDays = PaymentAdvanceReviewMonitoringService.NormalizeCriticalAgeDays(ParseInt(
            settings.GetValueOrDefault(PaymentAdvanceReviewMonitoringService.CriticalAgeDaysSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:CriticalAgeDays", PaymentAdvanceReviewMonitoringService.DefaultCriticalAgeDays)),
            warningAgeDays);
        var criticalCutoff = DateTime.UtcNow.Date.AddDays(-criticalAgeDays);
        var singleton = _db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"");
        var lastNotifications = _db.UserNotifications
            .AsNoTracking()
            .Where(item => item.Type == UserNotificationTypes.PaymentAdvanceReviewRequired
                || item.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation)
            .OrderByDescending(item => item.CreatedAt)
            .Take(1);
        AdvanceReviewDiagnosticsStatistics statistics;
        if (enabled)
        {
            var candidateStatistics = BuildAdvanceReviewCandidateQuery()
                .GroupBy(_ => 1)
                .Select(group => new AdvanceReviewCandidateStatistics
                {
                    PendingCount = group.Count(),
                    CriticalPendingCount = group.Count(payment =>
                        (payment.LastPaymentDate
                            ?? payment.PaidDate
                            ?? (payment.Date == default ? payment.CreatedAt : payment.Date)).Date
                        <= criticalCutoff),
                });
            statistics = await (
                    from _ in singleton
                    from candidates in candidateStatistics
                        .Take(1)
                        .DefaultIfEmpty()
                    from lastNotification in lastNotifications.DefaultIfEmpty()
                    select new AdvanceReviewDiagnosticsStatistics
                    {
                        PendingCount = candidates == null ? 0 : candidates.PendingCount ?? 0,
                        CriticalPendingCount = candidates == null ? 0 : candidates.CriticalPendingCount ?? 0,
                        CreatedAt = lastNotification == null ? null : lastNotification.CreatedAt,
                        Title = lastNotification == null ? null : lastNotification.Title,
                    })
                .SingleAsync(ct);
        }
        else
        {
            statistics = await (
                    from _ in singleton
                    from lastNotification in lastNotifications.DefaultIfEmpty()
                    select new AdvanceReviewDiagnosticsStatistics
                    {
                        PendingCount = 0,
                        CriticalPendingCount = 0,
                        CreatedAt = lastNotification == null ? null : lastNotification.CreatedAt,
                        Title = lastNotification == null ? null : lastNotification.Title,
                    })
                .SingleAsync(ct);
        }
        var pendingCount = statistics.PendingCount;
        var criticalPendingCount = statistics.CriticalPendingCount;

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.PaymentAdvanceReviewMonitoring,
            Name = "Мониторинг нераспределенных авансов",
            Description = "Находит входящие оплаченные платежи без счета и акта, ставит их на контроль аванса, уведомляет ответственного пользователя и эскалирует просроченные авансы руководителям.",
            Enabled = enabled,
            Status = enabled ? criticalPendingCount > 0 ? "warning" : "active" : "disabled",
            StatusText = enabled ? criticalPendingCount > 0 ? "Есть просроченные авансы" : "Включено" : "Выключено",
            IntervalText = $"Каждые {intervalHours} ч.",
            PendingCount = pendingCount,
            LastProcessedCount = batchSize,
            LastActivityAt = statistics.CreatedAt,
            LastActivityDescription = statistics.Title,
            NextRunHint = enabled
                ? $"За один запуск проверяется до {batchSize} платежей, ожидают разбора: {pendingCount}, критичных старше {criticalAgeDays} дн.: {criticalPendingCount}. Предупреждение начинается с {warningAgeDays} дн."
                : "Автоматический мониторинг авансов выключен.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную проверить платежи, создать уведомления ответственным и эскалации по просроченным авансам." : "Ручной запуск недоступен, пока мониторинг авансов выключен.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildCalendarSyncLogRetentionJobAsync(
        IReadOnlyDictionary<string, string> settings,
        TimeZoneInfo timeZone,
        CancellationToken ct)
    {
        var enabled = ParseBool(
            settings.GetValueOrDefault(CalendarSyncLogRetentionService.EnabledSettingKey),
            _configuration.GetValue("CalendarSync:LogRetention:Enabled", true));
        var retentionDays = CalendarSyncLogRetentionService.NormalizeRetentionDays(ParseInt(
            settings.GetValueOrDefault(CalendarSyncLogRetentionService.RetentionDaysSettingKey),
            _configuration.GetValue("CalendarSync:LogRetention:RetentionDays", CalendarSyncLogRetentionService.DefaultRetentionDays)));
        var intervalHours = CalendarSyncLogRetentionService.NormalizeIntervalHours(ParseInt(
            settings.GetValueOrDefault(CalendarSyncLogRetentionService.IntervalHoursSettingKey),
            _configuration.GetValue("CalendarSync:LogRetention:IntervalHours", CalendarSyncLogRetentionService.DefaultIntervalHours)));
        var batchSize = CalendarSyncLogRetentionService.NormalizeBatchSize(ParseInt(
            settings.GetValueOrDefault(CalendarSyncLogRetentionService.BatchSizeSettingKey),
            _configuration.GetValue("CalendarSync:LogRetention:BatchSize", CalendarSyncLogRetentionService.DefaultBatchSize)));
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var statistics = await _db.Database
            .SqlQuery<CalendarSyncLogRetentionStatistics>($$"""
                SELECT
                    CAST(CASE WHEN {{enabled}} THEN
                        (SELECT COUNT(*) FROM "GoogleCalendarSyncLogs" WHERE "CreatedAt" < {{cutoff}})
                        ELSE 0 END AS INTEGER) AS "GoogleEligibleCount",
                    CAST(CASE WHEN {{enabled}} THEN
                        (SELECT COUNT(*) FROM "YandexCalendarSyncLogs" WHERE "CreatedAt" < {{cutoff}})
                        ELSE 0 END AS INTEGER) AS "YandexEligibleCount",
                    (SELECT MIN("CreatedAt") FROM "GoogleCalendarSyncLogs") AS "GoogleOldestAt",
                    (SELECT MIN("CreatedAt") FROM "YandexCalendarSyncLogs") AS "YandexOldestAt"
                """)
            .SingleAsync(ct);
        var counts = new CalendarSyncLogRetentionCounts(
            statistics.GoogleEligibleCount,
            statistics.YandexEligibleCount);
        var oldestGoogleAt = statistics.GoogleOldestAt;
        var oldestYandexAt = statistics.YandexOldestAt;
        var oldestLogAt = oldestGoogleAt is null ? oldestYandexAt : oldestYandexAt is null ? oldestGoogleAt : oldestGoogleAt < oldestYandexAt ? oldestGoogleAt : oldestYandexAt;

        return new SystemJobDiagnosticsItemDto
        {
            Key = SystemJobKeys.CalendarSyncLogRetention,
            Name = "Очистка журналов синхронизации календарей",
            Description = "Удаляет устаревшие технические записи синхронизации Google и Яндекс календарей по настроенному сроку хранения.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = $"Каждые {intervalHours} ч., хранение {retentionDays} дн.",
            PendingCount = counts.TotalCount,
            LastProcessedCount = batchSize * 2,
            LastActivityAt = oldestLogAt,
            LastActivityDescription = oldestLogAt is null ? null : $"Самая старая техническая запись: {FormatSystemJobDateTime(oldestLogAt.Value, timeZone)}.",
            NextRunHint = enabled
                ? $"За один запуск удаляется до {batchSize} записей каждого календарного провайдера; ожидают очистки: Google — {counts.GoogleCount}, Яндекс — {counts.YandexCount}."
                : "Автоочистка журналов синхронизации календарей выключена.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную удалить устаревшие записи журналов синхронизации календарей." : "Ручной запуск недоступен, пока автоочистка выключена.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto> BuildExternalCalendarSyncJobAsync(
        IReadOnlyDictionary<string, string> settings,
        TimeZoneInfo timeZone,
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
        var calendarSettings = LoadCalendarSyncSettings(settings);
        var externalCalendarSyncEnabled = calendarSettings.ExternalCalendarSyncEnabled == true;
        var state = _calendarSyncRunner is null
            ? new CalendarSyncSchedulerState(
                schedulerEnabled,
                externalCalendarSyncEnabled,
                CalendarSyncProviderRegistry.BuildReadiness(
                    calendarSettings.GoogleCalendarSyncEnabled == true,
                    calendarSettings.YandexCalendarSyncEnabled == true,
                    calendarSettings),
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
            ? "Выключено в админке"
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
            LastActivityDescription = state.OldestCandidateAt is null ? null : $"Ближайшее событие-кандидат: {FormatSystemJobDateTime(state.OldestCandidateAt.Value, timeZone)}.",
            NextRunHint = ready
                ? $"За один запуск проверяется до {state.BatchSize} событий. Внешняя запись остается подготовительным контуром до OAuth."
                : $"Не закрыто обязательных пунктов готовности: {blockedCount}.",
            ManualRunEnabled = state.SchedulerEnabled && state.FeatureEnabled,
            ManualRunHint = state.FeatureEnabled
                ? state.SchedulerEnabled
                    ? "Можно вручную выполнить подготовительную проверку внешних календарей."
                    : "Ручной запуск недоступен, пока фоновая проверка внешних календарей выключена."
                : "Ручной запуск недоступен, пока внешний календарный контур выключен в админке.",
        };
    }

    private CalendarSyncSettingsDto LoadCalendarSyncSettings(IReadOnlyDictionary<string, string> settings)
    {
        var fallback = _featureFlags.CurrentValue.Legal.ExternalCalendarSync;
        var raw = settings.GetValueOrDefault(CalendarSyncSettingsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings(fallback);
        }

        try
        {
            return CalendarSyncProviderRegistry.NormalizeSettings(
                JsonSerializer.Deserialize<CalendarSyncSettingsDto>(raw, JsonOptions),
                fallback);
        }
        catch (JsonException)
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings(fallback);
        }
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
        var candidates = _db.ClientCases
            .AsNoTracking()
            .Where(clientCase =>
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
                    CaseControlActiveTaskStatuses.Contains(task.Status)));
        var singleton = _db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\"");
        var statistics = await (
                from _ in singleton
                from lastAutomation in _db.ObjectAuditEvents
                    .AsNoTracking()
                    .Where(item =>
                        item.ObjectType == "client-case" &&
                        item.Action == "Automation" &&
                        item.MetadataJson != null &&
                        item.MetadataJson.Contains("caseInactivityControlTaskCreated"))
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(1)
                    .DefaultIfEmpty()
                select new CaseControlStatistics
                {
                    EligibleCount = enabled ? candidates.Count() : 0,
                    CreatedAt = lastAutomation == null ? null : lastAutomation.CreatedAt,
                    Description = lastAutomation == null ? null : lastAutomation.Description,
                })
            .SingleAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "case-inactivity-control",
            Name = "Автоконтроль неактивных дел",
            Description = "Создает контрольные задачи по делам, которые долго остаются без активности.",
            Enabled = enabled,
            Status = enabled ? "active" : "disabled",
            StatusText = enabled ? "Включено" : "Выключено",
            IntervalText = "Проверка каждые 5 минут",
            PendingCount = statistics.EligibleCount,
            LastProcessedCount = limit,
            LastActivityAt = statistics.CreatedAt,
            LastActivityDescription = statistics.Description,
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
        var eligibleCounts = _db.UseObjectAuditModel
            ? _db.Database.SqlQuery<int>($$"""
                SELECT CAST(COUNT(*) AS INTEGER) AS "Value"
                FROM "ClientCases"
                WHERE {{enabled}}
                  AND NOT "IsDeleted"
                  AND "Status" = {{ClientCaseStatus.Open}}
                  AND "CourtUrl" <> ''
                """)
            : _db.Database.SqlQuery<int>($$"""
                SELECT CAST(COUNT(*) AS INTEGER) AS "Value"
                FROM "ClientCases"
                WHERE {{enabled}}
                  AND "Status" = {{ClientCaseStatus.Open}}
                  AND "CourtUrl" <> ''
                """);
        var statistics = await (
                from eligibleCount in eligibleCounts
                from lastCourtSync in _db.ClientCases
                    .AsNoTracking()
                    .Where(item => item.EventHistory.Contains("судебн"))
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(1)
                    .DefaultIfEmpty()
                select new CourtSyncStatistics
                {
                    EligibleCount = eligibleCount,
                    CreatedAt = lastCourtSync == null ? null : lastCourtSync.CreatedAt,
                    CaseNumber = lastCourtSync == null ? null : lastCourtSync.CaseNumber,
                })
            .SingleAsync(ct);

        return new SystemJobDiagnosticsItemDto
        {
            Key = "court-sync",
            Name = "Синхронизация судебных источников",
            Description = "Проверяет дела с привязкой к судебным источникам и переносит найденные изменения в карточку дела.",
            Enabled = enabled,
            Status = enabled ? "active" : settingEnabled ? "blocked" : "disabled",
            StatusText = enabled ? "Включено" : settingEnabled ? "Ожидает feature flag" : "Выключено",
            IntervalText = $"Каждые {intervalMinutes} мин.",
            PendingCount = statistics.EligibleCount,
            LastProcessedCount = batchSize,
            LastActivityAt = statistics.CreatedAt,
            LastActivityDescription = statistics.CreatedAt is null ? null : $"Последнее дело с судебной активностью: {statistics.CaseNumber}",
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
        TimeZoneInfo timeZone,
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
            LastActivityDescription = oldestLogAt is null ? null : $"Самая старая запись журнала: {FormatSystemJobDateTime(oldestLogAt.Value, timeZone)}.",
            NextRunHint = enabled ? $"Хранение журнала: {retentionDays} дн." : "Автоочистка не удаляет записи без заданного срока хранения.",
            ManualRunEnabled = enabled,
            ManualRunHint = enabled ? "Можно вручную выполнить очистку журнала действий по текущему сроку хранения." : "Ручной запуск недоступен, пока срок хранения журнала не задан.",
        };
    }

    private async Task<SystemJobDiagnosticsItemDto?> BuildSingleJobAsync(string jobKey, CancellationToken ct)
    {
        var normalized = (jobKey ?? string.Empty).Trim();
        var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct);
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == DatabaseBackupEnabledKey ||
                item.Key == DatabaseBackupScheduleKey ||
                item.Key == DatabaseBackupS3EnabledKey ||
                item.Key == DatabaseBackupS3ConnectionIdsKey ||
                item.Key == DatabaseBackupS3ResultsKey ||
                item.Key == DatabaseBackupYandexDiskEnabledKey ||
                item.Key == DatabaseBackupYandexDiskResultsKey ||
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
                item.Key == CalendarSyncLogRetentionService.EnabledSettingKey ||
                item.Key == CalendarSyncLogRetentionService.RetentionDaysSettingKey ||
                item.Key == CalendarSyncLogRetentionService.IntervalHoursSettingKey ||
                item.Key == CalendarSyncLogRetentionService.BatchSizeSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.EnabledSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.IntervalHoursSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.WarningAgeDaysSettingKey ||
                item.Key == PaymentAdvanceReviewMonitoringService.CriticalAgeDaysSettingKey ||
                item.Key == CalendarSyncSchedulerService.EnabledSettingKey ||
                item.Key == CalendarSyncSchedulerService.IntervalMinutesSettingKey ||
                item.Key == CalendarSyncSchedulerService.BatchSizeSettingKey ||
                item.Key == CalendarSyncSchedulerService.LookAheadDaysSettingKey ||
                item.Key == CalendarSyncSettingsKey ||
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
            SystemJobKeys.NotificationDeliveryRetention => await BuildNotificationDeliveryRetentionJobAsync(settings, timeZone, ct),
            SystemJobKeys.CalendarSyncLogRetention => await BuildCalendarSyncLogRetentionJobAsync(settings, timeZone, ct),
            SystemJobKeys.PaymentAdvanceReviewMonitoring => await BuildAdvanceReviewMonitoringJobAsync(settings, ct),
            SystemJobKeys.ExternalCalendarSync => await BuildExternalCalendarSyncJobAsync(settings, timeZone, ct),
            SystemJobKeys.CaseInactivityControl => await BuildCaseControlJobAsync(settings, DateTime.UtcNow, ct),
            SystemJobKeys.CourtSync => await BuildCourtSyncJobAsync(settings, ct),
            SystemJobKeys.MoneyBalanceReconciliation => await BuildMoneyBalanceReconciliationJobAsync(ct),
            SystemJobKeys.UserActivityRetention => await BuildUserActivityRetentionJobAsync(settings, timeZone, ct),
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

    private IQueryable<Payment> BuildAdvanceReviewCandidateQuery()
    {
        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
        return _db.Payments
            .AsNoTracking()
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision);
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

    private int? GetCurrentUserId()
    {
        var value = HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var userId) ? userId : null;
    }

    private static string FormatSystemJobDateTime(DateTime value, TimeZoneInfo timeZone)
        => CalendarTimeZoneHelper.FormatUserDateTime(value, timeZone, culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static int CountS3Connections(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 0;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?.Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() ?? 0;
        }
        catch (JsonException)
        {
            return rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        }
    }

    private static DatabaseBackupS3UploadResult? GetRecentS3UploadResult(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<DatabaseBackupS3UploadResult>>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?.OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DatabaseBackupYandexDiskUploadResult? GetRecentYandexDiskUploadResult(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<DatabaseBackupYandexDiskUploadResult>>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?.OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
            LastSuccessAt = state.LastSuccessAt ?? (state.Status == "success" ? state.FinishedAt : null),
            LastRunDurationMs = Math.Max(0, (long)(state.FinishedAt - state.StartedAt).TotalMilliseconds),
            LastRunError = state.ErrorMessage,
            ManualRunEnabled = item.ManualRunEnabled,
            ManualRunHint = item.ManualRunHint,
        };
    }

    private sealed class RetentionStatistics
    {
        public int EligibleCount { get; init; }
        public DateTime? OldestAt { get; init; }
    }

    private sealed class CalendarSyncLogRetentionStatistics
    {
        public int GoogleEligibleCount { get; init; }
        public int YandexEligibleCount { get; init; }
        public DateTime? GoogleOldestAt { get; init; }
        public DateTime? YandexOldestAt { get; init; }
    }

    private sealed class AdvanceReviewCandidateStatistics
    {
        public int? PendingCount { get; init; }
        public int? CriticalPendingCount { get; init; }
    }

    private sealed class AdvanceReviewDiagnosticsStatistics
    {
        public int PendingCount { get; init; }
        public int CriticalPendingCount { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string? Title { get; init; }
    }

    private sealed class YandexDiskCrmSyncStatistics
    {
        public int EligibleCount { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public string? FileName { get; init; }
        public string? Status { get; init; }
        public string? LastError { get; init; }
    }

    private sealed class TaskReminderStatistics
    {
        public int EligibleCount { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string? Subject { get; init; }
    }

    private sealed class CourtSyncStatistics
    {
        public int EligibleCount { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string? CaseNumber { get; init; }
    }

    private sealed class CaseControlStatistics
    {
        public int EligibleCount { get; init; }
        public DateTime? CreatedAt { get; init; }
        public string? Description { get; init; }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Cases;

namespace PayPlanner.Api.Services.Notifications;

public sealed class TaskJournalReminderNotificationService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReminderChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        NotificationChannels.InApp,
        NotificationChannels.Email,
        NotificationChannels.Telegram
    };
    private const int DefaultDeadlineSyncLookAheadDays = 7;
    private const int DefaultDeadlineSyncBatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaskJournalReminderNotificationService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private int _lastCaseDeadlineSyncId;
    private DateTime? _lastReminderDueAt;
    private int _lastReminderTaskId;

    public TaskJournalReminderNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TaskJournalReminderNotificationService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
        _jobRunState = jobRunState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Clamp(_configuration.GetValue("Notifications:TaskReminders:PollSeconds", 60), 15, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsEnabled())
            {
                await RunOnceAsync(stoppingToken);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool IsEnabled()
        => _configuration.GetValue("Notifications:TaskReminders:Enabled", true);

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var startedAt = DateTime.UtcNow;
            var sent = await ProcessDueRemindersAsync(cancellationToken);
            if (_jobRunState is not null && sent >= 0)
            {
                await _jobRunState.RecordSuccessAsync(
                    SystemJobKeys.TaskReminders,
                    startedAt,
                    sent,
                    null,
                    sent > 0
                        ? $"Отправлено напоминаний по задачам: {sent}."
                        : "Проверка напоминаний выполнена, новых отправок не было.",
                    cancellationToken);
            }

            return sent;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<int> ProcessDueRemindersAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var nowUtc = DateTime.UtcNow;
            var taskJournalChanged = await SyncUpcomingCaseDeadlineTasksAsync(context, nowUtc, cancellationToken);
            if (taskJournalChanged)
            {
                var realtimePublisher = scope.ServiceProvider.GetService<IRealtimeNotificationPublisher>()
                    ?? NoOpRealtimeNotificationPublisher.Instance;
                await realtimePublisher.PublishTaskJournalChangedAsync(cancellationToken);
            }

            var reminderBatchSize = Math.Clamp(
                _configuration.GetValue("Notifications:TaskReminders:BatchSize", 500), 1, 500);
            var eligibleTasks = context.TaskJournalItems
                .AsNoTracking()
                .Include(task => task.Participants)
                    .ThenInclude(participant => participant.User)
                .Where(task =>
                    task.AssigneeUserId.HasValue
                    && task.Status != "done"
                    && task.Status != "cancelled"
                    && (task.DueAt <= nowUtc.AddDays(7)
                        || (task.ApprovalDueAt.HasValue
                            && task.ApprovalDueAt <= nowUtc
                            && (task.Status == "onApproval" || task.Status == "overdueApproval"))));
            var tasks = await eligibleTasks
                .Where(task => !_lastReminderDueAt.HasValue
                    || task.DueAt > _lastReminderDueAt.Value
                    || task.DueAt == _lastReminderDueAt.Value && task.Id > _lastReminderTaskId)
                .OrderBy(task => task.DueAt)
                .ThenBy(task => task.Id)
                .Take(reminderBatchSize)
                .ToListAsync(cancellationToken);
            if (tasks.Count == 0 && _lastReminderDueAt.HasValue)
            {
                _lastReminderDueAt = null;
                _lastReminderTaskId = 0;
                tasks = await eligibleTasks
                    .OrderBy(task => task.DueAt)
                    .ThenBy(task => task.Id)
                    .Take(reminderBatchSize)
                    .ToListAsync(cancellationToken);
            }
            if (tasks.Count == 0)
            {
                return 0;
            }

            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var reminderTasks = tasks
                .Where(task => DeserializeList(task.ChannelsJson).Any(channel => ReminderChannels.Contains(channel)))
                .ToList();
            var assigneeUserIds = reminderTasks
                .Select(task => task.AssigneeUserId!.Value)
                .Distinct()
                .ToArray();
            var assigneeTimeZoneIds = assigneeUserIds.Length == 0
                ? new Dictionary<int, string?>()
                : await context.UserSettings
                    .AsNoTracking()
                    .Where(setting =>
                        assigneeUserIds.Contains(setting.UserId)
                        && setting.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
                    .Select(setting => new { setting.UserId, Value = (string?)setting.Value })
                    .ToDictionaryAsync(setting => setting.UserId, setting => setting.Value, cancellationToken);
            var fallbackTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);
            var assigneeTimeZones = assigneeUserIds.ToDictionary(
                userId => userId,
                userId => assigneeTimeZoneIds.TryGetValue(userId, out var timeZoneId)
                    ? CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId)
                    : fallbackTimeZone);
            foreach (var task in reminderTasks)
            {
                var assigneeUserId = task.AssigneeUserId!.Value;
                if (IsApprovalOverdueNotificationDue(task, nowUtc))
                {
                    var approvalDeliveries = await notificationService.NotifyTaskApprovalOverdueAsync(task, nowUtc, cancellationToken);
                    sent += approvalDeliveries.Count;
                }

                var assigneeTimeZone = assigneeTimeZones[assigneeUserId];

                foreach (var reminder in DeserializeList(task.RemindersJson))
                {
                    if (!IsReminderDue(task, reminder, nowUtc, assigneeTimeZone))
                    {
                        continue;
                    }

                    var deliveries = await notificationService.NotifyTaskReminderAsync(task, reminder, nowUtc, cancellationToken);
                    sent += deliveries.Count;
                }
            }

            _lastReminderDueAt = tasks[^1].DueAt;
            _lastReminderTaskId = tasks[^1].Id;

            return sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to process task journal reminder notifications.");
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordFailureAsync(
                    SystemJobKeys.TaskReminders,
                    DateTime.UtcNow,
                    ex,
                    "Проверка напоминаний по задачам завершилась с ошибкой.",
                    cancellationToken);
            }
        }

        return -1;
    }

    private async Task<bool> SyncUpcomingCaseDeadlineTasksAsync(
        PaymentContext context,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Keep the installer's exact startup data check ahead of automatic task updates.
        var validationMarker = _configuration["Deployment:ValidationMarkerPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "..", ".deploy-validation-pending");
        if (File.Exists(validationMarker))
        {
            return false;
        }

        var lookAheadDays = Math.Clamp(
            _configuration.GetValue("Notifications:TaskReminders:DeadlineSyncLookAheadDays", DefaultDeadlineSyncLookAheadDays),
            1,
            90);
        var batchSize = Math.Clamp(
            _configuration.GetValue("Notifications:TaskReminders:DeadlineSyncBatchSize", DefaultDeadlineSyncBatchSize),
            1,
            500);
        var fromUtc = nowUtc.AddDays(-1);
        var toUtc = nowUtc.AddDays(lookAheadDays);
        var upcomingCases = context.ClientCases
            .AsNoTracking()
            .Where(clientCase =>
                clientCase.Status != ClientCaseStatus.Closed &&
                clientCase.Status != ClientCaseStatus.Lost &&
                ((clientCase.DueDate.HasValue && clientCase.DueDate.Value >= fromUtc && clientCase.DueDate.Value <= toUtc) ||
                 (clientCase.NextHearingAt.HasValue && clientCase.NextHearingAt.Value >= fromUtc && clientCase.NextHearingAt.Value <= toUtc) ||
                 (clientCase.AppealDeadlineAt.HasValue && clientCase.AppealDeadlineAt.Value >= fromUtc && clientCase.AppealDeadlineAt.Value <= toUtc) ||
                 (clientCase.CassationDeadlineAt.HasValue && clientCase.CassationDeadlineAt.Value >= fromUtc && clientCase.CassationDeadlineAt.Value <= toUtc) ||
                 (clientCase.MotionDeadlineAt.HasValue && clientCase.MotionDeadlineAt.Value >= fromUtc && clientCase.MotionDeadlineAt.Value <= toUtc) ||
                 (clientCase.ObjectionDeadlineAt.HasValue && clientCase.ObjectionDeadlineAt.Value >= fromUtc && clientCase.ObjectionDeadlineAt.Value <= toUtc) ||
                 clientCase.ProceduralDeadlines.Any(deadline =>
                     deadline.Status != "closed" &&
                     deadline.Status != "cancelled" &&
                     deadline.Status != "archived" &&
                     deadline.DueAt >= fromUtc &&
                     deadline.DueAt <= toUtc)));
        var caseIds = await upcomingCases
            .Where(clientCase => clientCase.Id > _lastCaseDeadlineSyncId)
            .OrderBy(clientCase => clientCase.Id)
            .Select(clientCase => clientCase.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (caseIds.Count == 0 && _lastCaseDeadlineSyncId > 0)
        {
            _lastCaseDeadlineSyncId = 0;
            caseIds = await upcomingCases
                .OrderBy(clientCase => clientCase.Id)
                .Select(clientCase => clientCase.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
        }
        var taskJournalChanged = false;
        foreach (var caseId in caseIds)
        {
            try
            {
                taskJournalChanged |= await ClientCaseDeadlineTaskSyncService.SyncAsync(
                    context,
                    caseId,
                    actorUserId: null,
                    actorName: "Система",
                    cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException && !cancellationToken.IsCancellationRequested
                && CaseTaskSyncScope.IsRolledBackConflict(error))
            {
                _logger.LogDebug("Deferred deadline task synchronization for busy or changed case {ClientCaseId}. A later traversal will retry.", caseId);
            }
            context.ChangeTracker.Clear();
            _lastCaseDeadlineSyncId = caseId;
        }

        return taskJournalChanged;
    }

    private static bool IsReminderDue(
        TaskJournalItem task,
        string reminderKey,
        DateTime nowUtc,
        TimeZoneInfo assigneeTimeZone)
    {
        var dueAt = DateTime.SpecifyKind(task.DueAt, DateTimeKind.Utc);
        return reminderKey switch
        {
            "before7d" => IsInsideReminderWindow(nowUtc, dueAt, TimeSpan.FromDays(7)),
            "before3d" => IsInsideReminderWindow(nowUtc, dueAt, TimeSpan.FromDays(3)),
            "before1d" => IsInsideReminderWindow(nowUtc, dueAt, TimeSpan.FromDays(1)),
            "before1h" => IsInsideReminderWindow(nowUtc, dueAt, TimeSpan.FromHours(1)),
            "before15m" => IsInsideReminderWindow(nowUtc, dueAt, TimeSpan.FromMinutes(15)),
            "atDue" => nowUtc >= dueAt,
            "dailyOverdue" => CalendarTimeZoneHelper.ToUserLocal(nowUtc, assigneeTimeZone).Date >
                              CalendarTimeZoneHelper.ToUserLocal(dueAt, assigneeTimeZone).Date,
            _ => false
        };
    }

    private static bool IsApprovalOverdueNotificationDue(TaskJournalItem task, DateTime nowUtc)
        => task.ApprovalDueAt.HasValue
            && task.ApprovalDueAt.Value <= nowUtc
            && (string.Equals(task.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase))
            && task.Participants.Any(participant =>
                string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase));

    private static bool IsInsideReminderWindow(DateTime nowUtc, DateTime dueAt, TimeSpan offset)
        => nowUtc >= dueAt.Subtract(offset) && nowUtc < dueAt;

    private static IReadOnlyList<string> DeserializeList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}

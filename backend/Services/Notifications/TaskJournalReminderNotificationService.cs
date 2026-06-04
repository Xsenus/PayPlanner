using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

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

    private async Task<int> ProcessDueRemindersAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var nowUtc = DateTime.UtcNow;
            await SyncUpcomingCaseDeadlineTasksAsync(context, nowUtc, cancellationToken);

            var tasks = await context.TaskJournalItems
                .AsNoTracking()
                .Where(task =>
                    task.AssigneeUserId.HasValue
                    && task.Status != "done"
                    && task.Status != "cancelled"
                    && task.DueAt <= nowUtc.AddDays(7))
                .OrderBy(task => task.DueAt)
                .Take(500)
                .ToListAsync(cancellationToken);
            if (tasks.Count == 0)
            {
                return 0;
            }

            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var assigneeTimeZones = new Dictionary<int, TimeZoneInfo>();
            foreach (var task in tasks)
            {
                var channels = DeserializeList(task.ChannelsJson);
                if (!channels.Any(channel => ReminderChannels.Contains(channel)))
                {
                    continue;
                }

                var assigneeUserId = task.AssigneeUserId!.Value;
                if (!assigneeTimeZones.TryGetValue(assigneeUserId, out var assigneeTimeZone))
                {
                    assigneeTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(
                        context,
                        assigneeUserId,
                        cancellationToken);
                    assigneeTimeZones[assigneeUserId] = assigneeTimeZone;
                }

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

    private async Task<int> SyncUpcomingCaseDeadlineTasksAsync(
        PaymentContext context,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
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
        var caseIds = await context.ClientCases
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
                     deadline.DueAt >= fromUtc &&
                     deadline.DueAt <= toUtc)))
            .OrderBy(clientCase => clientCase.Id)
            .Select(clientCase => clientCase.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (var caseId in caseIds)
        {
            await ClientCaseDeadlineTaskSyncService.SyncAsync(
                context,
                caseId,
                actorUserId: null,
                actorName: "Система",
                cancellationToken);
            context.ChangeTracker.Clear();
        }

        return caseIds.Count;
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

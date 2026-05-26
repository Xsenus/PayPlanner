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
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
            var nowUtc = DateTime.UtcNow;

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

            foreach (var task in tasks)
            {
                var channels = DeserializeList(task.ChannelsJson);
                if (!channels.Any(channel => ReminderChannels.Contains(channel)))
                {
                    continue;
                }

                foreach (var reminder in DeserializeList(task.RemindersJson))
                {
                    if (!IsReminderDue(task, reminder, nowUtc))
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

    private static bool IsReminderDue(TaskJournalItem task, string reminderKey, DateTime nowUtc)
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
            "dailyOverdue" => nowUtc.Date > dueAt.Date,
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

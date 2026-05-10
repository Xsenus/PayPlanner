using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed class TaskJournalAutomationSchedulerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ActiveTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "inProgress", "waiting", "onApproval", "revision", "overdueApproval"
    };

    private const string EnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string InactiveDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string LimitSettingKey = "tasks.automation.caseControl.limit";
    private const string DefaultAssigneeSettingKey = "tasks.automation.caseControl.defaultAssigneeUserId";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaskJournalAutomationSchedulerService> _logger;

    public TaskJournalAutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TaskJournalAutomationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollSeconds = Math.Clamp(_configuration.GetValue("TaskJournal:Automation:PollSeconds", 3600), 60, 86_400);
        var initialDelaySeconds = Math.Clamp(_configuration.GetValue("TaskJournal:Automation:InitialDelaySeconds", 30), 0, 3600);

        if (initialDelaySeconds > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(initialDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

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

    public async Task<TaskJournalAutomationSchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var settings = await LoadSettingsAsync(context, cancellationToken);
            if (!settings.Enabled)
            {
                return TaskJournalAutomationSchedulerResult.Disabled;
            }

            var assignee = await ResolveAutomationUserAsync(context, settings.DefaultAssigneeUserId, cancellationToken);
            if (assignee is null)
            {
                _logger.LogWarning("Task journal automation skipped: no active approved user was found for automatic control tasks.");
                return new TaskJournalAutomationSchedulerResult(0, 0, true, "Не найден активный пользователь для автоконтроля задач.");
            }

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-settings.InactiveDays);
            var candidates = await context.ClientCases
                .Include(item => item.Client)
                .Where(clientCase =>
                    clientCase.ClientId.HasValue &&
                    clientCase.Client != null &&
                    clientCase.Status != ClientCaseStatus.Closed)
                .Select(clientCase => new
                {
                    Case = clientCase,
                    ActiveTaskCount = context.TaskJournalItems.Count(task =>
                        task.ClientCaseId == clientCase.Id &&
                        ActiveTaskStatuses.Contains(task.Status)),
                    LastTaskAt = context.TaskJournalItems
                        .Where(task => task.ClientCaseId == clientCase.Id)
                        .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
                })
                .Where(item => item.ActiveTaskCount == 0 && (item.LastTaskAt ?? item.Case.CreatedAt) <= cutoff)
                .OrderBy(item => item.LastTaskAt ?? item.Case.CreatedAt)
                .ThenBy(item => item.Case.Client!.Name)
                .ThenBy(item => item.Case.Title)
                .Take(settings.Limit)
                .ToListAsync(cancellationToken);

            var created = 0;
            foreach (var candidate in candidates)
            {
                if (candidate.Case.Client is null || !candidate.Case.ClientId.HasValue)
                {
                    continue;
                }

                var stillHasActiveTask = await context.TaskJournalItems
                    .AsNoTracking()
                    .AnyAsync(task =>
                        task.ClientCaseId == candidate.Case.Id &&
                        ActiveTaskStatuses.Contains(task.Status),
                        cancellationToken);
                if (stillHasActiveTask)
                {
                    continue;
                }

                var task = BuildAutomationTask(candidate.Case, candidate.Case.Client, assignee, candidate.LastTaskAt, now);
                context.TaskJournalItems.Add(task);
                created += 1;
            }

            if (created > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            return new TaskJournalAutomationSchedulerResult(created, candidates.Count - created, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to create automatic task journal case-control tasks.");
            return new TaskJournalAutomationSchedulerResult(0, 0, true, ex.Message);
        }
    }

    private async Task<TaskJournalAutomationSettings> LoadSettingsAsync(PaymentContext context, CancellationToken cancellationToken)
    {
        var values = await context.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == InactiveDaysSettingKey ||
                item.Key == LimitSettingKey ||
                item.Key == DefaultAssigneeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new TaskJournalAutomationSettings(
            GetBoolean(values, EnabledSettingKey, "TaskJournal:Automation:Enabled", true),
            Math.Clamp(GetInteger(values, InactiveDaysSettingKey, "TaskJournal:Automation:InactiveDays", 14), 1, 365),
            Math.Clamp(GetInteger(values, LimitSettingKey, "TaskJournal:Automation:Limit", 25), 1, 100),
            GetNullableInteger(values, DefaultAssigneeSettingKey, "TaskJournal:Automation:DefaultAssigneeUserId"));
    }

    private async Task<User?> ResolveAutomationUserAsync(PaymentContext context, int? configuredUserId, CancellationToken cancellationToken)
    {
        if (configuredUserId.HasValue)
        {
            var configured = await context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user =>
                    user.Id == configuredUserId.Value &&
                    user.IsActive &&
                    user.IsApproved,
                    cancellationToken);
            if (configured is not null)
            {
                return configured;
            }
        }

        return await context.Users
            .AsNoTracking()
            .Include(user => user.Role)
            .Where(user => user.IsActive && user.IsApproved)
            .OrderByDescending(user => user.Role != null && user.Role.Name == "admin")
            .ThenBy(user => user.FullName)
            .ThenBy(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static TaskJournalItem BuildAutomationTask(
        ClientCase clientCase,
        Client client,
        User assignee,
        DateTime? lastTaskAt,
        DateTime now)
    {
        var assigneeName = DisplayName(assignee);
        var lastActivity = lastTaskAt ?? clientCase.CreatedAt;
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity).TotalDays));
        var task = new TaskJournalItem
        {
            ClientId = clientCase.ClientId!.Value,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(client.Name, clientCase.Title),
            Title = "Проверить движение по делу",
            Description = "Автоконтроль: по делу давно нет активной задачи. Проверьте текущее состояние и зафиксируйте следующий шаг.",
            AssigneeUserId = assignee.Id,
            AssigneeName = assigneeName,
            AuthorUserId = assignee.Id,
            AuthorName = assigneeName,
            DueAt = now.Date.AddDays(1).AddHours(9),
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = daysWithoutActiveTask >= 14 ? "high" : "normal",
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp"]),
            TagsJson = SerializeList(["автоконтроль"]),
            CreatedAt = now,
            UpdatedAt = now,
        };

        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = assignee.Id,
            Role = "assignee",
            DisplayName = assigneeName,
            Email = assignee.Email,
            SortOrder = 0,
            ApprovalStatus = "pending",
            Resolution = null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        task.Comments.Add(new TaskJournalComment
        {
            AuthorUserId = assignee.Id,
            AuthorName = assigneeName,
            Kind = "system",
            Text = $"Задача создана фоновым автоконтролем: по делу нет активных задач {daysWithoutActiveTask} дн.",
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });

        return task;
    }

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();

    private static string BuildObjectName(string clientName, string? caseTitle)
        => string.IsNullOrWhiteSpace(caseTitle) ? clientName.Trim() : $"{clientName.Trim()} - {caseTitle.Trim()}";

    private static string SerializeList(IEnumerable<string> values)
        => JsonSerializer.Serialize(values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), JsonOptions);

    private bool GetBoolean(IReadOnlyDictionary<string, string> settings, string key, string configPath, bool fallback)
    {
        if (settings.TryGetValue(key, out var settingValue) && bool.TryParse(settingValue, out var settingResult))
        {
            return settingResult;
        }

        return _configuration.GetValue(configPath, fallback);
    }

    private int GetInteger(IReadOnlyDictionary<string, string> settings, string key, string configPath, int fallback)
    {
        if (settings.TryGetValue(key, out var settingValue) && int.TryParse(settingValue, out var settingResult))
        {
            return settingResult;
        }

        return _configuration.GetValue(configPath, fallback);
    }

    private int? GetNullableInteger(IReadOnlyDictionary<string, string> settings, string key, string configPath)
    {
        if (settings.TryGetValue(key, out var settingValue) && int.TryParse(settingValue, out var settingResult))
        {
            return settingResult;
        }

        return _configuration.GetValue<int?>(configPath);
    }

    private sealed record TaskJournalAutomationSettings(
        bool Enabled,
        int InactiveDays,
        int Limit,
        int? DefaultAssigneeUserId);
}

public sealed record TaskJournalAutomationSchedulerResult(
    int CreatedCount,
    int SkippedCount,
    bool Enabled,
    string? Error)
{
    public static TaskJournalAutomationSchedulerResult Disabled { get; } = new(0, 0, false, null);
}

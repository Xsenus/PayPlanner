using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class TaskJournalAutomationSchedulerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ActiveTaskStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "inProgress", "waiting", "onApproval", "revision", "overdueApproval"
    };
    private static readonly ClientCaseStatus[] FinishedCaseStatuses =
    [
        ClientCaseStatus.Closed,
        ClientCaseStatus.Lost,
    ];

    private const string EnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string InactiveDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string LimitSettingKey = "tasks.automation.caseControl.limit";
    private const string DefaultAssigneeSettingKey = "tasks.automation.caseControl.defaultAssigneeUserId";
    private const string EscalationDaysSettingKey = "tasks.automation.caseControl.escalationDays";
    private const string EscalationUserSettingKey = "tasks.automation.caseControl.escalationUserId";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaskJournalAutomationSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;

    public TaskJournalAutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TaskJournalAutomationSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
        _jobRunState = jobRunState;
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
        var startedAt = DateTime.UtcNow;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var notificationService = scope.ServiceProvider.GetService<INotificationService>();
            var settings = await LoadSettingsAsync(context, cancellationToken);
            if (!settings.Enabled)
            {
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordSuccessAsync(
                        SystemJobKeys.CaseInactivityControl,
                        startedAt,
                        0,
                        null,
                        "Автоконтроль неактивных дел выключен в настройках.",
                        cancellationToken);
                }

                return TaskJournalAutomationSchedulerResult.Disabled;
            }

            var defaultAssignee = await ResolveConfiguredAutomationUserAsync(context, settings.DefaultAssigneeUserId, cancellationToken);
            var escalationUser = await ResolveConfiguredAutomationUserAsync(context, settings.EscalationUserId, cancellationToken);

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-settings.InactiveDays);
            var candidates = await context.ClientCases
                .Include(item => item.Client)
                .Include(item => item.LegalEntity)
                .Where(clientCase =>
                    ((clientCase.ClientId.HasValue && clientCase.Client != null) ||
                     (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)) &&
                    !FinishedCaseStatuses.Contains(clientCase.Status))
                .Select(clientCase => new
                {
                    Case = clientCase,
                    ActiveTaskCount = context.TaskJournalItems.Count(task =>
                        task.ClientCaseId == clientCase.Id &&
                        ActiveTaskStatuses.Contains(task.Status)),
                    LastTaskAt = context.TaskJournalItems
                        .IgnoreQueryFilters()
                        .Where(task => task.ClientCaseId == clientCase.Id)
                        .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
                })
                .Where(item => item.ActiveTaskCount == 0
                    && (item.LastTaskAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.Case.LastViewedAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.Case.ActivityConfirmedAt ?? item.Case.CreatedAt) <= cutoff
                    && item.Case.InactivityRemindersEnabled)
                .OrderBy(item => item.LastTaskAt ?? item.Case.CreatedAt)
                .ThenBy(item => item.Case.Client != null
                    ? item.Case.Client.Name
                    : item.Case.LegalEntity != null
                        ? item.Case.LegalEntity.ShortName
                        : string.Empty)
                .ThenBy(item => item.Case.Title)
                .Take(settings.Limit)
                .ToListAsync(cancellationToken);

            var created = 0;
            var createdTasks = new List<CreatedAutomationTask>();
            foreach (var candidate in candidates)
            {
                var owner = ResolveCaseOwner(candidate.Case);
                if (owner is null)
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

                var assignee = await ResolveAutomationAssigneeAsync(context, candidate.Case, defaultAssignee, cancellationToken);
                if (assignee is null)
                {
                    continue;
                }

                var daysWithoutActiveTask = ResolveDaysWithoutActiveTask(candidate.Case, candidate.LastTaskAt, now);
                var task = BuildAutomationTask(candidate.Case, owner, assignee, candidate.LastTaskAt, now, settings, escalationUser);
                context.TaskJournalItems.Add(task);
                createdTasks.Add(new CreatedAutomationTask(task, daysWithoutActiveTask, daysWithoutActiveTask >= settings.EscalationDays));
                created += 1;
            }

            if (created > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                AddInactivityAuditEvents(context, createdTasks, now);
                await context.SaveChangesAsync(cancellationToken);
                await NotifyCreatedAutomationTasksAsync(notificationService, createdTasks, cancellationToken);
            }

            var result = new TaskJournalAutomationSchedulerResult(created, candidates.Count - created, true, null);
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordSuccessAsync(
                    SystemJobKeys.CaseInactivityControl,
                    startedAt,
                    created,
                    result.SkippedCount,
                    created > 0
                        ? $"Создано контрольных задач по неактивным делам: {created}."
                        : "Проверка неактивных дел выполнена, новые контрольные задачи не требовались.",
                    cancellationToken);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to create automatic task journal case-control tasks.");
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordFailureAsync(
                    SystemJobKeys.CaseInactivityControl,
                    startedAt,
                    ex,
                    "Автоконтроль неактивных дел завершился с ошибкой.",
                    cancellationToken);
            }

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
                item.Key == DefaultAssigneeSettingKey ||
                item.Key == EscalationDaysSettingKey ||
                item.Key == EscalationUserSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var inactiveDays = Math.Clamp(GetInteger(values, InactiveDaysSettingKey, "TaskJournal:Automation:InactiveDays", 14), 1, 365);
        return new TaskJournalAutomationSettings(
            GetBoolean(values, EnabledSettingKey, "TaskJournal:Automation:Enabled", true),
            inactiveDays,
            Math.Clamp(GetInteger(values, LimitSettingKey, "TaskJournal:Automation:Limit", 25), 1, 100),
            GetNullableInteger(values, DefaultAssigneeSettingKey, "TaskJournal:Automation:DefaultAssigneeUserId"),
            Math.Clamp(GetInteger(values, EscalationDaysSettingKey, "TaskJournal:Automation:EscalationDays", 30), inactiveDays, 365),
            GetNullableInteger(values, EscalationUserSettingKey, "TaskJournal:Automation:EscalationUserId"));
    }

    private async Task<User?> ResolveConfiguredAutomationUserAsync(
        PaymentContext context,
        int? configuredUserId,
        CancellationToken cancellationToken)
    {
        if (!configuredUserId.HasValue)
        {
            return null;
        }

        return await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user =>
                user.Id == configuredUserId.Value &&
                user.IsActive &&
                user.IsApproved,
                cancellationToken);
    }

    private static int? ResolveCaseResponsibleUserId(ClientCase clientCase)
        => clientCase.ResponsibleUserId ?? clientCase.Client?.ResponsibleUserId ?? clientCase.LegalEntity?.ResponsibleUserId;

    private static async Task<User?> ResolveAutomationAssigneeAsync(
        PaymentContext context,
        ClientCase clientCase,
        User? fallbackAssignee,
        CancellationToken cancellationToken)
    {
        var responsibleUserId = ResolveCaseResponsibleUserId(clientCase);
        if (!responsibleUserId.HasValue)
        {
            return fallbackAssignee;
        }

        if (fallbackAssignee?.Id == responsibleUserId.Value)
        {
            return fallbackAssignee;
        }

        return await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user =>
                user.Id == responsibleUserId.Value &&
                user.IsActive &&
                user.IsApproved,
                cancellationToken);
    }

    private static string ResolveAutomationPriority(int daysWithoutActiveTask, int inactiveDays, int escalationDays)
    {
        if (daysWithoutActiveTask >= escalationDays)
        {
            return "urgent";
        }

        return daysWithoutActiveTask >= inactiveDays ? "high" : "normal";
    }

    private static string BuildAutomationTaskDescription(bool isEscalated)
        => isEscalated
            ? "Автоконтроль: по делу давно нет активной задачи, сработала эскалация. Срочно проверьте текущее состояние и зафиксируйте следующий шаг."
            : "Автоконтроль: по делу давно нет активной задачи. Проверьте текущее состояние и зафиксируйте следующий шаг.";

    private static void AddEscalationObserver(TaskJournalItem task, User? escalationUser, int assigneeUserId, DateTime now)
    {
        if (escalationUser is null || escalationUser.Id == assigneeUserId)
        {
            return;
        }

        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = escalationUser.Id,
            Role = "observer",
            DisplayName = DisplayName(escalationUser),
            Email = escalationUser.Email,
            SortOrder = task.Participants.Count,
            ApprovalStatus = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static TaskJournalItem BuildAutomationTask(
        ClientCase clientCase,
        CaseOwnerBinding owner,
        User assignee,
        DateTime? lastTaskAt,
        DateTime now,
        TaskJournalAutomationSettings settings,
        User? escalationUser)
    {
        var assigneeName = DisplayName(assignee);
        var lastActivity = ResolveLastCaseActivity(clientCase, lastTaskAt);
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity).TotalDays));
        var isEscalated = daysWithoutActiveTask >= settings.EscalationDays;
        var task = new TaskJournalItem
        {
            ClientId = owner.ClientId,
            LegalEntityId = owner.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(owner.Name, clientCase.Title),
            Title = "Проверить движение по делу",
            Description = BuildAutomationTaskDescription(isEscalated),
            AssigneeUserId = assignee.Id,
            AssigneeName = assigneeName,
            AuthorUserId = assignee.Id,
            AuthorName = assigneeName,
            DueAt = now.Date.AddDays(1).AddHours(9),
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.InactiveDays, settings.EscalationDays),
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
        AddEscalationObserver(task, isEscalated ? escalationUser : null, assignee.Id, now);

        return task;
    }

    private static void AddInactivityAuditEvents(
        PaymentContext context,
        IReadOnlyCollection<CreatedAutomationTask> createdTasks,
        DateTime now)
    {
        foreach (var item in createdTasks)
        {
            if (!item.Task.ClientCaseId.HasValue)
            {
                continue;
            }

            context.ObjectAuditEvents.Add(new ObjectAuditEvent
            {
                ObjectType = "client-case",
                ObjectId = item.Task.ClientCaseId.Value,
                Action = "Automation",
                Description = $"Автоконтроль создал задачу \"{item.Task.Title}\" для {item.Task.AssigneeName}: нет движения {item.DaysWithoutActiveTask} дн.",
                ActorUserName = "Автоконтроль дел",
                CreatedAt = now,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    eventType = "caseInactivityControlTaskCreated",
                    taskId = item.Task.Id,
                    taskTitle = item.Task.Title,
                    assigneeUserId = item.Task.AssigneeUserId,
                    assigneeName = item.Task.AssigneeName,
                    daysWithoutActiveTask = item.DaysWithoutActiveTask,
                    isEscalated = item.IsEscalated,
                }, JsonOptions),
            });
        }
    }

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();

    private static CaseOwnerBinding? ResolveCaseOwner(ClientCase clientCase)
    {
        if (clientCase.ClientId.HasValue && clientCase.Client is not null)
        {
            return new CaseOwnerBinding(clientCase.ClientId.Value, null, clientCase.Client.Name);
        }

        if (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity is not null)
        {
            return new CaseOwnerBinding(null, clientCase.LegalEntityId.Value, GetLegalEntityName(clientCase.LegalEntity));
        }

        return null;
    }

    private static string GetLegalEntityName(LegalEntity entity)
        => !string.IsNullOrWhiteSpace(entity.ShortName)
            ? entity.ShortName.Trim()
            : !string.IsNullOrWhiteSpace(entity.FullName)
                ? entity.FullName.Trim()
                : $"Юрлицо #{entity.Id}";

    private static string BuildObjectName(string clientName, string? caseTitle)
        => string.IsNullOrWhiteSpace(caseTitle) ? clientName.Trim() : $"{clientName.Trim()} - {caseTitle.Trim()}";

    private static DateTime ResolveLastCaseActivity(ClientCase clientCase, DateTime? lastTaskAt)
    {
        var lastActivity = clientCase.CreatedAt;
        if (lastTaskAt.HasValue && lastTaskAt.Value > lastActivity)
        {
            lastActivity = lastTaskAt.Value;
        }

        if (clientCase.LastViewedAt.HasValue && clientCase.LastViewedAt.Value > lastActivity)
        {
            lastActivity = clientCase.LastViewedAt.Value;
        }

        if (clientCase.ActivityConfirmedAt.HasValue && clientCase.ActivityConfirmedAt.Value > lastActivity)
        {
            lastActivity = clientCase.ActivityConfirmedAt.Value;
        }

        return lastActivity;
    }

    private static int ResolveDaysWithoutActiveTask(ClientCase clientCase, DateTime? lastTaskAt, DateTime now)
        => Math.Max(0, (int)Math.Floor((now - ResolveLastCaseActivity(clientCase, lastTaskAt)).TotalDays));

    private async Task NotifyCreatedAutomationTasksAsync(
        INotificationService? notificationService,
        IReadOnlyList<CreatedAutomationTask> createdTasks,
        CancellationToken cancellationToken)
    {
        if (notificationService is null || createdTasks.Count == 0)
        {
            return;
        }

        foreach (var (task, daysWithoutActiveTask, _) in createdTasks)
        {
            try
            {
                await notificationService.NotifyCaseInactivityControlTaskCreatedAsync(
                    task,
                    daysWithoutActiveTask,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to notify about automatic case-control task {TaskId}.", task.Id);
            }
        }
    }

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
        int? DefaultAssigneeUserId,
        int EscalationDays,
        int? EscalationUserId);

    private sealed record CreatedAutomationTask(TaskJournalItem Task, int DaysWithoutActiveTask, bool IsEscalated);

    private sealed record CaseOwnerBinding(int? ClientId, int? LegalEntityId, string Name);
}

public sealed record TaskJournalAutomationSchedulerResult(
    int CreatedCount,
    int SkippedCount,
    bool Enabled,
    string? Error)
{
    public static TaskJournalAutomationSchedulerResult Disabled { get; } = new(0, 0, false, null);
}

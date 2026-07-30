using System.Globalization;
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
        "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval"
    };
    private const string EnabledSettingKey = "tasks.automation.caseControl.enabled";
    private const string InactiveDaysSettingKey = "tasks.automation.caseControl.inactiveDays";
    private const string LimitSettingKey = "tasks.automation.caseControl.limit";
    private const string DefaultAssigneeSettingKey = "tasks.automation.caseControl.defaultAssigneeUserId";
    private const string EscalationDaysSettingKey = "tasks.automation.caseControl.escalationDays";
    private const string EscalationUserSettingKey = "tasks.automation.caseControl.escalationUserId";
    private const string RunLocalTimeSettingKey = "tasks.automation.caseControl.runLocalTime";
    private const string ScheduleTimeZoneSettingKey = "tasks.automation.caseControl.scheduleTimeZone";
    private const string AutomationTag = "автоконтроль";
    private const string AutomationActorName = "Автоконтроль дел";
    private const string InactivityEscalationEventType = "caseInactivityControlEscalation";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TaskJournalAutomationSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

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
        var scheduleCheckSeconds = Math.Clamp(
            _configuration.GetValue("TaskJournal:Automation:ScheduleCheckSeconds", 300),
            60,
            3600);
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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(scheduleCheckSeconds));
        DateOnly? lastRunLocalDate = null;
        string? lastScheduleSignature = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                var settings = await LoadSettingsAsync(context, stoppingToken);
                var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.ScheduleTimeZoneId);
                var scheduleSignature =
                    $"{settings.RunLocalTime.ToString("HH:mm", CultureInfo.InvariantCulture)}|{timeZone.Id}";
                if (!string.Equals(lastScheduleSignature, scheduleSignature, StringComparison.Ordinal))
                {
                    lastRunLocalDate = await LoadLastRunLocalDateAsync(timeZone, stoppingToken);
                    lastScheduleSignature = scheduleSignature;
                }

                var schedule = CaseInactivityAutomationSchedulePolicy.Evaluate(
                    DateTime.UtcNow,
                    settings.RunLocalTime,
                    timeZone,
                    lastRunLocalDate);
                if (settings.Enabled && schedule.ShouldRun)
                {
                    await RunOnceAsync(stoppingToken);
                    lastRunLocalDate = schedule.LocalDate;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось проверить расписание автоконтроля дел.");
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

    private async Task<DateOnly?> LoadLastRunLocalDateAsync(
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken)
    {
        if (_jobRunState is null)
        {
            return null;
        }

        var states = await _jobRunState.GetLastRunsAsync(
            [SystemJobKeys.CaseInactivityControl],
            cancellationToken);
        return states.TryGetValue(SystemJobKeys.CaseInactivityControl, out var state)
            ? CaseInactivityAutomationSchedulePolicy.ResolveLastRunLocalDate(state.LastSuccessAt, timeZone)
            : null;
    }

    public async Task<TaskJournalAutomationSchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            return await RunOnceCoreAsync(cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<TaskJournalAutomationSchedulerResult> RunOnceCoreAsync(CancellationToken cancellationToken)
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
            var responsibleAvailability = await CaseInactivityResponsibleAvailabilityService
                .GetAvailabilityAsync(context, now, cancellationToken);
            var vacationUserIds = responsibleAvailability.UsersOnVacation;
            var escalationResult = await EscalateExistingAutomationTasksAsync(
                context,
                settings,
                escalationUser,
                vacationUserIds,
                now,
                cancellationToken);
            var escalatedTasks = escalationResult.EscalatedTasks;
            var cutoff = now.AddDays(-settings.InactiveDays);
            var candidates = await context.ClientCases
                .Include(item => item.Client)
                .Include(item => item.LegalEntity)
                .Where(clientCase =>
                    ((clientCase.ClientId.HasValue && clientCase.Client != null) ||
                     (clientCase.LegalEntityId.HasValue && clientCase.LegalEntity != null)) &&
                    !CaseInactivityControlCyclePolicy.ExcludedCaseStatuses.Contains(clientCase.Status) &&
                    !vacationUserIds.Contains(
                        (clientCase.ResponsibleUserId ??
                         (clientCase.Client != null ? clientCase.Client.ResponsibleUserId : null) ??
                         (clientCase.LegalEntity != null ? clientCase.LegalEntity.ResponsibleUserId : null)) ?? 0))
                .Select(clientCase => new
                {
                    Case = clientCase,
                    ActiveTaskCount = context.TaskJournalItems.Count(task =>
                        task.ClientCaseId == clientCase.Id &&
                        ActiveTaskStatuses.Contains(task.Status)),
                    LastTaskAt = context.TaskJournalItems
                        .IgnoreQueryFilters()
                        .Where(task =>
                            task.ClientCaseId == clientCase.Id &&
                            !task.TagsJson.Contains(AutomationTag))
                        .Max(task => (DateTime?)(task.UpdatedAt ?? task.CreatedAt)),
                    LastTaskCommentAt = context.TaskJournalComments
                        .Where(comment =>
                            comment.TaskJournalItem.ClientCaseId == clientCase.Id &&
                            !comment.TaskJournalItem.TagsJson.Contains(AutomationTag))
                        .Max(comment => (DateTime?)(comment.UpdatedAt ?? comment.CreatedAt)),
                    LastControlCycleEndedAt = context.TaskJournalItems
                        .IgnoreQueryFilters()
                        .Where(task =>
                            task.ClientCaseId == clientCase.Id &&
                            task.TagsJson.Contains(AutomationTag) &&
                            ((task.Status == "done" || task.Status == "cancelled") ||
                             EF.Property<bool>(task, "IsDeleted")))
                        .Max(task =>
                            EF.Property<DateTime?>(task, "DeletedAt") ??
                            task.UpdatedAt ??
                            (DateTime?)task.CreatedAt),
                    LastDocumentAt = context.ClientCaseDocuments
                        .Where(document => document.ClientCaseId == clientCase.Id)
                        .Max(document => (DateTime?)document.UploadedAt),
                    LastCourtEventAt = context.ClientCaseCourtEvents
                        .Where(courtEvent => courtEvent.ClientCaseId == clientCase.Id)
                        .Max(courtEvent => (DateTime?)(courtEvent.UpdatedAt ?? courtEvent.CreatedAt)),
                })
                .Where(item => item.ActiveTaskCount == 0
                    && (item.LastTaskAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.LastTaskCommentAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.LastControlCycleEndedAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.LastDocumentAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.LastCourtEventAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.Case.LastViewedAt ?? item.Case.CreatedAt) <= cutoff
                    && (item.Case.ActivityConfirmedAt ?? item.Case.CreatedAt) <= cutoff
                    && item.Case.InactivityRemindersEnabled)
                .OrderBy(item => item.LastControlCycleEndedAt ?? item.LastTaskAt ?? item.LastTaskCommentAt ?? item.LastDocumentAt ?? item.LastCourtEventAt ?? item.Case.CreatedAt)
                .ThenBy(item => item.Case.Client != null
                    ? item.Case.Client.Name
                    : item.Case.LegalEntity != null
                        ? item.Case.LegalEntity.ShortName
                        : string.Empty)
                .ThenBy(item => item.Case.Title)
                .Take(settings.Limit)
                .ToListAsync(cancellationToken);

            var candidateCaseIds = candidates
                .Select(candidate => candidate.Case.Id)
                .ToArray();
            var activeTaskCaseIds = candidateCaseIds.Length == 0
                ? []
                : await context.TaskJournalItems
                    .AsNoTracking()
                    .Where(task =>
                        task.ClientCaseId.HasValue
                        && candidateCaseIds.Contains(task.ClientCaseId.Value)
                        && ActiveTaskStatuses.Contains(task.Status))
                    .Select(task => task.ClientCaseId!.Value)
                    .Distinct()
                    .ToHashSetAsync(cancellationToken);

            var responsibleUserIds = candidates
                .Select(candidate => ResolveCaseResponsibleUserId(candidate.Case))
                .Where(userId =>
                    userId.HasValue &&
                    userId.Value > 0 &&
                    (defaultAssignee is null || userId.Value != defaultAssignee.Id))
                .Select(userId => userId!.Value)
                .Distinct()
                .ToArray();
            var responsibleUsers = responsibleUserIds.Length == 0
                ? new Dictionary<int, User>()
                : await context.Users
                    .AsNoTracking()
                    .Where(user =>
                        responsibleUserIds.Contains(user.Id) &&
                        user.IsActive &&
                        user.IsApproved)
                    .ToDictionaryAsync(user => user.Id, cancellationToken);

            var assigneeUserIds = responsibleUsers.Keys
                .Append(defaultAssignee?.Id ?? 0)
                .Where(userId => userId > 0)
                .Distinct()
                .ToArray();
            var assigneeTimeZoneIds = assigneeUserIds
                .Where(responsibleAvailability.TimeZoneIds.ContainsKey)
                .ToDictionary(
                    userId => userId,
                    userId => responsibleAvailability.TimeZoneIds[userId]);
            var defaultTimeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);

            var created = 0;
            var createdTasks = new List<CreatedAutomationTask>();
            foreach (var candidate in candidates)
            {
                var owner = ResolveCaseOwner(candidate.Case);
                if (owner is null)
                {
                    continue;
                }

                if (activeTaskCaseIds.Contains(candidate.Case.Id))
                {
                    continue;
                }

                var assignee = ResolveAutomationAssignee(candidate.Case, responsibleUsers, defaultAssignee);
                if (assignee is null)
                {
                    continue;
                }

                var assigneeTimeZone = assigneeTimeZoneIds.TryGetValue(assignee.Id, out var assigneeTimeZoneId)
                    ? CalendarTimeZoneHelper.ResolveTimeZone(assigneeTimeZoneId)
                    : defaultTimeZone;
                var lastActivity = ResolveLastCaseActivity(
                    candidate.Case,
                    candidate.LastTaskAt,
                    candidate.LastTaskCommentAt,
                    candidate.LastDocumentAt,
                    candidate.LastCourtEventAt);
                var transition = CaseInactivityControlStateMachine.Evaluate(
                    new CaseInactivityControlStateInput(
                        now,
                        lastActivity,
                        null,
                        settings.InactiveDays,
                        settings.EscalationDays));
                if (transition.Transition != CaseInactivityControlTransition.FirstControlDue)
                {
                    continue;
                }

                var daysWithoutActiveTask = transition.DaysWithoutActivity;
                var task = BuildAutomationTask(
                    candidate.Case,
                    owner,
                    assignee,
                    assigneeTimeZone,
                    now,
                    settings,
                    escalationUser,
                    candidate.LastTaskAt,
                    candidate.LastTaskCommentAt,
                    candidate.LastDocumentAt,
                    candidate.LastCourtEventAt);
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

            if (escalationResult.StateChangedCount > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            if (escalatedTasks.Count > 0)
            {
                await NotifyEscalatedAutomationTasksAsync(notificationService, escalatedTasks, cancellationToken);
            }

            var processed = created + escalatedTasks.Count;
            var result = new TaskJournalAutomationSchedulerResult(created, candidates.Count - created, true, null, escalatedTasks.Count);
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordSuccessAsync(
                    SystemJobKeys.CaseInactivityControl,
                    startedAt,
                    processed,
                    result.SkippedCount,
                    processed > 0
                        ? $"Автоконтроль неактивных дел обработал записей: {processed}. Новых задач: {created}, повторных эскалаций: {escalatedTasks.Count}."
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
                item.Key == EscalationUserSettingKey ||
                item.Key == RunLocalTimeSettingKey ||
                item.Key == ScheduleTimeZoneSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var inactiveDays = Math.Clamp(GetInteger(values, InactiveDaysSettingKey, "TaskJournal:Automation:InactiveDays", 14), 1, 365);
        return new TaskJournalAutomationSettings(
            GetBoolean(values, EnabledSettingKey, "TaskJournal:Automation:Enabled", true),
            inactiveDays,
            Math.Clamp(GetInteger(values, LimitSettingKey, "TaskJournal:Automation:Limit", 25), 1, 100),
            GetNullableInteger(values, DefaultAssigneeSettingKey, "TaskJournal:Automation:DefaultAssigneeUserId"),
            Math.Clamp(GetInteger(values, EscalationDaysSettingKey, "TaskJournal:Automation:EscalationDays", 30), inactiveDays, 365),
            GetNullableInteger(values, EscalationUserSettingKey, "TaskJournal:Automation:EscalationUserId"),
            GetRunLocalTime(values),
            GetString(
                values,
                ScheduleTimeZoneSettingKey,
                "TaskJournal:Automation:ScheduleTimeZone",
                CalendarTimeZoneHelper.DefaultTimeZoneId));
    }

    private async Task<User?> ResolveConfiguredAutomationUserAsync(
        PaymentContext context,
        int? configuredUserId,
        CancellationToken cancellationToken)
    {
        if (!configuredUserId.HasValue || configuredUserId.Value <= 0)
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

    private static User? ResolveAutomationAssignee(
        ClientCase clientCase,
        IReadOnlyDictionary<int, User> responsibleUsers,
        User? fallbackAssignee)
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

        return responsibleUsers.GetValueOrDefault(responsibleUserId.Value);
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

        var alreadyAdded = task.Participants.Any(participant =>
            participant.UserId == escalationUser.Id &&
            string.Equals(participant.Role, "observer", StringComparison.OrdinalIgnoreCase));
        if (alreadyAdded)
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
        TimeZoneInfo assigneeTimeZone,
        DateTime now,
        TaskJournalAutomationSettings settings,
        User? escalationUser,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
    {
        var assigneeName = DisplayName(assignee);
        var lastActivity = ResolveLastCaseActivity(
            clientCase,
            lastTaskAt,
            lastTaskCommentAt,
            lastDocumentAt,
            lastCourtEventAt);
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
            DueAt = BuildNextMorningDueAt(now, assigneeTimeZone),
            PlannedDurationMinutes = 60,
            Type = "control",
            Priority = ResolveAutomationPriority(daysWithoutActiveTask, settings.InactiveDays, settings.EscalationDays),
            Status = "new",
            Result = null,
            RemindersJson = SerializeList(["before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp"]),
            TagsJson = SerializeList([AutomationTag]),
            InactivityControlCycleNumber = 1,
            InactivityControlNextCheckAt = now.AddDays(CaseInactivityControlStateMachine.FirstRepeatOffsetDays),
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

    private static DateTime BuildNextMorningDueAt(DateTime nowUtc, TimeZoneInfo assigneeTimeZone)
    {
        var localNow = CalendarTimeZoneHelper.ToUserLocal(nowUtc, assigneeTimeZone);
        var localDueAt = localNow.Date.AddDays(1).AddHours(9);
        return CalendarTimeZoneHelper.FromUserLocalToUtc(localDueAt, assigneeTimeZone);
    }

    private static string ResolveRepeatEscalationPriority(
        string currentPriority,
        int level,
        int daysWithoutActiveTask,
        TaskJournalAutomationSettings settings)
    {
        if (level >= 2 || daysWithoutActiveTask >= settings.EscalationDays)
        {
            return "urgent";
        }

        return string.Equals(currentPriority, "urgent", StringComparison.OrdinalIgnoreCase)
            ? currentPriority
            : "high";
    }

    private static string BuildRepeatEscalationComment(
        int level,
        int offsetDays,
        int daysWithoutActiveTask,
        bool escalatedToObserver)
    {
        var observerText = escalatedToObserver
            ? " Руководитель добавлен наблюдателем по задаче."
            : string.Empty;
        return $"Автоконтроль: повторная эскалация уровня {level} через {offsetDays} дн. после создания контрольной задачи. По делу нет нового движения {daysWithoutActiveTask} дн.{observerText}";
    }

    private static bool IsEscalationComment(string? metadataJson, int offsetDays)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            return root.TryGetProperty("eventType", out var eventType) &&
                   string.Equals(eventType.GetString(), InactivityEscalationEventType, StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("offsetDays", out var storedOffsetDays) &&
                   storedOffsetDays.ValueKind == JsonValueKind.Number &&
                   storedOffsetDays.GetInt32() == offsetDays;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<ExistingAutomationEscalationResult> EscalateExistingAutomationTasksAsync(
        PaymentContext context,
        TaskJournalAutomationSettings settings,
        User? escalationUser,
        IReadOnlySet<int> vacationUserIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var automationTasks = await context.TaskJournalItems
            .Include(task => task.ClientCase)
            .Include(task => task.Participants)
            .AsSplitQuery()
            .Where(task =>
                task.ClientCaseId.HasValue &&
                task.ClientCase != null &&
                task.TagsJson.Contains(AutomationTag) &&
                ActiveTaskStatuses.Contains(task.Status) &&
                !CaseInactivityControlCyclePolicy.ExcludedCaseStatuses.Contains(task.ClientCase.Status) &&
                !vacationUserIds.Contains(
                    (task.ClientCase.ResponsibleUserId ??
                     (task.ClientCase.Client != null ? task.ClientCase.Client.ResponsibleUserId : null) ??
                     (task.ClientCase.LegalEntity != null ? task.ClientCase.LegalEntity.ResponsibleUserId : null)) ?? 0) &&
                task.ClientCase.InactivityRemindersEnabled &&
                (task.InactivityControlCycleNumber == null ||
                 (task.InactivityControlNextCheckAt.HasValue &&
                  task.InactivityControlNextCheckAt <= now)))
            .OrderBy(task => task.CreatedAt)
            .Take(settings.Limit)
            .ToListAsync(cancellationToken);

        var legacyTaskIds = automationTasks
            .Where(task => !task.InactivityControlCycleNumber.HasValue)
            .Select(task => task.Id)
            .ToArray();
        var legacyCompletedLevels = new Dictionary<int, int>();
        if (legacyTaskIds.Length > 0)
        {
            var legacyEscalationMetadata = await context.TaskJournalComments
                .AsNoTracking()
                .Where(comment =>
                    legacyTaskIds.Contains(comment.TaskJournalItemId) &&
                    comment.MetadataJson.Contains(InactivityEscalationEventType))
                .Select(comment => new
                {
                    comment.TaskJournalItemId,
                    comment.MetadataJson,
                })
                .ToListAsync(cancellationToken);
            legacyCompletedLevels = legacyEscalationMetadata
                .GroupBy(comment => comment.TaskJournalItemId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Any(comment => IsEscalationComment(
                        comment.MetadataJson,
                        CaseInactivityControlStateMachine.SecondRepeatOffsetDays))
                        ? 2
                        : group.Any(comment => IsEscalationComment(
                            comment.MetadataJson,
                            CaseInactivityControlStateMachine.FirstRepeatOffsetDays))
                            ? 1
                            : 0);
        }

        var caseIds = automationTasks
            .Select(task => task.ClientCaseId!.Value)
            .Distinct()
            .ToArray();
        var signalsByCase = caseIds.Length == 0
            ? new Dictionary<int, ExistingAutomationCaseSignals>()
            : await context.ClientCases
                .AsNoTracking()
                .Where(clientCase => caseIds.Contains(clientCase.Id))
                .Select(clientCase => new ExistingAutomationCaseSignals(
                    clientCase.Id,
                    context.TaskJournalItems.Count(other =>
                        other.ClientCaseId == clientCase.Id &&
                        ActiveTaskStatuses.Contains(other.Status)),
                    context.TaskJournalItems
                        .IgnoreQueryFilters()
                        .Where(other =>
                            other.ClientCaseId == clientCase.Id &&
                            !other.TagsJson.Contains(AutomationTag))
                        .Max(other => (DateTime?)(other.UpdatedAt ?? other.CreatedAt)),
                    context.TaskJournalComments
                        .Where(comment =>
                            comment.TaskJournalItem.ClientCaseId == clientCase.Id &&
                            !comment.TaskJournalItem.TagsJson.Contains(AutomationTag))
                        .Max(comment => (DateTime?)(comment.UpdatedAt ?? comment.CreatedAt)),
                    context.ClientCaseDocuments
                        .Where(document => document.ClientCaseId == clientCase.Id)
                        .Max(document => (DateTime?)document.UploadedAt),
                    context.ClientCaseCourtEvents
                        .Where(courtEvent => courtEvent.ClientCaseId == clientCase.Id)
                        .Max(courtEvent => (DateTime?)(courtEvent.UpdatedAt ?? courtEvent.CreatedAt))))
                .ToDictionaryAsync(item => item.ClientCaseId, cancellationToken);

        var escalatedTasks = new List<EscalatedAutomationTask>();
        var stateChangedCount = 0;
        foreach (var task in automationTasks)
        {
            if (!task.InactivityControlCycleNumber.HasValue)
            {
                var completedRepeatLevel = legacyCompletedLevels.GetValueOrDefault(task.Id);
                task.InactivityControlCycleNumber = completedRepeatLevel + 1;
                task.InactivityControlNextCheckAt = completedRepeatLevel switch
                {
                    0 => task.CreatedAt.AddDays(CaseInactivityControlStateMachine.FirstRepeatOffsetDays),
                    1 => task.CreatedAt.AddDays(CaseInactivityControlStateMachine.SecondRepeatOffsetDays),
                    _ => null,
                };
                stateChangedCount += 1;
            }

            if (task.ClientCase is null ||
                !task.AssigneeUserId.HasValue ||
                !signalsByCase.TryGetValue(task.ClientCase.Id, out var signals))
            {
                continue;
            }

            var lastActivity = ResolveLastCaseActivity(
                task.ClientCase,
                signals.LastNonAutomationTaskAt,
                signals.LastTaskCommentAt,
                signals.LastDocumentAt,
                signals.LastCourtEventAt);
            var transition = CaseInactivityControlStateMachine.Evaluate(
                new CaseInactivityControlStateInput(
                    now,
                    lastActivity,
                    task.CreatedAt,
                    settings.InactiveDays,
                    settings.EscalationDays,
                    HasOtherActiveTask: signals.ActiveTaskCount > 1,
                    CompletedRepeatLevel: Math.Max(0, task.InactivityControlCycleNumber.Value - 1),
                    NextCheckAt: task.InactivityControlNextCheckAt));
            if (transition.Transition == CaseInactivityControlTransition.Reset)
            {
                if (task.InactivityControlNextCheckAt.HasValue)
                {
                    task.InactivityControlNextCheckAt = null;
                    task.UpdatedAt = now;
                    stateChangedCount += 1;
                }

                continue;
            }

            if (transition.Transition is not (
                    CaseInactivityControlTransition.RepeatDue or
                    CaseInactivityControlTransition.CriticalEscalationDue) ||
                !transition.RepeatOffsetDays.HasValue)
            {
                continue;
            }

            var daysWithoutActiveTask = transition.DaysWithoutActivity;
            var level = transition.RepeatLevel;
            var dueOffset = transition.RepeatOffsetDays.Value;
            var shouldEscalateToObserver = transition.IsCritical;
            if (shouldEscalateToObserver)
            {
                AddEscalationObserver(task, escalationUser, task.AssigneeUserId.Value, now);
            }

            task.Priority = ResolveRepeatEscalationPriority(task.Priority, level, daysWithoutActiveTask, settings);
            task.InactivityControlCycleNumber = level + 1;
            task.InactivityControlNextCheckAt = level < 2
                ? task.CreatedAt.AddDays(CaseInactivityControlStateMachine.SecondRepeatOffsetDays)
                : null;
            task.UpdatedAt = now;
            task.Comments.Add(new TaskJournalComment
            {
                AuthorUserId = null,
                AuthorName = AutomationActorName,
                Kind = "system",
                Text = BuildRepeatEscalationComment(level, dueOffset, daysWithoutActiveTask, shouldEscalateToObserver),
                MetadataJson = JsonSerializer.Serialize(new
                {
                    eventType = InactivityEscalationEventType,
                    level,
                    offsetDays = dueOffset,
                    daysWithoutActiveTask,
                    escalatedToObserver = shouldEscalateToObserver && escalationUser is not null && escalationUser.Id != task.AssigneeUserId.Value,
                }, JsonOptions),
                CreatedAt = now,
                UpdatedAt = now,
            });

            AddRepeatEscalationAuditEvent(context, task, level, dueOffset, daysWithoutActiveTask, now);
            escalatedTasks.Add(new EscalatedAutomationTask(task, level, dueOffset, daysWithoutActiveTask, shouldEscalateToObserver));
            stateChangedCount += 1;
        }

        return new ExistingAutomationEscalationResult(escalatedTasks, stateChangedCount);
    }

    private static void AddRepeatEscalationAuditEvent(
        PaymentContext context,
        TaskJournalItem task,
        int level,
        int offsetDays,
        int daysWithoutActiveTask,
        DateTime now)
    {
        if (!task.ClientCaseId.HasValue)
        {
            return;
        }

        context.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case",
            ObjectId = task.ClientCaseId.Value,
            Action = "Automation",
            Description = $"Автоконтроль повторно эскалировал задачу \"{task.Title}\" для {task.AssigneeName}: нет движения {daysWithoutActiveTask} дн.",
            ActorUserName = AutomationActorName,
            CreatedAt = now,
            MetadataJson = JsonSerializer.Serialize(new
            {
                eventType = InactivityEscalationEventType,
                taskId = task.Id,
                taskTitle = task.Title,
                assigneeUserId = task.AssigneeUserId,
                assigneeName = task.AssigneeName,
                level,
                offsetDays,
                daysWithoutActiveTask,
            }, JsonOptions),
        });
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

    private static DateTime ResolveLastCaseActivity(
        ClientCase clientCase,
        DateTime? lastTaskAt,
        DateTime? lastTaskCommentAt,
        DateTime? lastDocumentAt,
        DateTime? lastCourtEventAt)
        => CaseMeaningfulActivityResolver.Resolve(
            new CaseMeaningfulActivitySignals(
                clientCase.CreatedAt,
                LastTaskAt: lastTaskAt,
                LastTaskCommentAt: lastTaskCommentAt,
                LastDocumentAt: lastDocumentAt,
                LastCourtEventAt: lastCourtEventAt,
                LastResponsibleViewAt: clientCase.LastViewedAt,
                LastActivityConfirmationAt: clientCase.ActivityConfirmedAt))
            .At;

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

    private async Task NotifyEscalatedAutomationTasksAsync(
        INotificationService? notificationService,
        IReadOnlyList<EscalatedAutomationTask> escalatedTasks,
        CancellationToken cancellationToken)
    {
        if (notificationService is null || escalatedTasks.Count == 0)
        {
            return;
        }

        foreach (var item in escalatedTasks)
        {
            try
            {
                await notificationService.NotifyTaskChangedAsync(
                    item.Task,
                    [$"Повторная эскалация автоконтроля уровня {item.Level}"],
                    AutomationActorName,
                    actorUserId: 0,
                    comment: $"По делу нет нового движения {item.DaysWithoutActiveTask} дн.; контрольная задача открыта {item.OffsetDays} дн.",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to notify about repeated case-control escalation for task {TaskId}.", item.Task.Id);
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

    private TimeOnly GetRunLocalTime(IReadOnlyDictionary<string, string> settings)
    {
        var value = GetString(
            settings,
            RunLocalTimeSettingKey,
            "TaskJournal:Automation:RunLocalTime",
            "09:00");
        return TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : CaseInactivityAutomationSchedulePolicy.DefaultRunLocalTime;
    }

    private string GetString(
        IReadOnlyDictionary<string, string> settings,
        string key,
        string configPath,
        string fallback)
        => settings.TryGetValue(key, out var settingValue) && !string.IsNullOrWhiteSpace(settingValue)
            ? settingValue.Trim()
            : _configuration.GetValue(configPath, fallback) ?? fallback;

    private sealed record TaskJournalAutomationSettings(
        bool Enabled,
        int InactiveDays,
        int Limit,
        int? DefaultAssigneeUserId,
        int EscalationDays,
        int? EscalationUserId,
        TimeOnly RunLocalTime,
        string ScheduleTimeZoneId);

    private sealed record CreatedAutomationTask(TaskJournalItem Task, int DaysWithoutActiveTask, bool IsEscalated);

    private sealed record EscalatedAutomationTask(
        TaskJournalItem Task,
        int Level,
        int OffsetDays,
        int DaysWithoutActiveTask,
        bool EscalatedToObserver);

    private sealed record ExistingAutomationEscalationResult(
        IReadOnlyList<EscalatedAutomationTask> EscalatedTasks,
        int StateChangedCount);

    private sealed record ExistingAutomationCaseSignals(
        int ClientCaseId,
        int ActiveTaskCount,
        DateTime? LastNonAutomationTaskAt,
        DateTime? LastTaskCommentAt,
        DateTime? LastDocumentAt,
        DateTime? LastCourtEventAt);

    private sealed record CaseOwnerBinding(int? ClientId, int? LegalEntityId, string Name);
}

public sealed record TaskJournalAutomationSchedulerResult(
    int CreatedCount,
    int SkippedCount,
    bool Enabled,
    string? Error,
    int EscalatedCount = 0)
{
    public static TaskJournalAutomationSchedulerResult Disabled { get; } = new(0, 0, false, null);
}

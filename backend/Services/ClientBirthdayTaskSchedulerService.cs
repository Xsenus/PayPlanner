using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class ClientBirthdayTaskSchedulerService : BackgroundService
{
    public const string EnabledSettingKey = "tasks.automation.birthdays.enabled";
    public const string EmployeeEnabledSettingKey = "tasks.automation.birthdays.employees.enabled";
    public const string LeadDaysSettingKey = "tasks.automation.birthdays.leadDays";
    public const string ClientUseSelectedAssigneeSettingKey = "tasks.automation.birthdays.clients.useSelectedAssignee";
    public const string AssigneeUserIdSettingKey = "tasks.automation.birthdays.assigneeUserId";
    public const string AutomationTag = "auto-birthday";
    private const string ScheduleTimeZoneSettingKey = "tasks.automation.caseControl.scheduleTimeZone";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientBirthdayTaskSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public ClientBirthdayTaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ClientBirthdayTaskSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
        _jobRunState = jobRunState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkMinutes = Math.Clamp(
            _configuration.GetValue("TaskJournal:BirthdayAutomation:CheckMinutes", 60),
            5,
            1440);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(checkMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось проверить дни рождения клиентов и сотрудников.");
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

    public async Task<ClientBirthdayTaskSchedulerResult> RunOnceAsync(
        DateTime? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        var startedAt = DateTime.UtcNow;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var settings = await LoadSettingsAsync(context, cancellationToken);
            if (!settings.Enabled)
            {
                await RecordSuccessAsync(startedAt, 0, 0, "Автоматические задачи ко дням рождения выключены.", cancellationToken);
                return ClientBirthdayTaskSchedulerResult.Disabled;
            }

            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.TimeZoneId);
            var now = CalendarTimeZoneHelper.NormalizeUtcInstant(nowUtc ?? DateTime.UtcNow);
            var localToday = CalendarTimeZoneHelper.ToUserLocal(now, timeZone).Date;
            var windowEnd = localToday.AddDays(settings.LeadDays);
            var relevantMonths = Enumerable.Range(0, settings.LeadDays + 1)
                .Select(offset => localToday.AddDays(offset).Month)
                .Distinct()
                .ToArray();
            var needsSelectedAssignee = settings.EmployeesEnabled ||
                (settings.ClientsEnabled && settings.ClientUseSelectedAssignee);
            var selectedAssignee = needsSelectedAssignee && settings.SelectedAssigneeUserId > 0
                ? await context.Users.AsNoTracking()
                    .Where(user => user.Id == settings.SelectedAssigneeUserId && user.IsActive && user.IsApproved)
                    .Select(user => new BirthdayAssignee(user.Id, user.FullName ?? user.Email, user.Email))
                    .SingleOrDefaultAsync(cancellationToken)
                : null;
            if (needsSelectedAssignee && selectedAssignee is null)
            {
                throw new InvalidOperationException("Не назначен активный ответственный за поздравления.");
            }
            List<BirthdayClient> clients = settings.ClientsEnabled
                ? await context.Clients.AsNoTracking()
                .Where(client =>
                    client.IsActive &&
                    client.BirthDate.HasValue &&
                    relevantMonths.Contains(client.BirthDate.Value.Month) &&
                    (settings.ClientUseSelectedAssignee ||
                     (client.ResponsibleUserId.HasValue && client.ResponsibleUser != null &&
                      client.ResponsibleUser.IsActive && client.ResponsibleUser.IsApproved)))
                .Select(client => new BirthdayClient(
                    client.Id,
                    client.Name,
                    client.BirthDate!.Value,
                    client.ResponsibleUserId ?? 0,
                    client.ResponsibleUser != null ? client.ResponsibleUser.FullName ?? client.ResponsibleUser.Email : "",
                    client.ResponsibleUser != null ? client.ResponsibleUser.Email : ""))
                .ToListAsync(cancellationToken)
                : [];

            var candidates = clients
                .Select(client => new BirthdayCandidate(client, ResolveNextBirthday(client.BirthDate, localToday)))
                .Where(item => item.Birthday >= localToday && item.Birthday <= windowEnd)
                .ToList();
            var employees = settings.EmployeesEnabled && selectedAssignee is not null
                ? await context.Users.AsNoTracking()
                    .Where(user => user.IsEmployee && user.IsActive && user.IsApproved && user.DateOfBirth.HasValue &&
                        relevantMonths.Contains(user.DateOfBirth.Value.Month))
                    .Select(user => new BirthdayEmployee(user.Id, user.FullName ?? user.Email, user.DateOfBirth!.Value))
                    .ToListAsync(cancellationToken)
                : [];
            var employeeCandidates = employees
                .Select(employee => new BirthdayEmployeeCandidate(employee, ResolveNextBirthday(employee.BirthDate, localToday)))
                .Where(item => item.Birthday >= localToday && item.Birthday <= windowEnd)
                .ToList();
            var existingTags = candidates.Count == 0 && employeeCandidates.Count == 0
                ? []
                : await context.TaskJournalItems
                    .AsNoTracking()
                    .Where(task => task.CreatedAt >= now.AddDays(-430) && task.TagsJson.Contains(AutomationTag))
                    .Select(task => task.TagsJson)
                    .ToListAsync(cancellationToken);
            var existingMarkers = existingTags
                .SelectMany(DeserializeTags)
                .Where(tag => tag.StartsWith("birthday:", StringComparison.Ordinal) ||
                    tag.StartsWith("employee-birthday:", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            var created = 0;
            foreach (var candidate in candidates)
            {
                var marker = BuildMarker(candidate.Client.Id, candidate.Birthday.Year);
                if (existingMarkers.Contains(marker))
                {
                    continue;
                }

                var assignee = settings.ClientUseSelectedAssignee
                    ? selectedAssignee
                    : new BirthdayAssignee(candidate.Client.ResponsibleUserId, candidate.Client.ResponsibleName, candidate.Client.ResponsibleEmail);
                if (assignee is null)
                {
                    continue;
                }
                context.TaskJournalItems.Add(BuildTask(candidate, assignee, marker, timeZone, now));
                existingMarkers.Add(marker);
                created += 1;
            }
            foreach (var candidate in employeeCandidates)
            {
                var marker = $"employee-birthday:{candidate.Employee.Id}:{candidate.Birthday.Year}";
                if (existingMarkers.Contains(marker) || selectedAssignee is null)
                {
                    continue;
                }
                context.TaskJournalItems.Add(BuildEmployeeTask(candidate, selectedAssignee, marker, timeZone, now));
                existingMarkers.Add(marker);
                created += 1;
            }

            if (created > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                var realtimePublisher = scope.ServiceProvider.GetService<IRealtimeNotificationPublisher>();
                await (realtimePublisher ?? NoOpRealtimeNotificationPublisher.Instance)
                    .PublishTaskJournalChangedAsync(cancellationToken);
            }

            var skipped = Math.Max(0, candidates.Count + employeeCandidates.Count - created);
            await RecordSuccessAsync(
                startedAt,
                created,
                skipped,
                created > 0
                    ? $"Создано задач для поздравления клиентов и сотрудников: {created}."
                    : "Ближайшие дни рождения проверены, новые задачи не требуются.",
                cancellationToken);
            return new ClientBirthdayTaskSchedulerResult(created, skipped, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось создать задачи для поздравления клиентов и сотрудников.");
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordFailureAsync(
                    SystemJobKeys.ClientBirthdayTasks,
                    startedAt,
                    ex,
                    "Проверка дней рождения завершилась с ошибкой.",
                    cancellationToken);
            }

            return new ClientBirthdayTaskSchedulerResult(0, 0, true, ex.Message);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<BirthdaySettings> LoadSettingsAsync(PaymentContext context, CancellationToken cancellationToken)
    {
        var values = await context.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == EnabledSettingKey || item.Key == EmployeeEnabledSettingKey ||
                item.Key == LeadDaysSettingKey || item.Key == ClientUseSelectedAssigneeSettingKey ||
                item.Key == AssigneeUserIdSettingKey || item.Key == ScheduleTimeZoneSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        var enabled = values.TryGetValue(EnabledSettingKey, out var rawEnabled) && bool.TryParse(rawEnabled, out var parsedEnabled)
            ? parsedEnabled
            : _configuration.GetValue("TaskJournal:BirthdayAutomation:Enabled", false);
        return new BirthdaySettings(
            enabled,
            bool.TryParse(values.GetValueOrDefault(EmployeeEnabledSettingKey), out var employeesEnabled) && employeesEnabled,
            Math.Clamp(int.TryParse(values.GetValueOrDefault(LeadDaysSettingKey), out var leadDays) ? leadDays : 7, 0, 60),
            bool.TryParse(values.GetValueOrDefault(ClientUseSelectedAssigneeSettingKey), out var clientSelected) && clientSelected,
            int.TryParse(values.GetValueOrDefault(AssigneeUserIdSettingKey), out var assigneeId) ? assigneeId : 0,
            values.GetValueOrDefault(ScheduleTimeZoneSettingKey) ?? CalendarTimeZoneHelper.DefaultTimeZoneId);
    }

    private Task RecordSuccessAsync(DateTime startedAt, int processed, int skipped, string summary, CancellationToken cancellationToken)
        => _jobRunState?.RecordSuccessAsync(
            SystemJobKeys.ClientBirthdayTasks,
            startedAt,
            processed,
            skipped,
            summary,
            cancellationToken) ?? Task.CompletedTask;

    private static DateTime ResolveNextBirthday(DateTime birthDate, DateTime localToday)
    {
        var day = birthDate.Day;
        var month = birthDate.Month;
        var occurrence = BuildBirthdayOccurrence(localToday.Year, month, day);
        return occurrence < localToday
            ? BuildBirthdayOccurrence(localToday.Year + 1, month, day)
            : occurrence;
    }

    private static DateTime BuildBirthdayOccurrence(int year, int month, int day)
        => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private static string BuildMarker(int clientId, int year) => $"birthday:{clientId}:{year}";

    private static TaskJournalItem BuildTask(BirthdayCandidate candidate, BirthdayAssignee assignee, string marker, TimeZoneInfo timeZone, DateTime now)
    {
        var client = candidate.Client;
        var assigneeName = string.IsNullOrWhiteSpace(assignee.Name) ? assignee.Email : assignee.Name;
        var dueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(candidate.Birthday.AddHours(10), timeZone);
        var task = new TaskJournalItem
        {
            ClientId = client.Id,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            ObjectName = client.Name,
            Title = $"Поздравить с днём рождения: {client.Name}",
            Description = $"Связаться с клиентом и поздравить с днём рождения {candidate.Birthday:dd.MM.yyyy}.",
            AssigneeUserId = assignee.Id,
            AssigneeName = assigneeName,
            AuthorUserId = assignee.Id,
            AuthorName = "Система: дни рождения",
            DueAt = dueAt,
            PlannedDurationMinutes = 30,
            Type = "task",
            Priority = "normal",
            Status = "new",
            RemindersJson = SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp"]),
            TagsJson = SerializeList([AutomationTag, marker]),
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
            CreatedAt = now,
            UpdatedAt = now,
        });
        task.Comments.Add(new TaskJournalComment
        {
            AuthorUserId = null,
            AuthorName = "Система: дни рождения",
            Kind = "system",
            Text = "Задача создана автоматически по дате рождения из карточки клиента.",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return task;
    }

    private static TaskJournalItem BuildEmployeeTask(BirthdayEmployeeCandidate candidate, BirthdayAssignee assignee, string marker, TimeZoneInfo timeZone, DateTime now)
    {
        var task = new TaskJournalItem
        {
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            ObjectName = candidate.Employee.Name,
            Title = $"Поздравить коллегу с днём рождения: {candidate.Employee.Name}",
            Description = $"Поздравить сотрудника {candidate.Employee.Name} с днём рождения {candidate.Birthday:dd.MM.yyyy}.",
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.Name,
            AuthorUserId = assignee.Id,
            AuthorName = "Система: дни рождения",
            DueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(candidate.Birthday.AddHours(10), timeZone),
            PlannedDurationMinutes = 30,
            Type = "task",
            Priority = "normal",
            Status = "new",
            RemindersJson = SerializeList(["before7d", "before3d", "before1d", "atDue", "dailyOverdue"]),
            ChannelsJson = SerializeList(["inApp"]),
            TagsJson = SerializeList([AutomationTag, marker]),
            CreatedAt = now,
            UpdatedAt = now,
        };
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = assignee.Id,
            Role = "assignee",
            DisplayName = assignee.Name,
            Email = assignee.Email,
            SortOrder = 0,
            ApprovalStatus = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        });
        task.Comments.Add(new TaskJournalComment
        {
            AuthorName = "Система: дни рождения",
            Kind = "system",
            Text = "Задача создана автоматически по дате рождения из карточки сотрудника.",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return task;
    }

    private static string SerializeList(IEnumerable<string> values)
        => JsonSerializer.Serialize(values.ToList(), JsonOptions);

    private static IReadOnlyList<string> DeserializeTags(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record BirthdaySettings(bool ClientsEnabled, bool EmployeesEnabled, int LeadDays, bool ClientUseSelectedAssignee, int SelectedAssigneeUserId, string TimeZoneId)
    {
        public bool Enabled => ClientsEnabled || EmployeesEnabled;
    }
    private sealed record BirthdayAssignee(int Id, string Name, string Email);
    private sealed record BirthdayClient(int Id, string Name, DateTime BirthDate, int ResponsibleUserId, string ResponsibleName, string ResponsibleEmail);
    private sealed record BirthdayCandidate(BirthdayClient Client, DateTime Birthday);
    private sealed record BirthdayEmployee(int Id, string Name, DateTime BirthDate);
    private sealed record BirthdayEmployeeCandidate(BirthdayEmployee Employee, DateTime Birthday);
}

public sealed record ClientBirthdayTaskSchedulerResult(int CreatedCount, int SkippedCount, bool Enabled, string? Error)
{
    public static ClientBirthdayTaskSchedulerResult Disabled { get; } = new(0, 0, false, null);
}

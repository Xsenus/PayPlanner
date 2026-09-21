using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class ClientBirthdayTaskSchedulerService : BackgroundService
{
    public const string EnabledSettingKey = "tasks.automation.birthdays.enabled";
    public const string AutomationTag = "auto-birthday";
    private const string ScheduleTimeZoneSettingKey = "tasks.automation.caseControl.scheduleTimeZone";
    private const int LeadDays = 7;
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
        DateOnly? lastCheckedLocalDate = null;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(checkMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
                var settings = await LoadSettingsAsync(context, stoppingToken);
                var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.TimeZoneId);
                var localDate = DateOnly.FromDateTime(CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone));
                if (settings.Enabled && lastCheckedLocalDate != localDate)
                {
                    var result = await RunOnceAsync(DateTime.UtcNow, stoppingToken);
                    if (result.Error is null)
                    {
                        lastCheckedLocalDate = localDate;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось проверить дни рождения клиентов.");
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
            var windowEnd = localToday.AddDays(LeadDays);
            var clients = await context.Clients
                .AsNoTracking()
                .Where(client =>
                    client.IsActive &&
                    client.BirthDate.HasValue &&
                    client.ResponsibleUserId.HasValue &&
                    client.ResponsibleUser != null &&
                    client.ResponsibleUser.IsActive &&
                    client.ResponsibleUser.IsApproved)
                .Select(client => new BirthdayClient(
                    client.Id,
                    client.Name,
                    client.BirthDate!.Value,
                    client.ResponsibleUserId!.Value,
                    client.ResponsibleUser!.FullName ?? client.ResponsibleUser.Email,
                    client.ResponsibleUser.Email))
                .ToListAsync(cancellationToken);

            var candidates = clients
                .Select(client => new BirthdayCandidate(client, ResolveNextBirthday(client.BirthDate, localToday)))
                .Where(item => item.Birthday >= localToday && item.Birthday <= windowEnd)
                .ToList();
            var candidateClientIds = candidates.Select(item => item.Client.Id).Distinct().ToArray();
            var existingTags = candidateClientIds.Length == 0
                ? []
                : await context.TaskJournalItems
                    .AsNoTracking()
                    .Where(task => task.ClientId.HasValue && candidateClientIds.Contains(task.ClientId.Value) && task.TagsJson.Contains(AutomationTag))
                    .Select(task => task.TagsJson)
                    .ToListAsync(cancellationToken);
            var existingMarkers = existingTags
                .SelectMany(DeserializeTags)
                .Where(tag => tag.StartsWith("birthday:", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            var created = 0;
            foreach (var candidate in candidates)
            {
                var marker = BuildMarker(candidate.Client.Id, candidate.Birthday.Year);
                if (existingMarkers.Contains(marker))
                {
                    continue;
                }

                context.TaskJournalItems.Add(BuildTask(candidate, marker, timeZone, now));
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

            var skipped = Math.Max(0, candidates.Count - created);
            await RecordSuccessAsync(
                startedAt,
                created,
                skipped,
                created > 0
                    ? $"Создано задач для поздравления клиентов: {created}."
                    : "Ближайшие дни рождения проверены, новые задачи не требуются.",
                cancellationToken);
            return new ClientBirthdayTaskSchedulerResult(created, skipped, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось создать задачи для поздравления клиентов.");
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordFailureAsync(
                    SystemJobKeys.ClientBirthdayTasks,
                    startedAt,
                    ex,
                    "Проверка дней рождения клиентов завершилась с ошибкой.",
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
            .Where(item => item.Key == EnabledSettingKey || item.Key == ScheduleTimeZoneSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        var enabled = values.TryGetValue(EnabledSettingKey, out var rawEnabled) && bool.TryParse(rawEnabled, out var parsedEnabled)
            ? parsedEnabled
            : _configuration.GetValue("TaskJournal:BirthdayAutomation:Enabled", false);
        return new BirthdaySettings(
            enabled,
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

    private static TaskJournalItem BuildTask(BirthdayCandidate candidate, string marker, TimeZoneInfo timeZone, DateTime now)
    {
        var client = candidate.Client;
        var assigneeName = string.IsNullOrWhiteSpace(client.ResponsibleName) ? client.ResponsibleEmail : client.ResponsibleName;
        var dueAt = CalendarTimeZoneHelper.FromUserLocalToUtc(candidate.Birthday.AddHours(10), timeZone);
        var task = new TaskJournalItem
        {
            ClientId = client.Id,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            ObjectName = client.Name,
            Title = $"Поздравить с днём рождения: {client.Name}",
            Description = $"Связаться с клиентом и поздравить с днём рождения {candidate.Birthday:dd.MM.yyyy}.",
            AssigneeUserId = client.ResponsibleUserId,
            AssigneeName = assigneeName,
            AuthorUserId = client.ResponsibleUserId,
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
            UserId = client.ResponsibleUserId,
            Role = "assignee",
            DisplayName = assigneeName,
            Email = client.ResponsibleEmail,
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

    private sealed record BirthdaySettings(bool Enabled, string TimeZoneId);
    private sealed record BirthdayClient(int Id, string Name, DateTime BirthDate, int ResponsibleUserId, string ResponsibleName, string ResponsibleEmail);
    private sealed record BirthdayCandidate(BirthdayClient Client, DateTime Birthday);
}

public sealed record ClientBirthdayTaskSchedulerResult(int CreatedCount, int SkippedCount, bool Enabled, string? Error)
{
    public static ClientBirthdayTaskSchedulerResult Disabled { get; } = new(0, 0, false, null);
}

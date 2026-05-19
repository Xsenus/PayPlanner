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
    private static readonly ClientCaseStatus[] FinishedCaseStatuses =
    [
        ClientCaseStatus.Closed,
        ClientCaseStatus.Lost,
    ];

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

            var defaultAssignee = await ResolveConfiguredAutomationUserAsync(context, settings.DefaultAssigneeUserId, cancellationToken);

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

                var task = BuildAutomationTask(candidate.Case, owner, assignee, candidate.LastTaskAt, now);
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

    private static TaskJournalItem BuildAutomationTask(
        ClientCase clientCase,
        CaseOwnerBinding owner,
        User assignee,
        DateTime? lastTaskAt,
        DateTime now)
    {
        var assigneeName = DisplayName(assignee);
        var lastActivity = ResolveLastCaseActivity(clientCase, lastTaskAt);
        var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((now - lastActivity).TotalDays));
        var task = new TaskJournalItem
        {
            ClientId = owner.ClientId,
            LegalEntityId = owner.LegalEntityId,
            ClientCaseId = clientCase.Id,
            ObjectName = BuildObjectName(owner.Name, clientCase.Title),
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

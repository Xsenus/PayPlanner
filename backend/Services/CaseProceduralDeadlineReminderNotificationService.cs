using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class CaseProceduralDeadlineReminderNotificationService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    private const int MaxWarningWindowDays = 90;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CaseProceduralDeadlineReminderNotificationService> _logger;

    public CaseProceduralDeadlineReminderNotificationService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CaseProceduralDeadlineReminderNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CaseProceduralDeadlineReminderNotificationService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsEnabled())
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Case procedural deadline reminder tick failed.");
                }
            }

            try
            {
                await Task.Delay(ResolveInterval(), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }

        _logger.LogInformation("CaseProceduralDeadlineReminderNotificationService stopped.");
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notificationService = scope.ServiceProvider.GetService<INotificationService>();
        if (notificationService is null)
        {
            return 0;
        }

        var today = DateTime.UtcNow.Date;
        var maxDueDate = today.AddDays(MaxWarningWindowDays + 1);
        var batchSize = ResolveBatchSize();
        var pageSize = Math.Clamp(batchSize * 2, 50, 500);
        var candidates = db.ClientCaseProceduralDeadlines
            .AsNoTracking()
            .Include(deadline => deadline.ClientCase)
            .ThenInclude(clientCase => clientCase.Client)
            .Include(deadline => deadline.ClientCase)
            .ThenInclude(clientCase => clientCase.LegalEntity)
            .Where(deadline =>
                deadline.Status == "active"
                && deadline.DueAt >= today
                && deadline.DueAt < maxDueDate
                && deadline.WarningDaysBefore >= 0)
            .OrderBy(deadline => deadline.DueAt)
            .ThenBy(deadline => deadline.Id);
        var created = 0;
        DateTime? lastDueAt = null;
        var lastId = 0;
        while (created < batchSize)
        {
            var pageQuery = lastDueAt.HasValue
                ? candidates.Where(deadline => deadline.DueAt > lastDueAt.Value
                    || deadline.DueAt == lastDueAt.Value && deadline.Id > lastId)
                : candidates;
            var page = await pageQuery.Take(pageSize).ToListAsync(cancellationToken);
            var eligible = page.Where(deadline => !ClientCaseStageClassifier.IsPretrial(deadline.ClientCase)
                && deadline.DueAt.Date <= today.AddDays(
                    Math.Clamp(deadline.WarningDaysBefore, 0, MaxWarningWindowDays))).ToArray();
            var recipientsByCaseId = await ResolveCaseNotificationRecipientIdsAsync(
                db,
                eligible.Select(deadline => deadline.ClientCaseId).Distinct().ToArray(),
                cancellationToken);
            var pageReminders = new List<CaseProceduralDeadlineReminderBatchItem>(eligible.Length);
            foreach (var deadline in eligible)
            {
                if (!recipientsByCaseId.TryGetValue(deadline.ClientCaseId, out var recipients)
                    || recipients.Count == 0) continue;
                pageReminders.Add(new CaseProceduralDeadlineReminderBatchItem(
                    deadline.ClientCase, deadline, recipients));
            }
            for (var offset = 0; offset < pageReminders.Count && created < batchSize;)
            {
                var count = Math.Min(batchSize - created, pageReminders.Count - offset);
                created += await notificationService.NotifyCaseProceduralDeadlineRemindersAsync(
                    pageReminders.GetRange(offset, count), cancellationToken);
                offset += count;
            }
            if (page.Count < pageSize || created >= batchSize) break;
            lastDueAt = page[^1].DueAt;
            lastId = page[^1].Id;
        }

        return created;
    }

    private bool IsEnabled()
        => _configuration.GetValue("Notifications:CaseProceduralDeadlineReminders:Enabled", true);

    private TimeSpan ResolveInterval()
    {
        var seconds = Math.Clamp(
            _configuration.GetValue("Notifications:CaseProceduralDeadlineReminders:PollSeconds", (int)DefaultInterval.TotalSeconds),
            60,
            86400);
        return TimeSpan.FromSeconds(seconds);
    }

    private int ResolveBatchSize()
        => Math.Clamp(_configuration.GetValue("Notifications:CaseProceduralDeadlineReminders:BatchSize", 100), 1, 500);

    private static async Task<IReadOnlyDictionary<int, IReadOnlyCollection<int>>> ResolveCaseNotificationRecipientIdsAsync(
        PaymentContext db,
        IReadOnlyCollection<int> clientCaseIds,
        CancellationToken cancellationToken)
    {
        if (clientCaseIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyCollection<int>>();
        }

        var caseCandidates = await db.ClientCases
            .AsNoTracking()
            .Where(clientCase => clientCaseIds.Contains(clientCase.Id))
            .Select(clientCase => new CaseRecipientCandidate(
                clientCase.Id,
                clientCase.IsHiddenFromExecutor,
                clientCase.ResponsibleUserId,
                clientCase.Client != null ? clientCase.Client.ResponsibleUserId : null,
                clientCase.LegalEntity != null ? clientCase.LegalEntity.ResponsibleUserId : null))
            .ToListAsync(cancellationToken);

        var candidates = caseCandidates
            .SelectMany(item => new[]
            {
                item.ResponsibleUserId,
                item.ClientResponsibleUserId,
                item.LegalEntityResponsibleUserId,
            }.Where(userId => userId.HasValue && userId.Value > 0)
                .Select(userId => new CaseRecipient(
                    item.ClientCaseId,
                    item.IsHiddenFromExecutor,
                    userId!.Value)))
            .Distinct()
            .ToArray();
        var userIds = candidates
            .Select(candidate => candidate.UserId)
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
        {
            return new Dictionary<int, IReadOnlyCollection<int>>();
        }

        var allowedUsers = await db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Cases
                        && permission.CanView)))
            .Select(user => new
            {
                user.Id,
                CanSeeHiddenCases = user.Role!.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Cases
                        && permission.CanView
                        && permission.ViewScope == DataVisibilityScope.All),
            })
            .ToListAsync(cancellationToken);

        var accessByUserId = allowedUsers.ToDictionary(user => user.Id, user => user.CanSeeHiddenCases);
        return candidates
            .Where(candidate => accessByUserId.TryGetValue(candidate.UserId, out var canSeeHiddenCases)
                && (!candidate.IsHiddenFromExecutor || canSeeHiddenCases))
            .GroupBy(candidate => candidate.ClientCaseId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<int>)group
                    .Select(candidate => candidate.UserId)
                    .Distinct()
                    .ToArray());
    }

    private sealed record CaseRecipientCandidate(
        int ClientCaseId,
        bool IsHiddenFromExecutor,
        int? ResponsibleUserId,
        int? ClientResponsibleUserId,
        int? LegalEntityResponsibleUserId);

    private sealed record CaseRecipient(int ClientCaseId, bool IsHiddenFromExecutor, int UserId);
}

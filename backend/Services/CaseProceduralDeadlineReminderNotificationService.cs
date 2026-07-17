using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
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
        var deadlines = await db.ClientCaseProceduralDeadlines
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
            .ThenBy(deadline => deadline.Id)
            .Take(ResolveBatchSize())
            .ToListAsync(cancellationToken);
        deadlines = deadlines
            .Where(deadline => !ClientCaseStageClassifier.IsPretrial(deadline.ClientCase))
            .Where(deadline => deadline.DueAt.Date <= today.AddDays(
                Math.Clamp(deadline.WarningDaysBefore, 0, MaxWarningWindowDays)))
            .ToList();

        var recipientsByCaseId = await ResolveCaseNotificationRecipientIdsAsync(
            db,
            deadlines.Select(deadline => deadline.ClientCaseId).Distinct().ToArray(),
            cancellationToken);

        var notified = 0;
        foreach (var deadline in deadlines)
        {
            if (!recipientsByCaseId.TryGetValue(deadline.ClientCaseId, out var recipients)
                || recipients.Count == 0)
            {
                continue;
            }

            await notificationService.NotifyCaseProceduralDeadlineAsync(
                deadline.ClientCase,
                deadline,
                "reminder",
                "CRM",
                actorUserId: 0,
                recipientUserIds: recipients,
                cancellationToken: cancellationToken);
            notified++;
        }

        return notified;
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
                .Select(userId => new CaseRecipient(item.ClientCaseId, userId!.Value)))
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

        var allowedUserIds = await db.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id)
                && user.IsActive
                && user.IsApproved
                && user.Role != null
                && (user.Role.Name == "admin"
                    || user.Role.Permissions.Any(permission =>
                        permission.Section == RolePermissionCatalog.Cases
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, List<int>>();
        foreach (var userId in allowedUserIds)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))],
                authenticationType: "CaseProceduralDeadlineReminderAccessCheck"));
            var visibility = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases, cancellationToken);
            if (visibility is null)
            {
                continue;
            }

            var candidateCaseIds = candidates
                .Where(candidate => candidate.UserId == userId)
                .Select(candidate => candidate.ClientCaseId)
                .Distinct()
                .ToArray();
            var visibleCaseIds = visibility.SeesAll
                ? candidateCaseIds
                : await db.ClientCases
                    .AsNoTracking()
                    .Where(clientCase => candidateCaseIds.Contains(clientCase.Id))
                    .ApplyClientCaseVisibility(visibility)
                    .Select(clientCase => clientCase.Id)
                    .ToArrayAsync(cancellationToken);
            foreach (var clientCaseId in visibleCaseIds)
            {
                if (!result.TryGetValue(clientCaseId, out var recipients))
                {
                    recipients = [];
                    result[clientCaseId] = recipients;
                }

                recipients.Add(userId);
            }
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyCollection<int>)item.Value.ToArray());
    }

    private sealed record CaseRecipientCandidate(
        int ClientCaseId,
        int? ResponsibleUserId,
        int? ClientResponsibleUserId,
        int? LegalEntityResponsibleUserId);

    private sealed record CaseRecipient(int ClientCaseId, int UserId);
}

using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class ContractExpirationNotificationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int ContractBatchSize = 128;
    private const int UserBatchSize = 512;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContractExpirationNotificationService> _logger;
    private readonly TimeProvider _clock;

    public ContractExpirationNotificationService(
        IServiceScopeFactory scopeFactory,
        ILogger<ContractExpirationNotificationService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ContractExpirationNotificationService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contract expiration notification tick failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }

        _logger.LogInformation("ContractExpirationNotificationService stopped.");
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notifications = scope.ServiceProvider.GetService<INotificationService>();
        if (notifications is null) return 0;
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var utcToday = nowUtc.Date;
        var earliest = utcToday > DateTime.MinValue.Date ? utcToday.AddDays(-1) : utcToday;
        DateTime? latestExclusive = (DateTime.MaxValue.Date - utcToday).Days >= 32 ? utcToday.AddDays(32) : null;
        ContractExpirationRecipientSettings? settings = null;
        var lastContractId = 0;
        var notified = 0;
        while (true)
        {
            var contracts = await db.Contracts.AsNoTracking()
                .Where(contract => contract.Id > lastContractId && !contract.IsOpenEnded && contract.ValidUntil.HasValue
                    && contract.ValidUntil.Value >= earliest && (!latestExclusive.HasValue || contract.ValidUntil.Value < latestExclusive.Value)
                    && contract.Status != ContractStatus.Terminated && contract.Status != ContractStatus.Cancelled)
                .OrderBy(contract => contract.Id).Take(ContractBatchSize)
                .Select(contract => new Contract
                {
                    Id = contract.Id,
                    Number = contract.Number,
                    Title = contract.Title,
                    Date = contract.Date,
                    StartDate = contract.StartDate,
                    ValidUntil = contract.ValidUntil,
                    Amount = contract.Amount,
                    Status = contract.Status,
                    IsOpenEnded = contract.IsOpenEnded
                }).ToListAsync(ct);
            if (contracts.Count == 0) break;
            lastContractId = contracts[^1].Id;
            settings ??= await ContractExpirationRecipientSettings.LoadAsync(db, ct);
            var contractIds = contracts.Select(contract => contract.Id).ToArray();
            var responsibleByContract = contractIds.ToDictionary(id => id, _ => new HashSet<int>());
            var managerByContract = contractIds.ToDictionary(id => id, _ => new HashSet<int>());
            IReadOnlyList<ContractExpirationDepartmentRoute> routes = settings.DepartmentManagersEnabled
                ? await ContractExpirationNotificationPolicy.DepartmentRoutesAsync(db, settings.DepartmentManagers, ct)
                : [];
            var managerByDepartmentId = routes.ToDictionary(route => route.DepartmentId, route => route.ManagerUserId);
            var managerByLegacyName = routes.Where(route => route.LegacyNameUnique)
                .ToDictionary(route => route.Name.Trim(), route => route.ManagerUserId, StringComparer.OrdinalIgnoreCase);
            var sourceUsers = from candidate in ContractExpirationNotificationPolicy.ResponsibleCandidates(db, contractIds)
                              join user in db.Users.AsNoTracking() on candidate.UserId equals user.Id
                              select new { candidate.ContractId, candidate.UserId, user.DepartmentId, user.Department };
            await foreach (var user in sourceUsers.Distinct().AsAsyncEnumerable().WithCancellation(ct))
            {
                responsibleByContract[user.ContractId].Add(user.UserId);
                int managerId;
                var mapped = user.DepartmentId.HasValue
                    ? managerByDepartmentId.TryGetValue(user.DepartmentId.Value, out managerId)
                    : managerByLegacyName.TryGetValue(string.IsNullOrWhiteSpace(user.Department) ? string.Empty : user.Department.Trim(), out managerId);
                if (mapped) managerByContract[user.ContractId].Add(managerId);
            }
            var roleIdSet = settings.RoleIds.ToHashSet();
            var explicitUserIdSet = settings.UserIds.ToHashSet();
            var sourceUserIds = responsibleByContract.Values.SelectMany(ids => ids)
                .Concat(managerByContract.Values.SelectMany(ids => ids)).Distinct().ToArray();
            if (sourceUserIds.Length != 0 || settings.RoleIds.Length != 0 || settings.UserIds.Length != 0)
            {
                var lastUserId = 0;
                var notifiedThisBatch = new HashSet<int>();
                while (true)
                {
                    var users = await db.Users.AsNoTracking()
                        .Where(user => user.Id > lastUserId && user.IsActive && user.IsApproved && user.Role != null
                            && (sourceUserIds.Contains(user.Id) || settings.UserIds.Contains(user.Id) || settings.RoleIds.Contains(user.RoleId))
                            && (user.Role.Name == "admin" || user.Role.Permissions.Any(permission =>
                                permission.Section == RolePermissionCatalog.Contracts && permission.CanView)))
                        .OrderBy(user => user.Id).Take(UserBatchSize).Select(user => new { user.Id, user.RoleId }).ToListAsync(ct);
                    if (users.Count == 0) break;
                    lastUserId = users[^1].Id;
                    var userIds = users.Select(user => user.Id).ToArray();
                    var zones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(db, userIds, ct);
                    var localDays = zones.ToDictionary(pair => pair.Key,
                        pair => CalendarTimeZoneHelper.ToUserLocal(nowUtc, pair.Value).Date);
                    var globalRecipients = users.Where(user => roleIdSet.Contains(user.RoleId) || explicitUserIdSet.Contains(user.Id))
                        .Select(user => user.Id).ToHashSet();
                    foreach (var contract in contracts)
                    {
                        var recipients = userIds.Where(id => globalRecipients.Contains(id)
                            || responsibleByContract[contract.Id].Contains(id) || managerByContract[contract.Id].Contains(id));
                        foreach (var group in recipients.GroupBy(id => (contract.ValidUntil!.Value.Date - localDays[id]).Days))
                        {
                            if (!ContractExpirationNotificationPolicy.StageForDays(group.Key).HasValue) continue;
                            await notifications.NotifyContractExpiringAsync(contract, group.Key, group.ToArray(), ct);
                            notifiedThisBatch.Add(contract.Id);
                        }
                    }
                    if (users.Count < UserBatchSize) break;
                }
                notified += notifiedThisBatch.Count;
            }
            if (contracts.Count < ContractBatchSize) break;
        }
        return notified;
    }
}

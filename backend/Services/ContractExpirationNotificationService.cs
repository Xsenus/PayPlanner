using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class ContractExpirationNotificationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private const int ExpirationWindowDays = 7;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContractExpirationNotificationService> _logger;

    public ContractExpirationNotificationService(
        IServiceScopeFactory scopeFactory,
        ILogger<ContractExpirationNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
        var notificationService = scope.ServiceProvider.GetService<INotificationService>();
        if (notificationService is null)
        {
            return 0;
        }

        var today = DateTime.UtcNow.Date;
        var maxDate = today.AddDays(ExpirationWindowDays);
        var contracts = await db.Contracts
            .AsNoTracking()
            .Where(contract =>
                !contract.IsOpenEnded
                && contract.ValidUntil.HasValue
                && contract.ValidUntil.Value.Date >= today
                && contract.ValidUntil.Value.Date <= maxDate
                && contract.Status != ContractStatus.Terminated
                && contract.Status != ContractStatus.Cancelled)
            .ToListAsync(ct);

        var recipientsByContractId = await ResolveContractNotificationRecipientsAsync(
            db,
            contracts.Select(contract => contract.Id).ToArray(),
            ct);

        var notified = 0;
        foreach (var contract in contracts)
        {
            if (!recipientsByContractId.TryGetValue(contract.Id, out var recipients)
                || recipients.Count == 0)
            {
                continue;
            }

            var daysLeft = Math.Max((contract.ValidUntil!.Value.Date - today).Days, 0);
            await notificationService.NotifyContractExpiringAsync(contract, daysLeft, recipients, ct);
            notified++;
        }

        return notified;
    }

    private static async Task<IReadOnlyDictionary<int, IReadOnlyCollection<int>>> ResolveContractNotificationRecipientsAsync(
        PaymentContext db,
        IReadOnlyCollection<int> contractIds,
        CancellationToken ct)
    {
        if (contractIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyCollection<int>>();
        }

        var clientCandidates = db.ClientContracts
            .AsNoTracking()
            .Where(link => contractIds.Contains(link.ContractId)
                && link.Client.ResponsibleUserId.HasValue)
            .Select(link => new { link.ContractId, UserId = link.Client.ResponsibleUserId!.Value });
        var legalEntityCandidates = db.LegalEntityContracts
            .AsNoTracking()
            .Where(link => contractIds.Contains(link.ContractId)
                && link.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => new { link.ContractId, UserId = link.LegalEntity.ResponsibleUserId!.Value });
        var invoiceCandidates =
            from link in db.ContractInvoices.AsNoTracking()
            join responsible in db.PaymentResponsibles.AsNoTracking()
                on link.InvoiceId equals responsible.PaymentId
            where contractIds.Contains(link.ContractId)
            select new { link.ContractId, responsible.UserId };
        var linkedPaymentCandidates =
            from link in db.ContractPayments.AsNoTracking()
            join responsible in db.PaymentResponsibles.AsNoTracking()
                on link.PaymentId equals responsible.PaymentId
            where contractIds.Contains(link.ContractId)
            select new { link.ContractId, responsible.UserId };
        var actCandidates = db.ContractActs
            .AsNoTracking()
            .Where(link => contractIds.Contains(link.ContractId)
                && link.Act.ResponsibleId.HasValue)
            .Select(link => new { link.ContractId, UserId = link.Act.ResponsibleId!.Value });
        var directPaymentCandidates =
            from payment in db.Payments.AsNoTracking()
            join responsible in db.PaymentResponsibles.AsNoTracking()
                on payment.Id equals responsible.PaymentId
            where payment.ContractId.HasValue && contractIds.Contains(payment.ContractId.Value)
            select new { ContractId = payment.ContractId!.Value, responsible.UserId };

        var candidates = await clientCandidates
            .Concat(legalEntityCandidates)
            .Concat(invoiceCandidates)
            .Concat(linkedPaymentCandidates)
            .Concat(actCandidates)
            .Concat(directPaymentCandidates)
            .Distinct()
            .ToListAsync(ct);

        var userIds = candidates
            .Select(candidate => candidate.UserId)
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
                        permission.Section == RolePermissionCatalog.Contracts
                        && permission.CanView)))
            .Select(user => user.Id)
            .ToListAsync(ct);

        var allowedUserIdSet = allowedUserIds.ToHashSet();
        return candidates
            .Where(candidate => candidate.UserId > 0 && allowedUserIdSet.Contains(candidate.UserId))
            .GroupBy(candidate => candidate.ContractId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<int>)group
                    .Select(candidate => candidate.UserId)
                    .Distinct()
                    .ToArray());
    }
}

using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
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

        var notified = 0;
        foreach (var contract in contracts)
        {
            var recipients = await ResolveContractNotificationRecipientsAsync(db, contract.Id, ct);
            if (recipients.Count == 0)
            {
                continue;
            }

            var daysLeft = Math.Max((contract.ValidUntil!.Value.Date - today).Days, 0);
            await notificationService.NotifyContractExpiringAsync(contract, daysLeft, recipients, ct);
            notified++;
        }

        return notified;
    }

    private static async Task<IReadOnlyCollection<int>> ResolveContractNotificationRecipientsAsync(
        PaymentContext db,
        int contractId,
        CancellationToken ct)
    {
        var candidateUserIds = new List<int>();
        candidateUserIds.AddRange(await db.ClientContracts
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Client.ResponsibleUserId.HasValue)
            .Select(link => link.Client.ResponsibleUserId!.Value)
            .ToListAsync(ct));
        candidateUserIds.AddRange(await db.LegalEntityContracts
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.LegalEntity.ResponsibleUserId.HasValue)
            .Select(link => link.LegalEntity.ResponsibleUserId!.Value)
            .ToListAsync(ct));
        candidateUserIds.AddRange(await db.ContractInvoices
            .AsNoTracking()
            .Where(link => link.ContractId == contractId)
            .SelectMany(link => link.Invoice.Responsibles.Select(responsible => responsible.UserId))
            .ToListAsync(ct));
        candidateUserIds.AddRange(await db.ContractPayments
            .AsNoTracking()
            .Where(link => link.ContractId == contractId)
            .SelectMany(link => link.Payment.Responsibles.Select(responsible => responsible.UserId))
            .ToListAsync(ct));
        candidateUserIds.AddRange(await db.ContractActs
            .AsNoTracking()
            .Where(link => link.ContractId == contractId
                && link.Act.ResponsibleId.HasValue)
            .Select(link => link.Act.ResponsibleId!.Value)
            .ToListAsync(ct));
        candidateUserIds.AddRange(await db.Payments
            .AsNoTracking()
            .Where(payment => payment.ContractId == contractId)
            .SelectMany(payment => payment.Responsibles.Select(responsible => responsible.UserId))
            .ToListAsync(ct));

        var userIds = candidateUserIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
        {
            return Array.Empty<int>();
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

        var result = new List<int>();
        foreach (var userId in allowedUserIds)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)) },
                authenticationType: "ContractExpirationAccessCheck"));
            var visibility = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Contracts, ct);
            if (visibility is null)
            {
                continue;
            }

            var canOpenContract = await db.Contracts
                .AsNoTracking()
                .Where(contract => contract.Id == contractId)
                .ApplyContractVisibility(visibility)
                .AnyAsync(ct);
            if (canOpenContract)
            {
                result.Add(userId);
            }
        }

        return result;
    }
}

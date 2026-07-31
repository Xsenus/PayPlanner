using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public interface IExternalWorkOrderApprovalNotifier
{
    Task NotifyDecisionAsync(
        ExternalWorkOrderApprovalRequest request,
        ClientCaseWorkOrder workOrder,
        CancellationToken ct = default);

    Task NotifyExpiredAsync(
        ExternalWorkOrderApprovalRequest request,
        ClientCaseWorkOrder workOrder,
        CancellationToken ct = default);
}

public sealed class ExternalWorkOrderApprovalNotifier(
    PaymentContext db,
    INotificationService notifications) : IExternalWorkOrderApprovalNotifier
{
    public Task NotifyDecisionAsync(
        ExternalWorkOrderApprovalRequest request,
        ClientCaseWorkOrder workOrder,
        CancellationToken ct = default)
    {
        var approved = string.Equals(request.Decision, "approved", StringComparison.Ordinal);
        return NotifyRecipientsAsync(
            request,
            workOrder,
            UserNotificationTypes.ExternalWorkOrderDecision,
            $"external-work-order:{request.Id}:decision:{request.Decision}",
            approved ? "Клиент согласовал заказ-наряд" : "Заказ-наряд возвращён с замечаниями",
            approved
                ? $"По делу «{workOrder.ClientCase.Title}» согласована версия {request.Version} заказ-наряда «{workOrder.Title}»."
                : $"По делу «{workOrder.ClientCase.Title}» клиент вернул версию {request.Version} заказ-наряда «{workOrder.Title}» с замечаниями.",
            approved ? "emerald" : "orange",
            ct);
    }

    public Task NotifyExpiredAsync(
        ExternalWorkOrderApprovalRequest request,
        ClientCaseWorkOrder workOrder,
        CancellationToken ct = default)
        => NotifyRecipientsAsync(
            request,
            workOrder,
            UserNotificationTypes.ExternalWorkOrderExpired,
            $"external-work-order:{request.Id}:expired",
            "Истёк срок согласования заказ-наряда",
            $"По делу «{workOrder.ClientCase.Title}» клиент не принял решение по версии {request.Version} заказ-наряда «{workOrder.Title}».",
            "amber",
            ct);

    private async Task NotifyRecipientsAsync(
        ExternalWorkOrderApprovalRequest request,
        ClientCaseWorkOrder workOrder,
        string type,
        string eventKey,
        string title,
        string body,
        string tone,
        CancellationToken ct)
    {
        var candidateIds = new[]
            {
                request.CreatedByUserId,
                workOrder.ApprovalRequestedByUserId,
                workOrder.ClientCase.ResponsibleUserId,
            }
            .Where(static id => id.HasValue && id.Value > 0)
            .Select(static id => id!.Value)
            .Distinct()
            .ToArray();
        if (candidateIds.Length == 0)
        {
            return;
        }

        var activeRecipientIds = await db.Users
            .AsNoTracking()
            .Where(user => candidateIds.Contains(user.Id) && user.IsActive)
            .Select(user => user.Id)
            .ToArrayAsync(ct);

        foreach (var recipientId in activeRecipientIds)
        {
            await notifications.NotifyAsync(
                new NotificationEventEnvelope(
                    recipientId,
                    type,
                    eventKey,
                    title,
                    body,
                    "case",
                    workOrder.ClientCaseId,
                    null,
                    tone,
                    new
                    {
                        caseId = workOrder.ClientCaseId,
                        workOrderId = workOrder.Id,
                        requestId = request.Id,
                        request.Version,
                        request.Status,
                    }),
                ct);
        }
    }
}

public sealed class ExternalWorkOrderApprovalExpirationService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
    ILogger<ExternalWorkOrderApprovalExpirationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "External work order expiration processing failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!featureFlags.CurrentValue.Legal.ExternalWorkOrderApproval)
        {
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<IExternalWorkOrderApprovalNotifier>();
        var now = DateTime.UtcNow;
        var requests = await db.ExternalWorkOrderApprovalRequests
            .Include(item => item.WorkOrder)
                .ThenInclude(item => item.ClientCase)
            .Where(item => item.ExpiresAt <= now
                && ((item.Status == "pending" || item.Status == "opened")
                    || (item.Status == "expired" && item.ExpirationNotificationProcessedAt == null)))
            .OrderBy(item => item.ExpiresAt)
            .ThenBy(item => item.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var request in requests)
        {
            if (request.Status is "pending" or "opened")
            {
                request.Status = "expired";
                await db.SaveChangesAsync(ct);
            }

            try
            {
                await notifier.NotifyExpiredAsync(request, request.WorkOrder, ct);
                request.ExpirationNotificationProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                processed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "External work order expiration notification failed for request {RequestId}.", request.Id);
            }
        }

        return processed;
    }
}

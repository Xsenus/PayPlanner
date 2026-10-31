using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Creates one recipient's notification for a claimed, committed overdue event.</summary>
public interface IDurablePaymentOverdueNotificationService
{
    Task NotifyOverdueForEventAsync(PaymentOverdueNotificationOutbox item, string claimToken, int recipientUserId, CancellationToken ct = default);
}

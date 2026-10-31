using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Notifications;

public sealed partial class NotificationService
{
    public async Task NotifyOverdueForEventAsync(PaymentOverdueNotificationOutbox item, string claimToken, int recipientUserId, CancellationToken ct = default)
    {
        UserNotification? notification;
        var invoice = item.DocumentKind == DocumentKind.Invoice;
        var type = invoice ? UserNotificationTypes.InvoiceChanged : UserNotificationTypes.PaymentChanged;
        // Serialize persistence with payment writers and permission changes, but
        // release every database lock before invoking an external channel.
        await using (var lease = await BalanceApplicationLease.BeginAsync(_context, recipientUserId, ct))
        {
            await lease.LockPaymentAsync(item.PaymentId, ct);
            if (_context.Database.IsNpgsql())
                await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"PaymentOverdueNotificationOutboxes\" WHERE \"Id\" = {item.Id} FOR UPDATE NOWAIT", ct);
            if (!await IsOverdueRecipientCurrentAsync(item, claimToken, recipientUserId, type, ct)) return;
            var payment = await PaymentOverdueNotificationQueue.CurrentPayments(_context, item).SingleOrDefaultAsync(ct);
            if (payment is null) return;
            var key = $"{item.EventKey}:recipient:{recipientUserId}";
            notification = await _context.UserNotifications.AsNoTracking()
                .SingleOrDefaultAsync(row => row.RecipientUserId == recipientUserId && row.EventKey == key, ct);
            if (notification is null)
            {
                string[] changes = invoice ? ["Статус оплаты счёта: Просрочен"] : ["Статус: Ожидается -> Просрочен"];
                var message = await RenderTemplateAsync(invoice ? NotificationTemplateKeys.InvoiceChanged : NotificationTemplateKeys.PaymentChanged,
                    invoice ? $"Изменен счет №{BuildInvoiceNumber(payment)}" : $"Изменен платеж: {BuildPaymentTitle(payment)}",
                    invoice ? BuildInvoiceChangedBody(payment, "CRM", changes) : BuildPaymentChangedBody(payment, "CRM", changes),
                    invoice ? BuildInvoiceTemplateValues(payment, "CRM", changes) : BuildPaymentTemplateValues(payment, "CRM", changes),
                    purpose: NotificationPurposes.ManualTest, cancellationToken: ct);
                notification = new UserNotification
                {
                    RecipientUserId = recipientUserId,
                    Type = type,
                    EventKey = key,
                    Title = Truncate(message.Subject.Trim(), 300),
                    BodyPreview = Truncate(message.Body.Trim(), 1000),
                    Tone = "amber",
                    ObjectType = invoice ? "invoice" : "payment",
                    ObjectId = payment.Id,
                    MetadataJson = JsonSerializer.Serialize(new { paymentId = payment.Id, actorUserId = 0, actorName = "CRM", changes }, JsonOptions),
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                };
                _context.UserNotifications.Add(notification);
                await _context.SaveChangesAsync(ct);
            }
            await lease.CommitAsync(ct);
        }

        // A retry retains the same CRM row and MAX key. Realtime/push transports
        // remain at-least-once; do not revive an already read/archived/deleted row.
        if (notification.IsRead || notification.IsArchived || notification.DeletedAt is not null) return;
        if (!await IsOverdueRecipientCurrentAsync(item, claimToken, recipientUserId, type, ct)) return;
        await QueueMaxNotificationIfAvailableAsync(notification, ct, throwOnFailure: true);
        if (!await IsOverdueRecipientCurrentAsync(item, claimToken, recipientUserId, type, ct)) return;
        var toast = await IsUserNotificationChannelEnabledAsync(recipientUserId, type, NotificationChannels.Toast, ct);
        await PublishDurableRealtimeAsync(notification, toast, ct);
        if (_webPushNotificationService is not null
            && await IsOverdueRecipientCurrentAsync(item, claimToken, recipientUserId, type, ct)
            && await IsUserNotificationChannelEnabledAsync(recipientUserId, type, NotificationChannels.WebPush, ct))
            await _webPushNotificationService.SendUserNotificationAsync(notification, ct);
    }

    private async Task<bool> IsOverdueRecipientCurrentAsync(PaymentOverdueNotificationOutbox item, string claimToken, int userId, string type, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (!await _context.PaymentOverdueNotificationOutboxes.AsNoTracking().AnyAsync(row => row.Id == item.Id
                && row.EventKey == item.EventKey && row.TransitionToken == item.TransitionToken
                && row.PaymentId == item.PaymentId && row.DocumentKind == item.DocumentKind && row.DueDate == item.DueDate
                && row.CompletedAt == null && row.LeaseToken == claimToken && row.LeaseUntil > now, ct))
            throw new InvalidOperationException("Overdue notification claim is no longer current.");
        if (!await PaymentOverdueNotificationQueue.CurrentPayments(_context, item).AnyAsync(ct)
            || !await PaymentOverdueNotificationQueue.RecipientIds(_context, item).AnyAsync(id => id == userId, ct)
            || !await IsUserNotificationChannelEnabledAsync(userId, type, NotificationChannels.InApp, ct)) return false;
        var envelope = new NotificationEventEnvelope(userId, type, item.EventKey, "", "",
            item.DocumentKind == DocumentKind.Invoice ? "invoice" : "payment", item.PaymentId, null);
        return await CanRecipientSeeNotificationObjectAsync(envelope, type, ct);
    }
}

using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed partial class NotificationService : IDurableCourtNotificationService
{
    private async Task<IReadOnlyList<NotificationDelivery>> RetryCourtDeliveryAsync(NotificationDelivery original, CancellationToken ct)
    {
        if (original.Channel is not (NotificationChannels.Email or NotificationChannels.WebPush)
            || original.Status != NotificationDeliveryStatus.Failed || original.RecipientUserId is not > 0) return [];
        var key = CaseCourtNotificationDeliveryGuard.RootKey(original.EventKey);
        if (key is null) return [];
        var intent = await _context.CaseCourtNotificationOutboxes.AsNoTracking().SingleOrDefaultAsync(row => row.EventKey == key, ct);
        // Unknown outcomes, completed work and live leases retain their original fences.
        if (intent is null || intent.CompletedAt.HasValue || intent.BlockedAt.HasValue || intent.NotificationCompletedAt.HasValue) return [];
        var before = await _context.NotificationDeliveries.AsNoTracking().Where(row => row.EventKey == key)
            .Select(row => (int?)row.Id).MaxAsync(ct) ?? 0;
        await new CaseCourtNotificationDispatchProcessor(_context, this, _realtimePublisher, _timeProvider).RetryAsync(intent.Id, ct);
        return await _context.NotificationDeliveries.AsNoTracking().Where(row => row.EventKey == key && row.Id > before
            && row.RecipientUserId == original.RecipientUserId && row.Channel == original.Channel).OrderBy(row => row.Id).ToArrayAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyCourtEventForEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent,
        string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        var deliveries = await NotifyCaseCourtEventCoreAsync(clientCase, courtEvent, action, actorName, actorUserId, recipients, ct, claim);
        return deliveries.Concat(await ReadCourtPushOutcomesAsync(claim, ct)).ToArray();
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyProceduralDeadlineForEventAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline,
        string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        await NotifyCaseProceduralDeadlineCoreAsync(clientCase, deadline, action, actorName, actorUserId, recipients, ct, claim);
        return await ReadCourtPushOutcomesAsync(claim, ct);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyCaseDateForEventAsync(ClientCase clientCase, CaseCourtDateChange change,
        string actorName, int actorUserId, IReadOnlyCollection<int> recipients, DateTime changedAtUtc, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        if (!CaseCourtNotificationVersion.IsCaseDateKind(claim.Kind) || clientCase.Id != claim.CaseId)
            throw new InvalidOperationException("Case date notification does not match its accepted source.");
        if (change.SuppressDelivery) return [];
        IReadOnlyList<NotificationDelivery> deliveries = [];
        if (claim.Kind == "hearing-date")
        {
            if (!change.CurrentAt.HasValue) return [];
            await NotifyCaseHearingDateChangedCoreAsync(clientCase, change.PreviousAt, change.CurrentAt.Value,
                actorName, actorUserId, recipients, changedAtUtc, ct, claim);
        }
        else deliveries = await NotifyCaseDecisionDateChangedCoreAsync(clientCase, change.PreviousAt, change.CurrentAt,
            actorName, actorUserId, recipients, changedAtUtc, ct, claim);
        return deliveries.Concat(await ReadCourtPushOutcomesAsync(claim, ct)).ToArray();
    }

    private static async Task RequireCourtDeliveryClaimAsync(CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        if (claim.OutboxId <= 0 || claim.CaseId <= 0 || claim.SourceId <= 0 || claim.Kind is not ("event" or "deadline" or "reminder" or "hearing-date" or "decision-date" or "current-state")
            || !(claim.Kind == "reminder" ? CaseCourtReminderKeys.TryParse(claim.EventKey, out _) : CaseCourtNotificationVersion.IsValidEventKey(claim.EventKey)) || claim.SourceFingerprint.Length != 64 || !await claim.OwnsLease(ct))
            throw new InvalidOperationException("Court notification delivery claim is no longer current.");
    }

    private async Task PinCourtNotificationClaimAsync(CaseCourtDeliveryClaim claim, NotificationEventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.ObjectType != "case" || envelope.ObjectId != claim.CaseId || envelope.EventKey != claim.EventKey)
            throw new InvalidOperationException("Court notification envelope does not belong to its accepted event.");
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {envelope.RecipientUserId} FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {envelope.RecipientUserId}) FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"RoleId\" FROM \"RolePermissions\" WHERE \"RoleId\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {envelope.RecipientUserId}) ORDER BY \"Section\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"CaseCourtNotificationOutboxes\" WHERE \"Id\" = {claim.OutboxId} FOR UPDATE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {claim.CaseId} FOR SHARE NOWAIT", ct);
            if (claim.Kind == "event")
                await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEvents\" WHERE \"Id\" = {claim.SourceId} AND \"ClientCaseId\" = {claim.CaseId} FOR SHARE NOWAIT", ct);
            else if (claim.Kind is "deadline" or "reminder")
                await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"Id\" = {claim.SourceId} AND \"ClientCaseId\" = {claim.CaseId} FOR SHARE NOWAIT", ct);
        }
        await RequireCourtDeliveryClaimAsync(claim, ct);
    }

    private async Task<bool> IsCourtRecipientCurrentAsync(int userId, string type, string channel, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        if (!await _context.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive && user.IsApproved, ct)) return false;
        if (!await CaseCourtNotificationVersion.IsCurrentAsync(_context, claim.OutboxId, claim.CaseId, claim.Kind,
            claim.SourceId, claim.SourceFingerprint, ct)) return false;
        if (claim.Kind == "reminder" && !await CaseCourtReminderKeys.IsCurrentWindowAsync(_context, claim.EventKey, userId, claim.ReminderTimeZoneId,
            _timeProvider.GetUtcNow().UtcDateTime, ct)) return false;
        var envelope = new NotificationEventEnvelope(userId, type, claim.EventKey, "", "", "case", claim.CaseId, null);
        return await CanRecipientSeeNotificationObjectAsync(envelope, type, ct)
            && await IsUserNotificationChannelEnabledAsync(userId, type, channel, ct);
    }

    private async Task<bool> CanReplayCourtNotificationAsync(UserNotification notification, string channel, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        if (!await _context.UserNotifications.AsNoTracking().AnyAsync(row => row.Id == notification.Id
                && row.EventKey == claim.EventKey && row.RecipientUserId == notification.RecipientUserId
                && row.ObjectType == "case" && row.ObjectId == claim.CaseId && !row.IsRead && !row.IsArchived && row.DeletedAt == null, ct)) return false;
        return await IsCourtRecipientCurrentAsync(notification.RecipientUserId, notification.Type, channel, claim, ct);
    }

    private async Task ReplayCourtNotificationChannelsAsync(UserNotification notification, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        if (!await CanReplayCourtNotificationAsync(notification, NotificationChannels.InApp, claim, ct)) return;
        await QueueMaxNotificationIfAvailableAsync(notification, ct, throwOnFailure: true);
        if (!await CanReplayCourtNotificationAsync(notification, NotificationChannels.InApp, claim, ct)) return;
        var toast = await IsUserNotificationChannelEnabledAsync(notification.RecipientUserId, notification.Type, NotificationChannels.Toast, ct);
        await RequireCourtDeliveryClaimAsync(claim, ct);
        await PublishDurableRealtimeAsync(notification, toast, ct);
        if (_webPushNotificationService is not null && await CanReplayCourtNotificationAsync(notification, NotificationChannels.WebPush, claim, ct))
        {
            var pending = await _context.NotificationDeliveries.AsNoTracking().AnyAsync(row => row.EventKey == claim.EventKey
                && row.RecipientUserId == notification.RecipientUserId && row.Channel == NotificationChannels.WebPush
                && row.Status == NotificationDeliveryStatus.Pending
                && _context.UserPushSubscriptions.Any(subscription => subscription.UserId == row.RecipientUserId
                    && subscription.Endpoint == row.RecipientAddress && subscription.IsActive && subscription.RevokedAt == null), ct);
            if (!pending)
            {
                await RequireCourtDeliveryClaimAsync(claim, ct);
                await _webPushNotificationService.SendUserNotificationAsync(notification, ct);
            }
        }
    }

    private async Task<IReadOnlyList<NotificationDelivery>> SendCourtEmailForEventAsync(ClientCase clientCase, NotificationRecipient recipient,
        NotificationMessage message, string type, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        if (recipient.UserId is not int userId || clientCase.Id != claim.CaseId
            || !await IsCourtRecipientCurrentAsync(userId, type, NotificationChannels.Email, claim, ct)) return [];
        var pending = await _context.NotificationDeliveries.AsNoTracking().Where(row => row.EventKey == claim.EventKey
            && row.RecipientUserId == userId && row.Channel == NotificationChannels.Email && row.Status == NotificationDeliveryStatus.Pending).ToArrayAsync(ct);
        if (pending.Length > 0) return pending;
        await RequireCourtDeliveryClaimAsync(claim, ct);
        return await SendAsync(recipient, message, [NotificationChannels.Email], claim.EventKey, ct);
    }

    private async Task<IReadOnlyList<NotificationDelivery>> ReadCourtPushOutcomesAsync(CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        var latest = await _context.NotificationDeliveries.AsNoTracking()
            .Where(row => row.EventKey == claim.EventKey && row.Channel == NotificationChannels.WebPush)
            .GroupBy(row => new { row.RecipientUserId, row.RecipientAddress })
            .Select(group => group.OrderByDescending(row => row.Id).First()).ToListAsync(ct);
        var unfinished = new List<NotificationDelivery>();
        foreach (var delivery in latest.Where(row => row.Status is NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.Pending))
        {
            if (delivery.RecipientUserId is not int userId) continue;
            var notification = await _context.UserNotifications.AsNoTracking()
                .SingleOrDefaultAsync(row => row.EventKey == claim.EventKey && row.RecipientUserId == userId, ct);
            if (notification is not null && await CanReplayCourtNotificationAsync(notification, NotificationChannels.WebPush, claim, ct)
                && await _context.UserPushSubscriptions.AsNoTracking().AnyAsync(row => row.UserId == userId
                    && row.Endpoint == delivery.RecipientAddress && row.IsActive && row.RevokedAt == null, ct))
                unfinished.Add(delivery);
        }
        return unfinished;
    }
}

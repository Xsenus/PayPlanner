using Microsoft.AspNetCore.SignalR;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class SignalRRealtimeNotificationPublisher : IRealtimeNotificationPublisher
{
    private readonly IHubContext<NotificationsHub> _hubContext;
    private readonly ILogger<SignalRRealtimeNotificationPublisher> _logger;

    public SignalRRealtimeNotificationPublisher(
        IHubContext<NotificationsHub> hubContext,
        ILogger<SignalRRealtimeNotificationPublisher> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task PublishUserNotificationAsync(
        UserNotification notification,
        bool toastEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (notification.RecipientUserId <= 0)
        {
            return;
        }

        var payload = new RealtimeUserNotificationDto(
            notification.Id,
            notification.RecipientUserId,
            notification.Type,
            notification.EventKey,
            notification.Title,
            notification.BodyPreview,
            notification.Tone,
            toastEnabled,
            notification.ObjectType,
            notification.ObjectId,
            notification.TaskJournalItemId,
            notification.MetadataJson,
            notification.CreatedAt);

        try
        {
            await _hubContext.Clients
                .Group(NotificationsHub.UserGroup(notification.RecipientUserId))
                .SendAsync(NotificationsHub.UserNotificationCreatedEvent, payload, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish realtime notification {NotificationId} to user {UserId}.",
                notification.Id,
                notification.RecipientUserId);
        }
    }
}

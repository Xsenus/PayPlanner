using Microsoft.AspNetCore.SignalR;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class SignalRRealtimeNotificationPublisher : IRealtimeNotificationPublisher, IReliableRealtimeNotificationPublisher, IReliableTaskJournalChangePublisher, IRolePermissionsChangePublisher, IUserRoleChangePublisher
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
        try { await PublishUserNotificationOrThrowAsync(notification, toastEnabled, cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish realtime notification {NotificationId} to user {UserId}.", notification.Id, notification.RecipientUserId);
        }
    }

    public async Task PublishUserNotificationOrThrowAsync(
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

        await _hubContext.Clients
            .Group(NotificationsHub.UserGroup(notification.RecipientUserId))
            .SendAsync(NotificationsHub.UserNotificationCreatedEvent, payload, cancellationToken);
    }

    public async Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PublishTaskJournalChangedOrThrowAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish task journal realtime change signal.");
        }
    }

    public Task PublishTaskJournalChangedOrThrowAsync(CancellationToken cancellationToken = default)
        => _hubContext.Clients.All.SendAsync(NotificationsHub.TaskJournalChangedEvent, cancellationToken);

    public async Task PublishRolePermissionsChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
    {
        var groups = userIds.Where(id => id > 0).Distinct().Select(NotificationsHub.UserGroup).ToArray();
        if (groups.Length == 0) return;
        try
        {
            await _hubContext.Clients.Groups(groups).SendAsync(NotificationsHub.RolePermissionsChangedEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish role permissions change signal.");
        }
    }

    public async Task PublishUserRoleChangedAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (userId <= 0) return;
        try
        {
            await _hubContext.Clients.Group(NotificationsHub.UserGroup(userId))
                .SendAsync(NotificationsHub.UserRoleChangedEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish user role change signal for user {UserId}.", userId);
        }
    }

    public async Task PublishUsersRoleChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
    {
        var groups = userIds.Where(id => id > 0).Distinct().Select(NotificationsHub.UserGroup).ToArray();
        if (groups.Length == 0) return;
        try
        {
            await _hubContext.Clients.Groups(groups).SendAsync(NotificationsHub.UserRoleChangedEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish user role change signals.");
        }
    }
}

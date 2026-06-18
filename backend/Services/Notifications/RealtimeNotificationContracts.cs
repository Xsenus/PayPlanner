using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record RealtimeUserNotificationDto(
    int Id,
    int RecipientUserId,
    string Type,
    string EventKey,
    string Title,
    string BodyPreview,
    string Tone,
    bool ToastEnabled,
    string ObjectType,
    int? ObjectId,
    int? TaskJournalItemId,
    DateTime CreatedAt);

public interface IRealtimeNotificationPublisher
{
    Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default);
}

public sealed class NoOpRealtimeNotificationPublisher : IRealtimeNotificationPublisher
{
    public static NoOpRealtimeNotificationPublisher Instance { get; } = new();

    private NoOpRealtimeNotificationPublisher()
    {
    }

    public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

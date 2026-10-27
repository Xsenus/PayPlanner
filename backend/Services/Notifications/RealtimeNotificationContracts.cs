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
    string MetadataJson,
    DateTime CreatedAt);

public interface IRealtimeNotificationPublisher
{
    Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default);

    Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default);
}

public interface IRolePermissionsChangePublisher
{
    Task PublishRolePermissionsChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default);
}

public interface IUserRoleChangePublisher
{
    Task PublishUserRoleChangedAsync(int userId, CancellationToken cancellationToken = default);
    Task PublishUsersRoleChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default);
}

public sealed class NoOpRealtimeNotificationPublisher : IRealtimeNotificationPublisher
{
    public static NoOpRealtimeNotificationPublisher Instance { get; } = new();

    private NoOpRealtimeNotificationPublisher()
    {
    }

    public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

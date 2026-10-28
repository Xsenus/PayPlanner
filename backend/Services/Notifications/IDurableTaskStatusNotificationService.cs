using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Delivers one persisted task-status event using its stable identity for safe retries.</summary>
public interface IDurableTaskStatusNotificationService
{
    Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default);
}

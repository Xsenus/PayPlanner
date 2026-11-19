using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Delivers one persisted task-status event using its stable identity for safe retries.</summary>
public interface IDurableTaskStatusNotificationService
{
    Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, string completionOrigin, CancellationToken cancellationToken = default)
        => NotifyCompletionForEventAsync(task, eventKey, cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalCompletionForEventAsync(TaskJournalItem task, TaskJournalParticipant approver, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default)
        => NotifyCompletionForEventAsync(task, eventKey, TaskCompletionNotificationPolicy.ApprovalOrigin, cancellationToken);
    Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default);
}

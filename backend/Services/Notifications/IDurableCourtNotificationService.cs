using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public interface IDurableCourtNotificationService
{
    Task<IReadOnlyList<NotificationDelivery>> NotifyCourtEventForEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent,
        string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct);

    Task<IReadOnlyList<NotificationDelivery>> NotifyProceduralDeadlineForEventAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline,
        string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct);
}

public sealed record CaseCourtDeliveryClaim(long OutboxId, string EventKey, int CaseId, string Kind, int SourceId,
    Func<CancellationToken, Task<bool>> OwnsLease, string SourceFingerprint);

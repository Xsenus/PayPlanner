using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private Task DispatchAcceptedCaseEditNotificationsAsync(IReadOnlyList<long> ids, IDurableCourtNotificationService notifications, CancellationToken ct)
    {
        var logger = HttpContext?.RequestServices?.GetService<ILogger<CaseCourtNotificationDispatchProcessor>>();
        return new CaseCourtNotificationDispatchProcessor(_db, notifications, _realtimePublisher, logger: logger)
            .TryProcessAcceptedBatchAsync(ids, ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public interface ICaseDocumentNotificationLiveDispatcher
{
    Task<string> DispatchCaseDocumentAsync(int notificationId, string channel, bool toastEnabled, Func<CancellationToken, Task<bool>> ownsLease, CancellationToken ct);
}

public sealed class CaseDocumentNotificationDispatchProcessor(PaymentContext db,
    ICaseDocumentNotificationLiveDispatcher dispatcher, TimeProvider? timeProvider = null,
    ILogger<CaseDocumentNotificationDispatchProcessor>? logger = null)
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private ILogger Log => logger ?? NullLogger<CaseDocumentNotificationDispatchProcessor>.Instance;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        RequireCommittedContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var ids = await db.CaseDocumentNotificationDispatches.AsNoTracking()
            .Where(row => ((row.Status == "pending" || row.Status == "failed") && row.NextAttemptAt <= now)
                || (row.Status == "running" && row.LeaseExpiresAt <= now))
            .OrderBy(row => row.NextAttemptAt).ThenBy(row => row.Id).Select(row => row.Id).Take(100).ToArrayAsync(ct);
        var completed = 0;
        foreach (var id in ids) if (await ProcessAsync(id, ct)) completed++;
        return completed;
    }

    public async Task<bool> ProcessAsync(Guid id, CancellationToken ct)
    {
        RequireCommittedContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid().ToString("N");
        var expires = now.AddMinutes(2);
        var claimed = await db.CaseDocumentNotificationDispatches
            .Where(row => row.Id == id && (((row.Status == "pending" || row.Status == "failed") && row.NextAttemptAt <= now)
                || (row.Status == "running" && row.LeaseExpiresAt <= now)))
            .ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, "running").SetProperty(row => row.LeaseOwner, token)
                .SetProperty(row => row.LeaseExpiresAt, (DateTime?)expires).SetProperty(row => row.AttemptCount, row => row.AttemptCount + 1), ct);
        if (claimed != 1) return false;
        var item = await db.CaseDocumentNotificationDispatches.AsNoTracking()
            .SingleAsync(row => row.Id == id && row.LeaseOwner == token, ct);
        string result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            result = item.AttemptCount > 10 ? "failed" : await dispatcher.DispatchCaseDocumentAsync(item.UserNotificationId, item.Channel, item.ToastEnabled,
                async tokenCt => await db.CaseDocumentNotificationDispatches.AsNoTracking().AnyAsync(row => row.Id == id
                    && row.Status == "running" && row.LeaseOwner == token && row.LeaseExpiresAt > Clock.GetUtcNow().UtcDateTime, tokenCt), timeout.Token);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Case document notification dispatch {DispatchId} remains queued", id);
            result = "failed";
        }
        var finished = Clock.GetUtcNow().UtcDateTime;
        var status = result is "completed" or "skipped" ? result : item.AttemptCount >= 10 ? "deadLetter" : "failed";
        var next = finished.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(item.AttemptCount - 1, 10))));
        // Settle only our live lease; a delayed transport must not acknowledge a replacement worker's claim.
        using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var settled = await db.CaseDocumentNotificationDispatches
            .Where(row => row.Id == id && row.Status == "running" && row.LeaseOwner == token && row.LeaseExpiresAt > finished)
            .ExecuteUpdateAsync(s => s.SetProperty(row => row.Status, status)
                .SetProperty(row => row.NextAttemptAt, next).SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAt, (DateTime?)null).SetProperty(row => row.LastError, result == "failed" ? "Transport failed or timed out." : null)
                .SetProperty(row => row.CompletedAt, status == "completed" || status == "skipped" || status == "deadLetter" ? (DateTime?)finished : null), settleTimeout.Token);
        return settled == 1 && status == "completed";
    }

    private void RequireCommittedContext()
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Case notification delivery cannot run inside a domain transaction.");
    }
}

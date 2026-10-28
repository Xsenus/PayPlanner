using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Delivers the current reassigned reviewer notification with an expiring, owned claim.</summary>
public sealed class TaskDeadlineReviewerDispatchProcessor(
    PaymentContext db,
    INotificationService notifications,
    TimeProvider? timeProvider = null,
    ILogger<TaskDeadlineReviewerDispatchProcessor>? logger = null)
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private ILogger Log => logger ?? NullLogger<TaskDeadlineReviewerDispatchProcessor>.Instance;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Deadline reviewer recovery requires a clean, committed context.");
        var now = Clock.GetUtcNow().UtcDateTime;
        var ids = await db.TaskJournalDeadlineChangeRequests.AsNoTracking()
            .Where(item => item.ReviewerDispatchPending && (item.ReviewerDispatchLeaseUntil == null || item.ReviewerDispatchLeaseUntil <= now))
            .OrderBy(item => item.ReviewerDispatchLeaseUntil).ThenBy(item => item.Id)
            .Select(item => item.Id).Take(50).ToArrayAsync(ct);
        var completed = 0;
        foreach (var id in ids)
        {
            try { if (await ProcessAsync(id, ct)) completed++; }
            finally { db.ChangeTracker.Clear(); }
        }
        return completed;
    }

    public async Task<bool> ProcessAsync(int id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Deadline reviewer notifications require a committed transaction.");
        var now = Clock.GetUtcNow().UtcDateTime;
        var previousAttempts = await db.TaskJournalDeadlineChangeRequests.AsNoTracking()
            .Where(item => item.Id == id && item.ReviewerDispatchPending
                && (item.ReviewerDispatchLeaseUntil == null || item.ReviewerDispatchLeaseUntil <= now))
            .Select(item => (int?)item.ReviewerDispatchAttempts).SingleOrDefaultAsync(ct);
        if (!previousAttempts.HasValue) return false;
        var token = Guid.NewGuid().ToString("N");
        var retryMinutes = Math.Min(60, 2 * (1 << Math.Clamp(previousAttempts.Value, 0, 5)));
        var leaseUntil = now.AddMinutes(retryMinutes);
        var claimed = await db.TaskJournalDeadlineChangeRequests
            .Where(item => item.Id == id && item.ReviewerDispatchPending && item.ReviewerDispatchAttempts == previousAttempts.Value
                && (item.ReviewerDispatchLeaseUntil == null || item.ReviewerDispatchLeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchLeaseToken, token)
                .SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)leaseUntil)
                .SetProperty(item => item.ReviewerDispatchAttempts, item => item.ReviewerDispatchAttempts < 30 ? item.ReviewerDispatchAttempts + 1 : 30), ct);
        if (claimed != 1) return false;

        try
        {
            var request = await db.TaskJournalDeadlineChangeRequests.AsNoTracking()
                .Include(item => item.TaskJournalItem).ThenInclude(task => task.Participants)
                .SingleOrDefaultAsync(item => item.Id == id && item.ReviewerDispatchPending && item.ReviewerDispatchLeaseToken == token, ct);
            if (request is null) return false;
            if (string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase) && request.ReviewerUserId is > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                await notifications.NotifyTaskDeadlineReviewerAssignedAsync(request.TaskJournalItem, request, timeout.Token);
            }

            var finishedAt = Clock.GetUtcNow().UtcDateTime;
            using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settled = await db.TaskJournalDeadlineChangeRequests
                .Where(item => item.Id == id && item.ReviewerDispatchPending && item.ReviewerDispatchLeaseToken == token
                    && item.ReviewerDispatchLeaseUntil > finishedAt)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReviewerDispatchPending, false)
                    .SetProperty(item => item.ReviewerDispatchLeaseToken, (string?)null)
                    .SetProperty(item => item.ReviewerDispatchLeaseUntil, (DateTime?)null), settleTimeout.Token);
            return settled == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Log.LogWarning(error, "Deadline reviewer notification for request {RequestId} remains queued.", id);
            return false;
        }
    }
}

public sealed class TaskDeadlineReviewerDispatchWorker(IServiceScopeFactory scopes,
    ILogger<TaskDeadlineReviewerDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TaskDeadlineReviewerDispatchProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Deadline reviewer notifications will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

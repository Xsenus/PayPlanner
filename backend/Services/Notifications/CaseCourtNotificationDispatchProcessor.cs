using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class CaseCourtNotificationDispatchProcessor(PaymentContext db, IDurableCourtNotificationService notifications,
    IRealtimeNotificationPublisher realtime, TimeProvider? timeProvider = null, ILogger<CaseCourtNotificationDispatchProcessor>? logger = null)
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private ILogger Log => logger ?? NullLogger<CaseCourtNotificationDispatchProcessor>.Instance;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        RequireCleanContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var ids = await db.CaseCourtNotificationOutboxes.AsNoTracking()
            .Where(row => row.CompletedAt == null && row.NextAttemptAt <= now
                && (row.LeaseUntil == null || row.LeaseUntil <= now)
                && (row.BlockedAt == null || (row.JournalChanged && row.JournalCompletedAt == null)))
            .OrderBy(row => row.NextAttemptAt).ThenBy(row => row.Id).Select(row => row.Id).Take(50).ToArrayAsync(ct);
        var completed = 0;
        foreach (var id in ids)
        {
            try { if (await ProcessAsync(id, ct)) completed++; }
            finally { db.ChangeTracker.Clear(); }
        }
        return completed;
    }

    public Task<bool> ProcessAsync(long id, CancellationToken ct) => ProcessAsync(id, false, ct);

    internal Task<bool> RetryAsync(long id, CancellationToken ct) => ProcessAsync(id, true, ct);

    private async Task<bool> ProcessAsync(long id, bool retryNow, CancellationToken ct)
    {
        RequireCleanContext();
        var now = Clock.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid().ToString("N");
        var claimed = await db.CaseCourtNotificationOutboxes
            .Where(row => row.Id == id && row.CompletedAt == null && (row.NextAttemptAt <= now || retryNow)
                && (row.LeaseUntil == null || row.LeaseUntil <= now)
                && (row.BlockedAt == null || (row.JournalChanged && row.JournalCompletedAt == null)))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, token).SetProperty(row => row.LeaseUntil, (DateTime?)now.AddMinutes(2))
                .SetProperty(row => row.Attempts, row => row.Attempts < 30 ? row.Attempts + 1 : 30), ct);
        if (claimed != 1) return false;
        var item = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == id && row.LeaseToken == token, ct);
        var journalDone = !item.JournalChanged || item.JournalCompletedAt.HasValue;
        var notificationDone = item.NotificationCompletedAt.HasValue;
        var blocked = item.BlockedAt.HasValue;
        string? failure = null;
        try
        {
            // Journal invalidation and notification delivery can each recover independently.
            if (!journalDone)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await RequireOwnershipAsync(item, token, timeout.Token);
                    if (realtime is IReliableTaskJournalChangePublisher reliable)
                        await reliable.PublishTaskJournalChangedOrThrowAsync(timeout.Token);
                    else await realtime.PublishTaskJournalChangedAsync(timeout.Token);
                    var at = Clock.GetUtcNow().UtcDateTime;
                    journalDone = await Owned(item, token).ExecuteUpdateAsync(set => set.SetProperty(row => row.JournalCompletedAt, (DateTime?)at), timeout.Token) == 1;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { failure = error.GetType().Name; Log.LogWarning(error, "Court journal event {EventId} remains queued.", id); }
            }
            if (!notificationDone && !blocked)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    await RequireOwnershipAsync(item, token, timeout.Token);
                    if (!TryReadPayload(item, out var payload))
                    {
                        blocked = true;
                        failure = "Invalid court notification snapshot requires review.";
                    }
                    else
                    {
                        IReadOnlyList<NotificationDelivery> deliveries = [];
                        if (item.Kind != "journal" && await SourceIsCurrentAsync(item, payload!, timeout.Token))
                        {
                            var clientCase = new ClientCase { Id = item.ClientCaseId, Title = payload!.CaseTitle, CaseNumber = payload.CaseNumber };
                            var claim = new CaseCourtDeliveryClaim(item.Id, item.EventKey, item.ClientCaseId, item.Kind, item.SourceId,
                                async tokenCt => await Owned(item, token).AnyAsync(tokenCt),
                                item.Kind == "event" ? CaseCourtNotificationVersion.Capture(payload.CourtEvent!)
                                    : CaseCourtNotificationVersion.Capture(payload.Deadline!));
                            deliveries = item.Kind == "event"
                                ? await notifications.NotifyCourtEventForEventAsync(clientCase, payload.CourtEvent!, payload.Action, payload.ActorName,
                                    payload.ActorUserId, payload.RecipientUserIds, claim, timeout.Token)
                                : await notifications.NotifyProceduralDeadlineForEventAsync(clientCase, payload.Deadline!, payload.Action, payload.ActorName,
                                    payload.ActorUserId, payload.RecipientUserIds, claim, timeout.Token);
                        }
                        blocked = deliveries.Any(row => row.Status == NotificationDeliveryStatus.Pending);
                        if (blocked) failure = "External delivery outcome requires review.";
                        else if (deliveries.Any(row => row.Status == NotificationDeliveryStatus.Failed)) failure = "A notification channel failed.";
                        else
                        {
                            var at = Clock.GetUtcNow().UtcDateTime;
                            notificationDone = await Owned(item, token).ExecuteUpdateAsync(set => set.SetProperty(row => row.NotificationCompletedAt, (DateTime?)at), timeout.Token) == 1;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { failure = error.GetType().Name; Log.LogWarning(error, "Court notification event {EventId} remains queued.", id); }
            }
            var finished = Clock.GetUtcNow().UtcDateTime;
            var complete = journalDone && notificationDone && !blocked;
            var next = finished.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Clamp(item.Attempts - 1, 0, 10))));
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settled = await Owned(item, token).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.CompletedAt, complete ? (DateTime?)finished : null)
                .SetProperty(row => row.BlockedAt, row => blocked ? (DateTime?)finished : row.BlockedAt)
                .SetProperty(row => row.NextAttemptAt, next).SetProperty(row => row.LastError, failure)
                .SetProperty(row => row.LeaseToken, (string?)null).SetProperty(row => row.LeaseUntil, (DateTime?)null), settle.Token);
            return complete && settled == 1;
        }
        finally
        {
            // The controller may retain clean tracked case/file objects after this call.
            foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
                entry.State = EntityState.Detached;
        }
    }

    private IQueryable<CaseCourtNotificationOutbox> Owned(CaseCourtNotificationOutbox item, string token)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        return db.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id && row.CompletedAt == null && row.LeaseToken == token
            && row.LeaseUntil > now && row.EventKey == item.EventKey && row.ClientCaseId == item.ClientCaseId
            && row.Kind == item.Kind && row.SourceId == item.SourceId && row.PayloadJson == item.PayloadJson);
    }

    private async Task RequireOwnershipAsync(CaseCourtNotificationOutbox item, string token, CancellationToken ct)
    {
        if (!await Owned(item, token).AnyAsync(ct)) throw new InvalidOperationException("Court notification claim has expired or changed.");
    }

    private async Task<bool> SourceIsCurrentAsync(CaseCourtNotificationOutbox item, CaseCourtNotificationPayload payload, CancellationToken ct)
    {
        if (!await db.ClientCases.AsNoTracking().AnyAsync(row => row.Id == item.ClientCaseId, ct)) return false;
        var fingerprint = item.Kind == "event" ? CaseCourtNotificationVersion.Capture(payload.CourtEvent!)
            : CaseCourtNotificationVersion.Capture(payload.Deadline!);
        return await CaseCourtNotificationVersion.IsCurrentAsync(db, item.Id, item.ClientCaseId, item.Kind, item.SourceId, fingerprint, ct);
    }

    internal static bool TryReadPayload(CaseCourtNotificationOutbox item, [NotNullWhen(true)] out CaseCourtNotificationPayload? payload)
    {
        payload = null;
        try { payload = JsonSerializer.Deserialize<CaseCourtNotificationPayload>(item.PayloadJson, CaseCourtNotificationQueue.JsonOptions); }
        catch (JsonException) { return false; }
        if (payload is not { Version: 1, CaseTitle: not null, CaseNumber: not null, ActorName: not null, RecipientUserIds: not null }
            || payload.ActorUserId < 0 || payload.RecipientUserIds.Any(id => id <= 0)
            || payload.RecipientUserIds.Distinct().Count() != payload.RecipientUserIds.Length
            || !CaseCourtNotificationVersion.IsValidEventKey(item.EventKey)) return false;
        return item.Kind switch
        {
            "event" => payload.CourtEvent is not null && payload.Deadline is null && payload.CourtEvent.Id == item.SourceId
                && payload.CourtEvent.ClientCaseId == item.ClientCaseId && payload.Action is "created" or "updated" or "rescheduled",
            "deadline" => payload.Deadline is not null && payload.CourtEvent is null && payload.Deadline.Id == item.SourceId
                && payload.Deadline.ClientCaseId == item.ClientCaseId && payload.Action is "created" or "updated" or "closed",
            "journal" => item.SourceId == 0 && payload.CourtEvent is null && payload.Deadline is null,
            _ => false
        };
    }

    private void RequireCleanContext()
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Court notification recovery requires a clean, committed context.");
    }
}

public sealed class CaseCourtNotificationDispatchWorker(IServiceScopeFactory scopes, ILogger<CaseCourtNotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CaseCourtNotificationDispatchProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Court notification recovery batch failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

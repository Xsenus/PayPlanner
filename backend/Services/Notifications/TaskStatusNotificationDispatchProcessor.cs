using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>Processes committed status events using a bounded, expiring claim.</summary>
public sealed class TaskStatusNotificationDispatchProcessor(
    PaymentContext db,
    IDurableTaskStatusNotificationService notifications,
    TimeProvider? timeProvider = null,
    ILogger<TaskStatusNotificationDispatchProcessor>? logger = null)
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;
    private ILogger Log => logger ?? NullLogger<TaskStatusNotificationDispatchProcessor>.Instance;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Task status recovery requires a clean, committed context.");
        var now = Clock.GetUtcNow().UtcDateTime;
        var ids = await db.TaskStatusNotificationOutboxes.AsNoTracking()
            .Where(item => item.CompletedAt == null && (item.LeaseUntil == null || item.LeaseUntil <= now))
            .OrderBy(item => item.LeaseUntil).ThenBy(item => item.Id).Select(item => item.Id).Take(50).ToArrayAsync(ct);
        var completed = 0;
        foreach (var id in ids)
        {
            try { if (await ProcessAsync(id, ct)) completed++; }
            finally { db.ChangeTracker.Clear(); }
        }
        return completed;
    }

    public async Task<bool> ProcessAsync(long id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Task status recovery requires a clean, committed context.");
        var now = Clock.GetUtcNow().UtcDateTime;
        var attempts = await db.TaskStatusNotificationOutboxes.AsNoTracking()
            .Where(item => item.Id == id && item.CompletedAt == null && (item.LeaseUntil == null || item.LeaseUntil <= now))
            .Select(item => (int?)item.Attempts).SingleOrDefaultAsync(ct);
        if (!attempts.HasValue) return false;
        var token = Guid.NewGuid().ToString("N");
        var leaseUntil = now.AddMinutes(Math.Min(60, 2 * (1 << Math.Clamp(attempts.Value, 0, 5))));
        var claimed = await db.TaskStatusNotificationOutboxes
            .Where(item => item.Id == id && item.CompletedAt == null && item.Attempts == attempts.Value
                && (item.LeaseUntil == null || item.LeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseToken, token)
                .SetProperty(item => item.LeaseUntil, (DateTime?)leaseUntil)
                .SetProperty(item => item.Attempts, item => item.Attempts < 30 ? item.Attempts + 1 : 30), ct);
        if (claimed != 1) return false;

        try
        {
            var item = await db.TaskStatusNotificationOutboxes.AsNoTracking()
                .Include(entry => entry.TaskJournalItem).ThenInclude(task => task.Participants).ThenInclude(participant => participant.User)
                .SingleOrDefaultAsync(entry => entry.Id == id && entry.CompletedAt == null && entry.LeaseToken == token, ct);
            if (item is null) return false;
            var task = item.TaskJournalItem;
            if (item.EventType is not ("approval" or "completion"
                or TaskCompletionNotificationPolicy.OrdinaryEventType or TaskCompletionNotificationPolicy.CoalescedEventType or TaskCompletionNotificationPolicy.ApprovalEventType))
            {
                Log.LogError("Unknown task status notification type for event {EventId}.", id);
                return false;
            }
            var stale = !string.Equals(task.StatusTransitionToken, item.StatusTransitionToken, StringComparison.Ordinal)
                || await db.TaskStatusNotificationOutboxes.AsNoTracking()
                .AnyAsync(other => other.TaskJournalItemId == item.TaskJournalItemId && other.Id > item.Id, ct);
            if (item.EventType == "approval")
            {
                var currentApproverId = task.Participants
                    .Where(participant => string.Equals(participant.Role, "approver", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(participant.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(participant => participant.SortOrder).ThenBy(participant => participant.Id)
                    .Select(participant => (int?)participant.Id).FirstOrDefault();
                stale |= !item.ApprovalParticipantId.HasValue || item.ApprovalParticipantId != currentApproverId;
            }
            var validStatus = item.EventType switch
            {
                "approval" => string.Equals(task.Status, "onApproval", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(task.Status, "overdueApproval", StringComparison.OrdinalIgnoreCase),
                "completion" or TaskCompletionNotificationPolicy.OrdinaryEventType or TaskCompletionNotificationPolicy.CoalescedEventType or TaskCompletionNotificationPolicy.ApprovalEventType
                    => string.Equals(task.Status, "done", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!stale && validStatus)
            {
                // An unfinished external attempt has an unknown outcome; do not send a duplicate blindly.
                var approvalDecisionEventKey = TaskCompletionNotificationPolicy.ApprovalDecisionEventKey(item.EventKey);
                if (await db.NotificationDeliveries.AsNoTracking().AnyAsync(delivery => (delivery.EventKey == item.EventKey
                    || (item.EventType == TaskCompletionNotificationPolicy.ApprovalEventType && delivery.EventKey == approvalDecisionEventKey))
                    && delivery.Status == NotificationDeliveryStatus.Pending, ct)) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                IReadOnlyList<NotificationDelivery> deliveries;
                if (item.EventType == TaskCompletionNotificationPolicy.ApprovalEventType)
                {
                    var snapshot = await LoadApprovalSnapshotAsync(item, timeout.Token);
                    if (snapshot is null)
                    {
                        Log.LogWarning("Task approval completion event {EventId} has no matching decision snapshot.", id);
                        return false;
                    }
                    task.Result = snapshot.TaskResult;
                    var approver = new TaskJournalParticipant
                    {
                        Id = snapshot.ParticipantId,
                        TaskJournalItemId = task.Id,
                        UserId = snapshot.ApproverUserId,
                        DisplayName = snapshot.ApproverName,
                        ApprovalStatus = snapshot.Decision,
                        Resolution = snapshot.Resolution,
                        RespondedAt = snapshot.RespondedAt
                    };
                    deliveries = await notifications.NotifyApprovalCompletionForEventAsync(
                        task, approver, item.ActorName, item.ActorUserId, item.EventKey, timeout.Token);
                }
                else
                {
                    deliveries = item.EventType == "approval"
                        ? await notifications.NotifyApprovalForEventAsync(task, item.ActorName, item.ActorUserId, item.EventKey, timeout.Token)
                        : await notifications.NotifyCompletionForEventAsync(task, item.EventKey,
                            TaskCompletionNotificationPolicy.OriginForEventType(item.EventType), timeout.Token);
                }
                if (deliveries.Any(delivery => delivery.Status is NotificationDeliveryStatus.Failed or NotificationDeliveryStatus.Pending)) return false;
            }
            var finishedAt = Clock.GetUtcNow().UtcDateTime;
            using var settleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var settled = await db.TaskStatusNotificationOutboxes
                .Where(entry => entry.Id == id && entry.CompletedAt == null && entry.LeaseToken == token && entry.LeaseUntil > finishedAt)
                .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.CompletedAt, (DateTime?)finishedAt)
                    .SetProperty(entry => entry.LeaseToken, (string?)null).SetProperty(entry => entry.LeaseUntil, (DateTime?)null), settleTimeout.Token);
            return settled == 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Log.LogWarning(error, "Task status notification event {EventId} remains queued.", id);
            return false;
        }
    }

    private async Task<ApprovalDecisionSnapshot?> LoadApprovalSnapshotAsync(TaskStatusNotificationOutbox item, CancellationToken ct)
    {
        var raw = await db.TaskJournalComments.AsNoTracking()
            .Where(comment => comment.TaskJournalItemId == item.TaskJournalItemId && comment.Kind == "system"
                && comment.MetadataJson.Contains(item.EventKey))
            .Select(comment => comment.MetadataJson).SingleOrDefaultAsync(ct);
        if (raw is null) return null;
        var snapshot = JsonSerializer.Deserialize<ApprovalDecisionSnapshot>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return snapshot is not null && snapshot.Kind == "approvalDecision" && snapshot.NotificationEventKey == item.EventKey
            && snapshot.Decision == "approved" && snapshot.ResultingStatus == "done"
            && snapshot.ParticipantId > 0 && snapshot.ActorUserId == item.ActorUserId
            && (!item.ApprovalParticipantId.HasValue || item.ApprovalParticipantId == snapshot.ParticipantId)
            ? snapshot : null;
    }

    private sealed record ApprovalDecisionSnapshot
    {
        public string Kind { get; init; } = string.Empty;
        public string NotificationEventKey { get; init; } = string.Empty;
        public int ActorUserId { get; init; }
        public int ParticipantId { get; init; }
        public int? ApproverUserId { get; init; }
        public string ApproverName { get; init; } = string.Empty;
        public string Decision { get; init; } = string.Empty;
        public string? Resolution { get; init; }
        public string? TaskResult { get; init; }
        public string ResultingStatus { get; init; } = string.Empty;
        public DateTime? RespondedAt { get; init; }
    }
}

public sealed class TaskStatusNotificationDispatchWorker(IServiceScopeFactory scopes,
    ILogger<TaskStatusNotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TaskStatusNotificationDispatchProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Task status notifications will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

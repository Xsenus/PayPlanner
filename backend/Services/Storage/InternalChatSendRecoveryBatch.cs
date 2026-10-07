using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Storage;

public sealed record InternalChatSendRecoveryBatchResult(int Inspected, int Claimed, int Published, int Deferred, int DeadLettered);

public sealed partial class InternalChatOriginalWriter
{
    public async Task<InternalChatSendRecoveryBatchResult> ProcessRecoveryBatchAsync(IStoragePolicyService policies,
        IProductStorageBindingService bindings, CancellationToken ct)
    {
        bool CanRun() => Enabled && options.CurrentValue.WorkerEnabled && options.CurrentValue.UploadCapacityChecksEnabled;
        if (!CanRun()) return new(0, 0, 0, 0, 0);
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat recovery batch requires an independent clean unit of work.");
        var now = DateTime.UtcNow;
        var paused = new[] { "conflict", "invalid", "deadLetter" };
        var candidates = await db.InternalChatSendAttempts.AsNoTracking()
            .Where(item => item.PublishedMessageId == null && item.Version < long.MaxValue && item.RecoveryNextAttemptAt <= now
                && (item.RecoveryLeaseToken == null || item.RecoveryLeaseExpiresAt <= now)
                && (item.RecoveryLastStatus == null || !paused.Contains(item.RecoveryLastStatus))
                && (item.RecoveryLastStatus != "needsInput" || item.AcceptedAt != null))
            .OrderBy(item => item.RecoveryNextAttemptAt).ThenBy(item => item.Id)
            .Take(Math.Clamp(options.CurrentValue.WorkerBatchSize, 1, 20))
            .Include(item => item.Files).Include(item => item.Mentions).AsSplitQuery().ToArrayAsync(ct);
        var claimed = 0;
        var published = 0;
        var deferred = 0;
        var deadLettered = 0;
        var registry = new InternalChatSendAttemptRegistry(db);
        foreach (var attempt in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!CanRun()) break;
            var token = Guid.NewGuid();
            if (!await ClaimAsync(attempt, token, ct)) continue;
            claimed++;
            var status = "unavailable";
            var preserveLease = false;
            try
            {
                var snapshot = InternalChatSendAttemptRegistry.TrySnapshot(attempt);
                if (snapshot is null) status = "conflict";
                else if (!await registry.CanParticipateAsync(attempt.ThreadId, attempt.ActorId, ct)) status = "forbidden";
                else if (!await registry.HasPayloadScopeAsync(snapshot, ct)) status = "conflict";
                else if (storage is IStorageOriginalReadService originals)
                    status = (await ContinueClaimedAsync(attempt, snapshot, originals, registry, token, policies, bindings, ct)).Status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or DbException or DbUpdateException)
            {
                // Provider failure or unknown commit ACK is not permission to
                // erase bytes/release early. Inspect a historical receipt first.
                preserveLease = true;
                var fresh = await registry.ReadOwnedAsync(attempt.ThreadId, attempt.ActorId, attempt.ClientRequestId, ct);
                var snapshot = fresh is null ? null : InternalChatSendAttemptRegistry.TrySnapshot(fresh);
                if (fresh?.PublishedMessageId.HasValue == true && snapshot is not null)
                {
                    status = (await ReadPublishedReceiptAsync(fresh, snapshot, ct)).Status;
                    if (status == "published") preserveLease = false;
                }
            }
            var maxAttempts = Math.Max(1, options.CurrentValue.WorkerMaxAttempts);
            if (status == "unavailable" && attempt.RecoveryAttemptCount >= maxAttempts - 1) status = "deadLetter";
            now = DateTime.UtcNow;
            var retry = status == "forbidden" ? now.AddMinutes(15) : now.Add(ChatRecoveryRetryDelay(attempt.Id, attempt.RecoveryAttemptCount));
            var recorded = await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id
                    && item.RecoveryLeaseToken == token && item.RequestSha256 == attempt.RequestSha256)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLastStatus, status)
                    .SetProperty(item => item.RecoveryAttemptCount, item => item.RecoveryAttemptCount < int.MaxValue ? item.RecoveryAttemptCount + 1 : int.MaxValue)
                    .SetProperty(item => item.RecoveryNextAttemptAt, item => preserveLease && item.RecoveryLeaseExpiresAt > retry
                        ? item.RecoveryLeaseExpiresAt!.Value : retry)
                    .SetProperty(item => item.RecoveryLeaseToken, item => preserveLease ? item.RecoveryLeaseToken : null)
                    .SetProperty(item => item.RecoveryLeaseExpiresAt, item => preserveLease ? item.RecoveryLeaseExpiresAt : null), ct);
            if (recorded == 0) continue; // Never replace a successor's outcome.
            if (status == "published") published++;
            else if (status == "deadLetter") deadLettered++;
            else deferred++;
        }
        return new(candidates.Length, claimed, published, deferred, deadLettered);
    }
}

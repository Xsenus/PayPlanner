using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class InternalChatOriginalWriter
{
    // Shared entry point for explicit resume and the future bounded worker. One
    // aggregate lease covers both phases; originals are not browser-dependent.
    public async Task<InternalChatSendPublicationResult> ContinueAsync(int threadId, int actorId, Guid clientRequestId,
        IStoragePolicyService policies, IProductStorageBindingService bindings, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat continuation requires an independent clean unit of work.");
        if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled");
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden");
        var attempt = await registry.ReadOwnedAsync(threadId, actorId, clientRequestId, ct);
        var snapshot = attempt is null ? null : InternalChatSendAttemptRegistry.TrySnapshot(attempt);
        if (attempt is null || snapshot is null) return new("conflict");
        // Validate historical domain receipts before any provider/staging work.
        if (attempt.PublishedMessageId.HasValue) return await ReadPublishedReceiptAsync(attempt, snapshot, ct);
        if (storage is not IStorageOriginalReadService originals) return new("unavailable", attempt.Id);
        if (!await registry.HasPayloadScopeAsync(snapshot, ct)) return new("conflict", attempt.Id);
        var token = Guid.NewGuid();
        if (!await ClaimAsync(attempt, token, ct)) return new("busy", attempt.Id);
        var result = await ContinueClaimedAsync(attempt, snapshot, originals, registry, token, policies, bindings, ct);
        // An exception/cancellation deliberately leaves its lease to expire. A
        // normal outcome releases only this token and records no private payload.
        var now = DateTime.UtcNow;
        var next = now.Add(ChatRecoveryRetryDelay(attempt.Id, attempt.RecoveryAttemptCount));
        var recorded = await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id
                && item.RecoveryLeaseToken == token && item.RequestSha256 == attempt.RequestSha256)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLastStatus, result.Status)
                .SetProperty(item => item.RecoveryNextAttemptAt, next)
                .SetProperty(item => item.RecoveryAttemptCount, item => item.RecoveryAttemptCount < int.MaxValue ? item.RecoveryAttemptCount + 1 : int.MaxValue)
                .SetProperty(item => item.RecoveryLeaseToken, (Guid?)null)
                .SetProperty(item => item.RecoveryLeaseExpiresAt, (DateTime?)null), ct);
        return recorded == 1 || result.MessageId.HasValue ? result : new("busy", attempt.Id);
    }

    private async Task<InternalChatSendPublicationResult> ContinueClaimedAsync(InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, IStorageOriginalReadService originals, InternalChatSendAttemptRegistry registry,
        Guid token, IStoragePolicyService policies, IProductStorageBindingService bindings, CancellationToken ct)
    {
        if (attempt.AcceptedAt is null)
        {
            var reads = new List<StorageObjectReadResult>();
            InternalChatOriginalWriteResult resumed;
            try { resumed = await ResumeClaimedAsync(attempt, snapshot, originals, reads, token, registry, ct); }
            finally { foreach (var read in reads) await read.DisposeAsync(); }
            if (!resumed.Accepted) return new(resumed.Status, attempt.Id);
            // Pointer/acceptance updates are bulk receipts. Reload the complete
            // frozen aggregate instead of trusting the original in-memory graph.
            var accepted = await registry.ReadOwnedAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
            if (accepted is null || accepted.Id != attempt.Id || accepted.RequestSha256 != attempt.RequestSha256
                || accepted.AcceptedAt is null || accepted.PublishedMessageId.HasValue) return new("conflict", attempt.Id);
            attempt = accepted;
        }
        return await PublishClaimedAsync(attempt, snapshot, originals, registry, token, policies, bindings, ct);
    }

    // Same bounded exponential delay and stable jitter as the storage queue.
    private static TimeSpan ChatRecoveryRetryDelay(Guid attemptId, int previousAttempts)
        => TimeSpan.FromSeconds(Math.Min(900, Math.Pow(2, Math.Clamp((long)previousAttempts + 1, 1, 10)))
            + attemptId.ToByteArray()[0] % 17);
}

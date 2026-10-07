using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Storage;

public sealed record InternalChatSendStatusResult(string Status, Guid? AttemptId = null, int? MessageId = null,
    bool AutomaticRecovery = false, DateTime? RetryAfter = null);

public sealed partial class InternalChatOriginalWriter
{
    // Catalog-only status: no claim, writes, staging or provider I/O. A historical
    // publication remains provable when new managed writes have been disabled.
    public async Task<InternalChatSendStatusResult> ReadStatusAsync(int threadId, int actorId, Guid clientRequestId,
        StorageWorkerAvailability workerAvailability, CancellationToken ct)
    {
        if (threadId <= 0 || actorId <= 0 || clientRequestId == Guid.Empty) return new("invalid");
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden");
        var attempt = await db.InternalChatSendAttempts.AsNoTracking()
            .Where(item => item.ThreadId == threadId && item.ActorId == actorId && item.ClientRequestId == clientRequestId)
            .Include(item => item.Files).Include(item => item.Mentions).AsSplitQuery().SingleOrDefaultAsync(ct);
        if (attempt is null) return await registry.CanParticipateAsync(threadId, actorId, ct) ? new("notFound") : new("forbidden");
        var snapshot = InternalChatSendAttemptRegistry.TrySnapshot(attempt);
        InternalChatSendStatusResult result;
        if (snapshot is null) result = new("conflict", attempt.Id);
        else if (attempt.PublishedMessageId.HasValue)
        {
            var receipt = await ReadPublishedReceiptAsync(attempt, snapshot, ct);
            result = new(receipt.Status, attempt.Id, receipt.MessageId);
        }
        else if (!await registry.HasPayloadScopeAsync(snapshot, ct)
            || attempt.AcceptedAt.HasValue && !await AllOriginalsMatchAsync(attempt, snapshot, ct))
            result = new("conflict", attempt.Id);
        else
        {
            var settings = options.CurrentValue;
            var enabled = Enabled && settings.UploadCapacityChecksEnabled;
            var automatic = enabled && settings.WorkerEnabled && workerAvailability.IsRegistered;
            var now = DateTime.UtcNow;
            var leased = attempt.RecoveryLeaseToken.HasValue && attempt.RecoveryLeaseExpiresAt > now;
            var status = attempt.RecoveryLastStatus switch
            {
                _ when attempt.Version == long.MaxValue => "failed",
                "needsInput" when !attempt.AcceptedAt.HasValue => "needsInput",
                "conflict" or "invalid" => "conflict",
                "deadLetter" => "failed",
                _ when !enabled => "paused",
                _ when leased => "processing",
                _ when !automatic => "paused",
                "unavailable" => "retrying",
                _ => "waiting"
            };
            // Terminal/manual states do not promise automatic completion.
            automatic &= status is "waiting" or "retrying" or "processing";
            var next = leased ? attempt.RecoveryLeaseExpiresAt : attempt.RecoveryNextAttemptAt;
            result = new(status, attempt.Id, AutomaticRecovery: automatic,
                RetryAfter: automatic && next > now ? next : null);
        }
        // Access may have changed while reading the receipt. Do not disclose even
        // an attempt/message identity after removal from the thread or task.
        return await registry.CanParticipateAsync(threadId, actorId, ct) ? result : new("forbidden");
    }
}

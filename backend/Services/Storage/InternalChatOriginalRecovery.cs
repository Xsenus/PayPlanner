using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class InternalChatOriginalWriter
{
    public async Task<InternalChatOriginalWriteResult> ResumeAsync(int threadId, int actorId, Guid clientRequestId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat original recovery requires an independent clean unit of work.");
        if (!Enabled) return new("disabled");
        if (!options.CurrentValue.UploadCapacityChecksEnabled || storage is not IStorageOriginalReadService originals) return new("unavailable");
        if (threadId <= 0 || actorId <= 0 || clientRequestId == Guid.Empty) return new("invalid");
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden");
        var attempt = await registry.ReadOwnedAsync(threadId, actorId, clientRequestId, ct);
        var snapshot = attempt is null ? null : InternalChatSendAttemptRegistry.TrySnapshot(attempt);
        if (attempt is null || snapshot is null) return new("conflict");
        // A historical marker requires the domain publisher's receipt validation.
        // Never read bytes or start a second send after publication/deletion.
        if (attempt.PublishedMessageId.HasValue) return new("published", attempt.Id);
        if (snapshot.Files.Any(file => ForbiddenExtensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase)))
            return new("invalid", attempt.Id);
        var token = Guid.NewGuid();
        if (!await ClaimAsync(attempt, token, ct)) return new("busy", attempt.Id);
        var reads = new List<StorageObjectReadResult>();
        try
        {
            var result = await ResumeClaimedAsync(attempt, snapshot, originals, reads, token, registry, ct);
            await ReleaseAsync(attempt.Id, token, ct);
            return result;
        }
        finally { foreach (var read in reads) await read.DisposeAsync(); }
    }

    private async Task<InternalChatOriginalWriteResult> ResumeClaimedAsync(InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, IStorageOriginalReadService originals, List<StorageObjectReadResult> reads,
        Guid token, InternalChatSendAttemptRegistry registry, CancellationToken ct)
    {
        var keys = Enumerable.Range(0, snapshot.Files.Length).Select(index => StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, index))).ToArray();
        // Inspect every catalog receipt before any provider reads. A corrupt second
        // file must not cause recovery to reuse an unrelated first intent.
        var commits = await ReadOriginalReceiptsAsync(snapshot, ct);
        var capacityKeys = Enumerable.Range(0, snapshot.Files.Length)
            .Where(index => !commits.ContainsKey(keys[index]))
            .Select(index => StorageObjectService.GetCapacityReservationKey(WriteKey(snapshot, index))).ToArray();
        var localResource = StorageCapacityAdmission.ResourceKey("local", null);
        var reservations = await db.StorageCapacityReservations.AsNoTracking()
            .Where(item => capacityKeys.Contains(item.IdempotencyKey) && item.ResourceKey == localResource)
            .Select(item => new { item.IdempotencyKey, item.ProductContextJson, item.Bytes, item.ObjectId, item.Status })
            .ToDictionaryAsync(item => item.IdempotencyKey, ct);
        foreach (var file in attempt.Files)
        {
            if (commits.TryGetValue(keys[file.Index], out var commit))
            {
                if (commit.PayloadJson != Context(attempt, file.Index) || commit.OperationType != "commit" || !commit.Belongs
                    || commit.Generation is not { } generation
                    || file.StorageObjectId.HasValue && (file.StorageObjectId != commit.StorageObjectId || file.StorageGeneration != generation))
                    return new("conflict", attempt.Id);
                if (commit.Status != StorageOperationStatuses.Succeeded) return new("unavailable", attempt.Id);
                if (!IsOriginalReceipt(commit, attempt, snapshot, file.Index, commit.StorageObjectId, generation)) return new("conflict", attempt.Id);
            }
            else
            {
                if (file.StorageObjectId.HasValue) return new("conflict", attempt.Id);
                var capacityKey = StorageObjectService.GetCapacityReservationKey(WriteKey(snapshot, file.Index));
                if (!reservations.TryGetValue(capacityKey, out var reservation))
                    return new(attempt.AcceptedAt.HasValue ? "unavailable" : "needsInput", attempt.Id);
                if (reservation.Status != StorageCapacityReservationStatuses.Pending || reservation.ObjectId == Guid.Empty
                    || reservation.Bytes != file.SizeBytes || reservation.ProductContextJson != Context(attempt, file.Index))
                    return new("conflict", attempt.Id);
            }
        }
        for (var index = 0; index < snapshot.Files.Length; index++)
        {
            if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled", attempt.Id);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
            var file = snapshot.Files[index];
            var read = await originals.OpenOriginalReadAsync(new("chat-attachment", "chat-attachment", Owner(attempt.Id, index),
                file.FileName, file.ContentType, Stream.Null, WriteKey(snapshot, index), snapshot.ActorId,
                Context(attempt, index), file.SizeBytes, file.Sha256), ct);
            if (read is null) return new("unavailable", attempt.Id);
            reads.Add(read);
            var expectedObjectId = commits.TryGetValue(keys[index], out var originalCommit) ? originalCommit.StorageObjectId
                : reservations[StorageObjectService.GetCapacityReservationKey(WriteKey(snapshot, index))].ObjectId;
            if (read.SizeBytes != file.SizeBytes || read.Sha256 != file.Sha256 || read.LogicalName != file.FileName
                || read.ContentType != file.ContentType || read.ProviderKey != "local" || read.Generation != 1 || read.ObjectId != expectedObjectId)
                return new("conflict", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
        }
        return await WriteClaimedAsync(attempt, snapshot, index => new FormFile(reads[index].Content, 0,
            snapshot.Files[index].SizeBytes, "files", snapshot.Files[index].FileName).OpenReadStream(), token, registry, ct);
    }

    private async Task<bool> ClaimAsync(InternalChatSendAttempt attempt, Guid token, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expires = LeaseExpiry(now);
        return await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id
                && item.Version == attempt.Version && item.RequestSha256 == attempt.RequestSha256
                && item.PublishedMessageId == null && item.Version < long.MaxValue
                && (item.RecoveryLeaseToken == null || item.RecoveryLeaseExpiresAt <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, token)
                .SetProperty(item => item.RecoveryLeaseExpiresAt, expires)
                .SetProperty(item => item.Version, item => item.Version + 1), ct) == 1;
    }

    private Task<int> ReleaseAsync(Guid attemptId, Guid token, CancellationToken ct)
        => db.InternalChatSendAttempts.Where(item => item.Id == attemptId && item.RecoveryLeaseToken == token)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, (Guid?)null)
                .SetProperty(item => item.RecoveryLeaseExpiresAt, (DateTime?)null), ct);
}

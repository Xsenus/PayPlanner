using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record InternalChatSendPublicationResult(string Status, Guid? AttemptId = null, int? MessageId = null);

public sealed partial class InternalChatOriginalWriter
{
    // Internal core. The HTTP caller dispatches existing notifications/realtime
    // only after this result; the committed pending flag also supports recovery.
    public async Task<InternalChatSendPublicationResult> PublishAsync(int threadId, int actorId, Guid clientRequestId,
        IStoragePolicyService policies, IProductStorageBindingService bindings, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat publication requires an independent clean unit of work.");
        if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled");
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden");
        var attempt = await registry.ReadOwnedAsync(threadId, actorId, clientRequestId, ct);
        var snapshot = attempt is null ? null : InternalChatSendAttemptRegistry.TrySnapshot(attempt);
        if (attempt is null || snapshot is null) return new("conflict");
        if (attempt.PublishedMessageId.HasValue) return await ReadPublishedReceiptAsync(attempt, snapshot, ct);
        if (attempt.AcceptedAt is null) return new("notReady", attempt.Id);
        if (storage is not IStorageOriginalReadService originals) return new("unavailable", attempt.Id);
        if (!await registry.HasPayloadScopeAsync(snapshot, ct)) return new("conflict", attempt.Id);
        var token = Guid.NewGuid();
        if (!await ClaimAsync(attempt, token, ct)) return new("busy", attempt.Id);
        var result = await PublishClaimedAsync(attempt, snapshot, originals, registry, token, policies, bindings, ct);
        await ReleaseAsync(attempt.Id, token, ct);
        return result;
    }

    private async Task<InternalChatSendPublicationResult> PublishClaimedAsync(InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, IStorageOriginalReadService originals, InternalChatSendAttemptRegistry registry,
        Guid token, IStoragePolicyService policies, IProductStorageBindingService bindings, CancellationToken ct)
    {
        if (!await AllOriginalsMatchAsync(attempt, snapshot, ct)) return new("conflict", attempt.Id);
        if (!await AllCopiesReadyAsync(attempt, policies, ct)) return new("notReady", attempt.Id);
        // Compatibility bytes stay outside the database transaction. Deterministic
        // targets survive rollback/lost commit ACK and are never overwritten/deleted.
        for (var index = 0; index < snapshot.Files.Length; index++)
        {
            if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled", attempt.Id);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
            if (!await EnsureChatCompatibilityAsync(attempt, snapshot, index, originals, ct)) return new("unavailable", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var graph = new List<object>();
        try
        {
            if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
            // Same thread lock as metadata capture serializes aggregate publication.
            if (await db.InternalChatThreads.Where(item => item.Id == snapshot.ThreadId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct) != 1)
                return new("conflict", attempt.Id);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden", attempt.Id);
            if (!await registry.HasPayloadScopeAsync(snapshot, ct)) return new("conflict", attempt.Id);
            var fresh = await registry.ReadOwnedAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
            if (fresh is null || fresh.Id != attempt.Id || fresh.RequestSha256 != attempt.RequestSha256
                || fresh.AcceptedAt is null || fresh.PublishedMessageId.HasValue) return new("conflict", attempt.Id);
            foreach (var file in fresh.Files.OrderBy(item => item.Index))
                if (file.StorageObjectId is not { } objectId || file.StorageGeneration is not { } generation
                    || !await LockObjectAsync(objectId, generation, ct)) return new("conflict", attempt.Id);
            if (!await AllOriginalsMatchAsync(fresh, snapshot, ct)) return new("conflict", attempt.Id);
            if (!await AllCopiesReadyAsync(fresh, policies, ct)) return new("notReady", attempt.Id);
            if (await db.InternalChatMessages.AnyAsync(item => item.ThreadId == snapshot.ThreadId
                && item.AuthorUserId == snapshot.ActorId && item.ClientRequestId == snapshot.ClientRequestId, ct))
                return new("conflict", attempt.Id); // Never adopt a legacy send.
            var now = DateTime.UtcNow;
            var message = new InternalChatMessage
            {
                ThreadId = snapshot.ThreadId,
                AuthorUserId = snapshot.ActorId,
                Text = snapshot.Text,
                ClientRequestId = snapshot.ClientRequestId,
                ReplyToMessageId = snapshot.ReplyToMessageId,
                CreatedAt = now,
                NotificationDispatchPending = true,
                LastNotificationDispatchAttemptAt = now,
                Mentions = snapshot.MentionUserIds.Select(id => new InternalChatMention { UserId = id }).ToList(),
                Attachments = snapshot.Files.Select((file, index) => new InternalChatAttachment
                {
                    FileName = file.FileName,
                    StoredFileName = ChatCompatibilityName(attempt.Id, index),
                    ContentType = file.ContentType,
                    Size = file.SizeBytes,
                    UploadedByUserId = snapshot.ActorId,
                    CreatedAt = now
                }).ToList()
            };
            graph.Add(message);
            graph.AddRange(message.Attachments);
            graph.AddRange(message.Mentions);
            db.InternalChatMessages.Add(message);
            await db.SaveChangesAsync(ct);
            var attachments = message.Attachments.ToArray();
            foreach (var file in fresh.Files.OrderBy(item => item.Index))
            {
                var owner = Owner(attempt.Id, file.Index);
                var attachment = attachments[file.Index];
                var newOwner = attachment.Id.ToString(CultureInfo.InvariantCulture);
                if (await db.StorageObjects.Where(item => item.Id == file.StorageObjectId && !item.IsDeleted
                        && !item.Tombstones.Any() && item.CurrentGeneration == file.StorageGeneration
                        && item.OwnerType == "chat-attachment" && item.OwnerId == owner && item.Version < long.MaxValue)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.OwnerId, newOwner)
                        .SetProperty(item => item.Version, item => item.Version + 1).SetProperty(item => item.UpdatedAt, now), ct) != 1)
                    return new("conflict", attempt.Id);
                var binding = await bindings.PublishAsync(new(LegacyStorageSources.InternalChat, attachment.Id,
                    file.StorageObjectId!.Value, attachment.FileName, attachment.ContentType, 0), ct);
                if (!binding.Published) return new(binding.Status, attempt.Id);
            }
            await db.InternalChatThreads.Where(item => item.Id == snapshot.ThreadId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.LastMessageAt, now).SetProperty(item => item.UpdatedAt, now), ct);
            if (snapshot.ReplyToMessageId is { } replyId)
                await db.InternalChatMessages.Where(item => item.Id == replyId && item.ThreadId == snapshot.ThreadId
                        && item.DeletedAt == null && item.AuthorUserId.HasValue && item.AuthorUserId != snapshot.ActorId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.NextNoReplyReminderAt, (DateTime?)null), ct);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)
                || !await registry.HasPayloadScopeAsync(snapshot, ct) || !Enabled
                || !options.CurrentValue.UploadCapacityChecksEnabled || !await AllCopiesReadyAsync(fresh, policies, ct)
                || !await FenceAsync(attempt, token, ct))
                return new("busy", attempt.Id);
            var changed = await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id && item.RecoveryLeaseToken == token
                    && item.RequestSha256 == attempt.RequestSha256 && item.AcceptedAt != null && item.PublishedMessageId == null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.PublishedMessageId, message.Id), ct);
            if (changed != 1) return new("busy", attempt.Id);
            await transaction.CommitAsync(ct);
            return new("published", attempt.Id, message.Id);
        }
        finally
        {
            // Do not clear unrelated tracked entities or erase compatibility bytes
            // after an uncertain commit. A fresh retry validates the historical marker.
            foreach (var item in graph) db.Entry(item).State = EntityState.Detached;
        }
    }

    private async Task<bool> AllOriginalsMatchAsync(InternalChatSendAttempt attempt, InternalChatSendSnapshot snapshot, CancellationToken ct)
    {
        var receipts = await ReadOriginalReceiptsAsync(snapshot, ct);
        return attempt.Files.All(file => file.StorageObjectId is { } objectId && file.StorageGeneration is { } generation
            && receipts.TryGetValue(StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, file.Index)), out var receipt)
            && IsOriginalReceipt(receipt, attempt, snapshot, file.Index, objectId, generation));
    }

    private async Task<bool> AllCopiesReadyAsync(InternalChatSendAttempt attempt, IStoragePolicyService policies, CancellationToken ct)
    {
        var policy = policies.Evaluate("chat-attachment");
        if (!policy.RoutingEnabled || !policy.IsValid) return false;
        var providers = policy.WriteProviders.Select(item => item.ToLowerInvariant()).Distinct().ToArray();
        var objectIds = attempt.Files.Select(item => item.StorageObjectId).ToArray();
        var ready = await db.StorageObjectGenerations.AsNoTracking().Where(generation => objectIds.Contains(generation.StorageObjectId)
                && generation.Generation == generation.StorageObject.CurrentGeneration
                && !generation.Operations.Any(operation => operation.OperationType == "commit" && operation.Status != StorageOperationStatuses.Succeeded)
                && generation.Replicas.Where(replica => replica.Status == StorageReplicaStatuses.Available
                    && replica.LastVerifiedAt != null && replica.SizeBytes == generation.SizeBytes && replica.Sha256 == generation.Sha256
                    && providers.Contains(replica.ProviderKey.ToLower())).Select(replica => replica.ProviderKey.ToLower()).Distinct().Count() >= policy.RequiredCopies)
            .Select(generation => new { generation.StorageObjectId, generation.Generation }).ToArrayAsync(ct);
        return attempt.Files.All(file => ready.Any(item => item.StorageObjectId == file.StorageObjectId && item.Generation == file.StorageGeneration));
    }

    private static string ChatCompatibilityName(Guid attemptId, int index) => $"{attemptId:N}-{index}.bin";

    private async Task<bool> EnsureChatCompatibilityAsync(InternalChatSendAttempt attempt, InternalChatSendSnapshot snapshot,
        int index, IStorageOriginalReadService originals, CancellationToken ct)
    {
        var file = snapshot.Files[index];
        var directory = Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration, AppContext.BaseDirectory),
            "internal-chat-files", snapshot.ThreadId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, ChatCompatibilityName(attempt.Id, index));
        async Task<bool> MatchesAsync()
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return false;
            await using var input = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return input.Length == file.SizeBytes && Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant() == file.Sha256;
        }
        if (File.Exists(target)) return await MatchesAsync();
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        var (ticket, _) = await stages.ReserveCompatibilityAsync(WriteKey(snapshot, index) + ":compat", temporary, file.SizeBytes, "files", ct);
        if (ticket is null) return false;
        var created = false;
        try
        {
            await using var read = await originals.OpenOriginalReadAsync(new("chat-attachment", "chat-attachment", Owner(attempt.Id, index),
                file.FileName, file.ContentType, Stream.Null, WriteKey(snapshot, index), snapshot.ActorId,
                Context(attempt, index), file.SizeBytes, file.Sha256), ct);
            var identity = attempt.Files.Single(item => item.Index == index);
            if (read is null || read.ObjectId != identity.StorageObjectId || read.Generation != identity.StorageGeneration
                || read.ProviderKey != "local" || read.SizeBytes != file.SizeBytes || read.Sha256 != file.Sha256
                || read.LogicalName != file.FileName || read.ContentType != file.ContentType) return false;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created = true;
                var input = new FormFile(read.Content, 0, file.SizeBytes, "files", file.FileName);
                if (await UploadValidationHelper.CopyValidatedAndHashAsync(input, output, MaxFileBytes, ct) != file.Sha256) return false;
            }
            await stages.SettlePhysicalAsync(ticket, ct);
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target)) { return await MatchesAsync(); }
            return true;
        }
        finally
        {
            try
            {
                try
                {
                    if (created && File.Exists(temporary)) File.Delete(temporary);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await stages.ReleaseAsync(ticket, cleanup.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
                { /* Keep durable debt; do not mask a provider error or release foreign bytes. */ }
            }
            finally
            {
                await ticket.CloseWriterAsync();
            }
        }
    }

    private async Task<InternalChatSendPublicationResult> ReadPublishedReceiptAsync(InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, CancellationToken ct)
    {
        var message = await db.InternalChatMessages.AsNoTracking().Include(item => item.Attachments).Include(item => item.Mentions)
            .AsSplitQuery().SingleOrDefaultAsync(item => item.Id == attempt.PublishedMessageId, ct);
        if (message is null || message.DeletedAt.HasValue) return new("deleted", attempt.Id);
        if (message.ThreadId != snapshot.ThreadId || message.AuthorUserId != snapshot.ActorId || message.ClientRequestId != snapshot.ClientRequestId
            || message.Attachments.Count != snapshot.Files.Length || message.EditedAt is null && (message.Text != snapshot.Text
                || message.ReplyToMessageId != snapshot.ReplyToMessageId
                || !message.Mentions.Select(item => item.UserId).Order().SequenceEqual(snapshot.MentionUserIds))) return new("conflict", attempt.Id);
        var receipts = await ReadOriginalReceiptsAsync(snapshot, ct);
        var attachmentIds = message.Attachments.Select(item => (long)item.Id).ToArray();
        var bindings = await db.StorageProductBindings.AsNoTracking().Where(item => item.Source == LegacyStorageSources.InternalChat
            && attachmentIds.Contains(item.SourceId)).ToDictionaryAsync(item => item.SourceId, ct);
        foreach (var file in attempt.Files)
        {
            var attachment = message.Attachments.SingleOrDefault(item => item.StoredFileName == ChatCompatibilityName(attempt.Id, file.Index));
            if (attachment is null || attachment.FileName != file.FileName || attachment.ContentType != file.ContentType
                || attachment.Size != file.SizeBytes || attachment.UploadedByUserId != snapshot.ActorId
                || file.StorageObjectId is not { } objectId || file.StorageGeneration is not { } generation
                || !bindings.TryGetValue(attachment.Id, out var binding) || binding.StorageObjectId != objectId || binding.Version <= 0
                || !receipts.TryGetValue(StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, file.Index)), out var receipt)
                || !IsOriginalReceipt(receipt, attempt, snapshot, file.Index, objectId, generation, attachment.Id.ToString(CultureInfo.InvariantCulture)))
                return new("conflict", attempt.Id);
        }
        return new("published", attempt.Id, message.Id);
    }
}

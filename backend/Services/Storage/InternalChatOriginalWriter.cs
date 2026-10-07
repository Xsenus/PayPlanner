using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record InternalChatOriginalWriteResult(string Status, Guid? AttemptId = null, bool Accepted = false);

// Internal aggregate writer only. Accepted originals do not grant permission to
// publish a message: that separately requires every policy copy and domain guard.
public sealed partial class InternalChatOriginalWriter(PaymentContext db, IStorageObjectService storage,
    IStorageStageCapacityService stages, IWebHostEnvironment environment, IConfiguration configuration,
    IOptionsMonitor<StorageOrchestrationOptions> options)
{
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private static readonly string[] ForbiddenExtensions = [".exe", ".dll", ".cmd", ".bat", ".com", ".msi", ".ps1", ".js", ".vbs", ".scr"];
    public bool Enabled => options.CurrentValue.Enabled
        && options.CurrentValue.ProductWriteEnabledClasses.Contains("chat-attachment", StringComparer.Ordinal);

    public async Task<InternalChatOriginalWriteResult> WriteAsync(InternalChatSendSnapshot snapshot,
        IReadOnlyList<IFormFile> files, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat original writer requires an independent clean unit of work.");
        if (!Enabled) return new("disabled");
        if (!options.CurrentValue.UploadCapacityChecksEnabled) return new("unavailable");
        // Copy mutable caller collections before the first await.
        snapshot = snapshot with { Files = snapshot.Files.ToArray(), MentionUserIds = snapshot.MentionUserIds.ToArray() };
        var inputs = files.ToArray();
        if (!InternalChatSendProof.IsValid(snapshot) || inputs.Length != snapshot.Files.Length
            || snapshot.Files.Any(file => ForbiddenExtensions.Contains(Path.GetExtension(file.FileName), StringComparer.OrdinalIgnoreCase)))
            return new("invalid");
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden");
        var root = Path.Combine(LocalStoragePaths.ResolveRoot(environment, configuration), ".chat-originals");
        var staged = new List<(string Path, StorageStageCapacityTicket Ticket, bool Created)>();
        try
        {
            // Validate ALL original bytes before capturing metadata or writing any
            // managed object. A changed second attachment cannot acknowledge replay.
            for (var index = 0; index < inputs.Length; index++)
            {
                var input = inputs[index];
                var expected = snapshot.Files[index];
                if (input.Length != expected.SizeBytes) return new("invalid");
                var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                Directory.CreateDirectory(root);
                var (ticket, _) = await stages.ReserveAsync(WriteKey(snapshot, index), path, input.Length, ct);
                if (ticket is null) return new("unavailable");
                staged.Add((path, ticket, false));
                string sha;
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    staged[^1] = (path, ticket, true);
                    try { sha = await UploadValidationHelper.CopyValidatedAndHashAsync(input, output, MaxFileBytes, ct); }
                    catch (InvalidDataException) { return new("invalid"); }
                }
                await stages.SettlePhysicalAsync(ticket, ct);
                if (sha != expected.Sha256) return new("conflict");
            }
            var capture = await registry.CaptureAsync(snapshot, ct);
            if (capture.Status != "recorded") return new(capture.Status);
            var attempt = await registry.ReadOwnedAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
            if (attempt is null || attempt.Id != capture.AttemptId) return new("conflict");
            // Published receipts belong to the later domain publisher, never this writer.
            if (attempt.PublishedMessageId.HasValue) return new("published", attempt.Id);
            var token = Guid.NewGuid();
            if (!await ClaimAsync(attempt, token, ct)) return new("busy", attempt.Id);
            var result = await WriteClaimedAsync(attempt, snapshot,
                index => new FileStream(staged[index].Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true), token, registry, ct);
            await ReleaseAsync(attempt.Id, token, ct);
            return result;
        }
        finally
        {
            try
            {
                // Delete only this invocation's raw staging files. Never remove reserved
                // managed bytes after failure or an uncertain commit acknowledgement.
                foreach (var (path, ticket, created) in staged)
                {
                    try
                    {
                        if (created) File.Delete(path);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await stages.ReleaseAsync(ticket, cleanup.Token);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
                        || !created && error is InvalidOperationException)
                    { /* Durable reservation remains for the existing stage reconciler. */ }
                }
            }
            finally
            {
                foreach (var stagedFile in staged) await stagedFile.Ticket.CloseWriterAsync();
            }
        }
    }

    private async Task<InternalChatOriginalWriteResult> WriteClaimedAsync(InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, Func<int, Stream> openInput,
        Guid token, InternalChatSendAttemptRegistry registry, CancellationToken ct)
    {
        for (var index = 0; index < snapshot.Files.Length; index++)
        {
            if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled", attempt.Id);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden", attempt.Id);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
            var file = snapshot.Files[index];
            await using var input = openInput(index);
            var write = await storage.WriteAsync(new("chat-attachment", "chat-attachment", Owner(attempt.Id, index),
                file.FileName, file.ContentType, input, WriteKey(snapshot, index), snapshot.ActorId,
                Context(attempt, index), file.SizeBytes, file.Sha256), ct);
            if (write.ObjectId == Guid.Empty) return new("unavailable", attempt.Id);
            // A provider's returned object id/status alone is never a trusted receipt.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
            if (!await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden", attempt.Id);
            if (!await LockObjectAsync(write.ObjectId, write.Generation, ct)) return new("conflict", attempt.Id);
            if (!await VerifyOriginalAsync(attempt, snapshot, index, write.ObjectId, write.Generation, ct))
                return new("conflict", attempt.Id);
            var row = await db.InternalChatSendAttemptFiles.SingleAsync(item => item.AttemptId == attempt.Id && item.Index == index, ct);
            try
            {
                if (row.StorageObjectId.HasValue && (row.StorageObjectId != write.ObjectId || row.StorageGeneration != write.Generation))
                    return new("conflict", attempt.Id);
                row.StorageObjectId = write.ObjectId;
                row.StorageGeneration = write.Generation;
                await db.SaveChangesAsync(ct);
                if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
                await tx.CommitAsync(ct);
            }
            finally { db.Entry(row).State = EntityState.Detached; }
        }
        await using var acceptance = await db.Database.BeginTransactionAsync(ct);
        if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) return new("disabled", attempt.Id);
        if (!await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
        var fresh = await registry.ReadOwnedAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
        if (fresh is null || fresh.Id != attempt.Id || fresh.RequestSha256 != attempt.RequestSha256 || fresh.PublishedMessageId.HasValue)
            return new("conflict", attempt.Id);
        foreach (var file in fresh.Files)
            if (file.StorageObjectId is not { } objectId || file.StorageGeneration is not { } generation
                || !await LockObjectAsync(objectId, generation, ct)) return new("conflict", attempt.Id);
        var receipts = await ReadOriginalReceiptsAsync(snapshot, ct);
        foreach (var file in fresh.Files)
            if (!receipts.TryGetValue(StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, file.Index)), out var receipt)
                || !IsOriginalReceipt(receipt, fresh, snapshot, file.Index, file.StorageObjectId!.Value, file.StorageGeneration!.Value))
                return new("conflict", attempt.Id);
        var acceptedAt = fresh.AcceptedAt ?? DateTime.UtcNow;
        var changed = await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id && item.RecoveryLeaseToken == token
                && item.RequestSha256 == attempt.RequestSha256 && item.PublishedMessageId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.AcceptedAt, acceptedAt), ct);
        if (changed != 1 || !await registry.CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)
            || !await FenceAsync(attempt, token, ct)) return new("busy", attempt.Id);
        await acceptance.CommitAsync(ct);
        return new("accepted", attempt.Id, true);
    }

    private async Task<bool> FenceAsync(InternalChatSendAttempt attempt, Guid token, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expires = LeaseExpiry(now);
        return await db.InternalChatSendAttempts.Where(item => item.Id == attempt.Id && item.RecoveryLeaseToken == token
                && item.RecoveryLeaseExpiresAt > now && item.RequestSha256 == attempt.RequestSha256 && item.PublishedMessageId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expires), ct) == 1;
    }

    private async Task<bool> LockObjectAsync(Guid objectId, long generation, CancellationToken ct)
        => await db.StorageObjects.Where(item => item.Id == objectId && !item.IsDeleted && item.CurrentGeneration == generation
                && !item.Tombstones.Any())
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, item => item.Version), ct) == 1;

    private DateTime LeaseExpiry(DateTime now) => now.AddSeconds(Math.Clamp(options.CurrentValue.WorkerLeaseSeconds, 15, 3600));
    internal static string WriteKey(InternalChatSendSnapshot snapshot, int index)
        => $"chat-attachment:{snapshot.ThreadId}:{snapshot.ActorId}:{snapshot.ClientRequestId:N}:{index}";
    internal static string Owner(Guid attemptId, int index) => $"chat-send:{attemptId:N}:{index}";
    internal static string Context(InternalChatSendAttempt attempt, int index)
        => JsonSerializer.Serialize(new { attempt.Id, attempt.ThreadId, attempt.ActorId, attempt.ClientRequestId, Index = index, attempt.RequestSha256 });
}

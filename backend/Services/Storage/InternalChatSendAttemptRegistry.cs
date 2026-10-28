using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Server-only snapshots: file hashes come from validated original streams,
// never from a public request DTO or a caller-supplied catalog identifier.
public sealed record InternalChatSendFileSnapshot(string FileName, string ContentType, long SizeBytes, string Sha256);
public sealed record InternalChatSendSnapshot(int ThreadId, int ActorId, Guid ClientRequestId,
    string Text, int? ReplyToMessageId, int[] MentionUserIds, InternalChatSendFileSnapshot[] Files);
public sealed record InternalChatSendCapture(string Status, Guid? AttemptId = null, string? RequestSha256 = null, bool IsReplay = false);

public static class InternalChatSendProof
{
    public const int MaximumFiles = 10;
    public const long MaximumFileSize = 20L * 1024 * 1024;
    private static readonly UTF8Encoding ProofEncoding = new(false, true);

    public static bool IsValid(InternalChatSendSnapshot? snapshot)
        => snapshot is not null && snapshot.ThreadId > 0 && snapshot.ActorId > 0 && snapshot.ClientRequestId != Guid.Empty
            && snapshot.Text is { Length: <= 8000 } && snapshot.Text == snapshot.Text.Trim()
            && WellFormed(snapshot.Text)
            && (snapshot.ReplyToMessageId is null or > 0)
            && snapshot.MentionUserIds is not null && snapshot.MentionUserIds.All(id => id > 0)
            && snapshot.MentionUserIds.SequenceEqual(snapshot.MentionUserIds.Distinct().Order())
            && snapshot.Files is { Length: >= 1 and <= MaximumFiles }
            && snapshot.Files.All(file => file is not null
                && !string.IsNullOrWhiteSpace(file.FileName) && file.FileName.Length <= 260
                && file.FileName == file.FileName.Trim() && !file.FileName.Contains('/') && !file.FileName.Contains('\\')
                && WellFormed(file.FileName)
                && !string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType.Length <= 160
                && file.ContentType == file.ContentType.Trim()
                && WellFormed(file.ContentType)
                && file.SizeBytes is > 0 and <= MaximumFileSize
                && file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit)
                && file.Sha256 == file.Sha256.ToLowerInvariant());

    public static string Hash(InternalChatSendSnapshot snapshot)
    {
        if (!IsValid(snapshot)) throw new ArgumentException("Invalid internal chat send proof.", nameof(snapshot));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "payplanner-chat-send-v1");
        AppendInteger(hash, snapshot.ThreadId);
        AppendInteger(hash, snapshot.ActorId);
        hash.AppendData(snapshot.ClientRequestId.ToByteArray());
        AppendString(hash, snapshot.Text);
        AppendInteger(hash, snapshot.ReplyToMessageId ?? 0);
        AppendInteger(hash, snapshot.MentionUserIds.Length);
        foreach (var id in snapshot.MentionUserIds) AppendInteger(hash, id);
        AppendInteger(hash, snapshot.Files.Length);
        foreach (var file in snapshot.Files)
        {
            AppendString(hash, file.FileName);
            AppendString(hash, file.ContentType);
            AppendInteger(hash, file.SizeBytes);
            AppendString(hash, file.Sha256);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string HashFrozen(InternalChatSendSnapshot snapshot, Guid attemptId, DateTime createdAt)
    {
        if (attemptId == Guid.Empty) throw new ArgumentException("Invalid frozen attempt identity.", nameof(attemptId));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "payplanner-chat-frozen-v1");
        AppendString(hash, Hash(snapshot));
        hash.AppendData(attemptId.ToByteArray());
        AppendInteger(hash, createdAt.Ticks);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = ProofEncoding.GetBytes(value);
        AppendInteger(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInteger(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static bool WellFormed(string value)
    {
        try { _ = ProofEncoding.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}

public sealed class InternalChatSendAttemptRegistry(PaymentContext db, TimeProvider? clock = null)
{
    // This captures only frozen metadata. No provider, file, notification or
    // visible message is created; later acceptance must prove all original bytes.
    public async Task<InternalChatSendCapture> CaptureAsync(InternalChatSendSnapshot request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Chat capture requires a clean, independent unit of work.");
        if (!InternalChatSendProof.IsValid(request)) return new("invalid");
        // Caller-owned arrays cannot change the frozen request across awaits.
        var snapshot = request with { MentionUserIds = request.MentionUserIds.ToArray(), Files = request.Files.ToArray() };
        var sha = InternalChatSendProof.Hash(snapshot);
        if (!await CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden");
        var existing = await FindAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
        if (existing is not null) return Replay(existing, sha);
        var originals = db.ChangeTracker.Entries()
            .Select(entry => (entry.Entity, Current: entry.CurrentValues.Clone(), Original: entry.OriginalValues.Clone())).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        InternalChatSendAttempt? attempt = null;
        try
        {
            // One short thread-row lock serializes capture/publication without
            // keeping a transaction open during file transfer or cloud copying.
            var locked = await db.InternalChatThreads.Where(item => item.Id == snapshot.ThreadId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct);
            if (locked != 1) return new("notFound");
            if (!await CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct)) return new("forbidden");
            existing = await FindAsync(snapshot.ThreadId, snapshot.ActorId, snapshot.ClientRequestId, ct);
            if (existing is not null) return Replay(existing, sha);
            // A legacy send is never adopted as a trusted managed attempt.
            if (await db.InternalChatMessages.AsNoTracking().AnyAsync(item => item.ThreadId == snapshot.ThreadId
                    && item.AuthorUserId == snapshot.ActorId && item.ClientRequestId == snapshot.ClientRequestId, ct))
                return new("legacy");
            if (!await HasPayloadScopeAsync(snapshot, ct)) return new("conflict");
            var createdAt = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            // PostgreSQL timestamps preserve microseconds, not .NET's 100 ns.
            // Freeze the portable precision before hashing the captured time.
            createdAt = new DateTime(createdAt.Ticks - createdAt.Ticks % 10, DateTimeKind.Utc);
            attempt = new InternalChatSendAttempt
            {
                ThreadId = snapshot.ThreadId,
                ActorId = snapshot.ActorId,
                ClientRequestId = snapshot.ClientRequestId,
                Text = snapshot.Text,
                ReplyToMessageId = snapshot.ReplyToMessageId,
                FileCount = snapshot.Files.Length,
                TotalSizeBytes = snapshot.Files.Sum(file => file.SizeBytes),
                CreatedAt = createdAt,
                Files = snapshot.Files.Select((file, index) => new InternalChatSendAttemptFile
                {
                    Index = index,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256
                }).ToList(),
                Mentions = snapshot.MentionUserIds.Select(id => new InternalChatSendAttemptMention { UserId = id }).ToList()
            };
            attempt.RequestSha256 = InternalChatSendProof.HashFrozen(snapshot, attempt.Id, createdAt);
            db.InternalChatSendAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            if (!await CanParticipateAsync(snapshot.ThreadId, snapshot.ActorId, ct) || !await HasPayloadScopeAsync(snapshot, ct))
                throw new InvalidOperationException("Chat capture lost its original author's authority or payload scope.");
            await transaction.CommitAsync(ct);
            return new("recorded", attempt.Id, attempt.RequestSha256);
        }
        catch (Exception original)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
            }
            catch (Exception rollback) { original.Data["ChatCaptureRollbackFailure"] = rollback; }
            // A failed capture must not be saved by unrelated subsequent work.
            // Preserve the caller's initially clean tracked values and exception.
            foreach (var saved in originals)
            {
                var entry = db.Entry(saved.Entity);
                entry.CurrentValues.SetValues(saved.Current);
                entry.OriginalValues.SetValues(saved.Original);
                entry.State = EntityState.Unchanged;
            }
            throw;
        }
        finally
        {
            if (attempt is not null)
            {
                var owned = attempt.Files.Cast<object>().Concat(attempt.Mentions).Append(attempt)
                    .ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var entry in db.ChangeTracker.Entries().ToArray())
                    if (owned.Contains(entry.Entity)) entry.State = EntityState.Detached;
            }
        }
    }

    public async Task<InternalChatSendAttempt?> ReadOwnedAsync(int threadId, int actorId, Guid clientRequestId, CancellationToken ct)
    {
        if (threadId <= 0 || actorId <= 0 || clientRequestId == Guid.Empty
            || !await CanParticipateAsync(threadId, actorId, ct)) return null;
        var attempt = await FindAsync(threadId, actorId, clientRequestId, ct);
        return attempt is not null && TrySnapshot(attempt) is not null
            && await CanParticipateAsync(threadId, actorId, ct) ? attempt : null;
    }

    private Task<InternalChatSendAttempt?> FindAsync(int threadId, int actorId, Guid clientRequestId, CancellationToken ct)
        => db.InternalChatSendAttempts.AsNoTracking().AsSplitQuery().Include(item => item.Files).Include(item => item.Mentions)
            .SingleOrDefaultAsync(item => item.ThreadId == threadId && item.ActorId == actorId && item.ClientRequestId == clientRequestId, ct);

    private static InternalChatSendCapture Replay(InternalChatSendAttempt attempt, string sha)
        => TrySnapshot(attempt) is { } snapshot && InternalChatSendProof.Hash(snapshot) == sha
            ? new("recorded", attempt.Id, attempt.RequestSha256, true) : new("conflict");

    internal static InternalChatSendSnapshot? TrySnapshot(InternalChatSendAttempt attempt)
    {
        var files = attempt.Files.OrderBy(file => file.Index).ToArray();
        if (attempt.Id == Guid.Empty || files.Length != attempt.FileCount
            || !files.Select(file => file.Index).SequenceEqual(Enumerable.Range(0, files.Length))) return null;
        var snapshot = new InternalChatSendSnapshot(attempt.ThreadId, attempt.ActorId, attempt.ClientRequestId,
            attempt.Text, attempt.ReplyToMessageId, attempt.Mentions.Select(item => item.UserId).Order().ToArray(),
            files.Select(file => new InternalChatSendFileSnapshot(file.FileName, file.ContentType, file.SizeBytes, file.Sha256)).ToArray());
        return InternalChatSendProof.IsValid(snapshot) && files.Sum(file => file.SizeBytes) == attempt.TotalSizeBytes
            && InternalChatSendProof.HashFrozen(snapshot, attempt.Id, attempt.CreatedAt) == attempt.RequestSha256 ? snapshot : null;
    }

    internal Task<bool> CanParticipateAsync(int threadId, int actorId, CancellationToken ct)
        => AuthorizedMembers().AnyAsync(member => member.ThreadId == threadId && member.UserId == actorId
            && member.User.IsActive && member.User.IsApproved, ct);

    private IQueryable<InternalChatMember> AuthorizedMembers()
        => db.InternalChatMembers.AsNoTracking().Where(member => member.IsActive
            && (member.Thread.Kind != InternalChatThreadKinds.Task || member.Role == "guest"
                || member.Thread.TaskJournalItem!.AuthorUserId == member.UserId
                || member.Thread.TaskJournalItem.AssigneeUserId == member.UserId
                || member.Thread.TaskJournalItem.Participants.Any(participant => participant.UserId == member.UserId)));

    internal async Task<bool> HasPayloadScopeAsync(InternalChatSendSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.ReplyToMessageId is { } replyId && !await AuthorizedMembers()
                .AnyAsync(member => member.ThreadId == snapshot.ThreadId && member.UserId == snapshot.ActorId
                    && db.InternalChatMessages.Any(message => message.Id == replyId && message.ThreadId == snapshot.ThreadId
                        && message.DeletedAt == null && message.Id > (member.HistoryHiddenThroughMessageId ?? 0)), ct)) return false;
        return snapshot.MentionUserIds.Length == 0 || await AuthorizedMembers()
            .CountAsync(member => member.ThreadId == snapshot.ThreadId && snapshot.MentionUserIds.Contains(member.UserId), ct) == snapshot.MentionUserIds.Length;
    }
}

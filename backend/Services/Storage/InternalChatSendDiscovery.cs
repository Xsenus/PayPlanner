using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Storage;

public sealed record InternalChatPendingSend(Guid AttemptId, Guid ClientRequestId, DateTime CreatedAt,
    int FileCount, long TotalSizeBytes, string? LastStatus);
public sealed record InternalChatSendDiscoveryResult(string Status, IReadOnlyList<InternalChatPendingSend> Items,
    Guid? NextAfterAttemptId = null);

public sealed partial class InternalChatOriginalWriter
{
    // Metadata-only discovery. Each continuation still verifies its complete
    // frozen proof and current authority before reading or publishing bytes.
    public async Task<InternalChatSendDiscoveryResult> DiscoverAsync(int threadId, int actorId, int limit,
        Guid? afterAttemptId, CancellationToken ct)
    {
        if (limit is < 1 or > 50 || afterAttemptId == Guid.Empty) return new("invalid", []);
        var registry = new InternalChatSendAttemptRegistry(db);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden", []);
        var scope = db.InternalChatSendAttempts.AsNoTracking().Where(item => item.ThreadId == threadId && item.ActorId == actorId);
        var query = scope.Where(item => item.PublishedMessageId == null);
        if (afterAttemptId is { } cursorId)
        {
            // Scope the cursor lookup too: never confirm another actor's identity.
            var cursor = await scope.Where(item => item.Id == cursorId)
                .Select(item => new { item.CreatedAt }).SingleOrDefaultAsync(ct);
            if (cursor is null) return new("invalidCursor", []);
            var key = cursorId.ToString("D");
            query = query.Where(item => item.CreatedAt < cursor.CreatedAt || item.CreatedAt == cursor.CreatedAt
                && string.Compare(item.Id.ToString().ToLower(), key) < 0);
        }
        var rows = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(limit + 1).Select(item => new InternalChatPendingSend(item.Id, item.ClientRequestId, item.CreatedAt,
                item.FileCount, item.TotalSizeBytes, item.RecoveryLastStatus)).ToArrayAsync(ct);
        if (!await registry.CanParticipateAsync(threadId, actorId, ct)) return new("forbidden", []);
        var items = rows.Take(limit).ToArray();
        return new("pending", items, rows.Length > limit ? items[^1].AttemptId : null);
    }
}

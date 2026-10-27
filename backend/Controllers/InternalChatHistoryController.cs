using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

public sealed record InternalChatHistoryStateDto(int? HiddenThroughMessageId, long Revision, int LatestMessageId);

public sealed partial class InternalChatController
{
    [HttpGet("threads/{threadId:int}/history-visibility")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<InternalChatHistoryStateDto>> GetHistoryVisibility(int threadId, CancellationToken ct)
    {
        try { return await GetHistoryVisibilityCoreAsync(threadId, ct); }
        catch (Exception error) when (IsHistoryAdmissionConflict(error))
        { return Conflict(new { message = "Чат занят. Повторите проверку истории." }); }
    }

    private async Task<ActionResult<InternalChatHistoryStateDto>> GetHistoryVisibilityCoreAsync(int threadId, CancellationToken ct)
    {
        var actor = CurrentUserId();
        if (actor is null) return Unauthorized();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (_db.Database.IsNpgsql()) await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
        if (!await LockHistoryThreadAsync(threadId, actor.Value, ct)) return Forbid();
        var member = await ActiveAuthorizedMembers().SingleOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == actor.Value, ct);
        if (member is null) return Forbid();
        var latest = await _db.InternalChatMessages.Where(m => m.ThreadId == threadId).MaxAsync(m => (int?)m.Id, ct) ?? 0;
        var state = new InternalChatHistoryStateDto(member.HistoryHiddenThroughMessageId, member.HistoryVisibilityRevision, latest);
        await transaction.CommitAsync(ct);
        return Ok(state);
    }

    [HttpPut("threads/{threadId:int}/history-visibility")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<InternalChatHistoryStateDto>> SetHistoryVisibility(int threadId, [FromBody] JsonElement body, CancellationToken ct)
    {
        try { return await SetHistoryVisibilityCoreAsync(threadId, body, ct); }
        catch (Exception error) when (IsHistoryAdmissionConflict(error))
        { return Conflict(new { message = "Чат занят. Повторите действие с той же границей истории." }); }
    }

    private async Task<ActionResult<InternalChatHistoryStateDto>> SetHistoryVisibilityCoreAsync(int threadId, JsonElement body, CancellationToken ct)
    {
        var actor = CurrentUserId();
        if (actor is null) return Unauthorized();
        if (!TryReadHistoryVisibility(body, out var cutoff, out var expectedRevision))
            return BadRequest(new { message = "Некорректные параметры истории." });
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (_db.Database.IsNpgsql()) await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
        if (!await LockHistoryThreadAsync(threadId, actor.Value, ct)) return Forbid();
        var member = await ActiveAuthorizedMembers().SingleOrDefaultAsync(m => m.ThreadId == threadId && m.UserId == actor.Value, ct);
        if (member is null) return Forbid();
        var latest = await _db.InternalChatMessages.Where(m => m.ThreadId == threadId).MaxAsync(m => (int?)m.Id, ct) ?? 0;
        if (cutoff is > 0 && !await _db.InternalChatMessages.AnyAsync(m => m.ThreadId == threadId && m.Id == cutoff, ct))
            return BadRequest(new { message = "Граница истории не найдена в этом чате." });
        var current = new InternalChatHistoryStateDto(member.HistoryHiddenThroughMessageId, member.HistoryVisibilityRevision, latest);
        if (member.HistoryVisibilityRevision != expectedRevision)
        {
            if (expectedRevision < long.MaxValue && member.HistoryVisibilityRevision == expectedRevision + 1
                && member.HistoryHiddenThroughMessageId == cutoff)
            {
                await transaction.CommitAsync(ct);
                return Ok(current);
            }
            return Conflict(new { message = "История уже изменена. Обновите состояние и повторите действие.", state = current });
        }
        if (expectedRevision == long.MaxValue) return Conflict(new { message = "История временно недоступна для изменения." });
        var changed = await ActiveAuthorizedMembers().Where(m => m.Id == member.Id && m.UserId == actor.Value
                && m.HistoryVisibilityRevision == expectedRevision)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.HistoryHiddenThroughMessageId, cutoff)
                .SetProperty(m => m.HistoryVisibilityRevision, expectedRevision + 1), ct);
        if (changed != 1) return Conflict(new { message = "История уже изменена. Обновите состояние и повторите действие." });
        await transaction.CommitAsync(ct);
        try { await _realtime.PublishTaskJournalChangedAsync(ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        { _logger.LogWarning(error, "Chat history visibility changed but realtime refresh failed for thread {ThreadId}.", threadId); }
        return Ok(new InternalChatHistoryStateDto(cutoff, expectedRevision + 1, latest));
    }

    private async Task<bool> LockHistoryThreadAsync(int threadId, int actor, CancellationToken ct)
    {
        if (!await IsMemberAsync(threadId, actor, ct)) return false;
        return await InternalChatThreadLock.AcquireAsync(_db, threadId, ct) && await IsMemberAsync(threadId, actor, ct);
    }

    private Task<bool> IsHistoryMessageVisibleAsync(int threadId, int messageId, int actor, CancellationToken ct)
        => ActiveAuthorizedMembers().AnyAsync(member => member.ThreadId == threadId && member.UserId == actor
            && messageId > (member.HistoryHiddenThroughMessageId ?? 0), ct);

    private static bool IsHistoryAdmissionConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }
                || current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return false;
    }

    private static bool TryReadHistoryVisibility(JsonElement body, out int? cutoff, out long revision)
    {
        cutoff = null;
        revision = 0;
        if (body.ValueKind != JsonValueKind.Object) return false;
        var properties = body.EnumerateObject().ToArray();
        if (properties.Length != 2 || properties.Count(p => p.Name == "hiddenThroughMessageId") != 1
            || properties.Count(p => p.Name == "expectedRevision") != 1) return false;
        var hidden = body.GetProperty("hiddenThroughMessageId");
        if (hidden.ValueKind != JsonValueKind.Null)
        {
            if (hidden.ValueKind != JsonValueKind.Number || !hidden.TryGetInt32(out var id) || id < 0) return false;
            cutoff = id;
        }
        var version = body.GetProperty("expectedRevision");
        return version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out revision) && revision >= 0;
    }
}

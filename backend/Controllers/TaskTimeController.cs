using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

public sealed record StartTaskTimerRequest(Guid RequestId, int TaskId);
public sealed record CorrectTaskTimeRequest(int? ExpectedRevision, int? Seconds, string? Reason);
public sealed record TaskTimerDto(Guid Id, int? TaskId, DateTime StartedAtUtc, DateTime? StoppedAtUtc, int? CorrectedSeconds, int Revision);
public sealed record TaskTimerCursorDto(DateTime BeforeStartedAtUtc, Guid BeforeId);
public sealed record TaskTimerHistoryDto(IReadOnlyList<TaskTimerDto> Items, bool HasMore, TaskTimerCursorDto? NextCursor);
public sealed record TaskTimerCorrectionDto(string Id, int? PreviousSeconds, int NewSeconds, string Reason, DateTime CreatedAtUtc);
public sealed record TaskTimerCorrectionsDto(IReadOnlyList<TaskTimerCorrectionDto> Items, string? NextBeforeId);

/// <summary>Personal actual-work intervals, independent of billing and planned duration.</summary>
[ApiController]
[Authorize]
[Route("api/task-time")]
public sealed class TaskTimeController(PaymentContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet("current")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        var entry = await db.TaskTimeEntries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == actor && item.StoppedAtUtc == null, ct);
        if (entry is null) return Ok(null);
        var permitted = await new TaskFilePublicationAuthorizationService(db).CanPublishAsync(actor.Value, entry.TaskId, ct);
        return Ok(ToDto(entry, permitted));
    }

    [HttpGet("task/{taskId:int}/history")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> History(int taskId, CancellationToken ct, [FromQuery] DateTime? beforeStartedAtUtc = null, [FromQuery] Guid? beforeId = null)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        if (beforeStartedAtUtc.HasValue != beforeId.HasValue || beforeId == Guid.Empty
            || (beforeStartedAtUtc.HasValue && beforeStartedAtUtc.Value.Kind != DateTimeKind.Utc)) return BadRequest("Неверный указатель истории.");
        if (!await new TaskFilePublicationAuthorizationService(db).CanPublishAsync(actor.Value, taskId, ct)) return NotFound();
        var query = db.TaskTimeEntries.AsNoTracking().Where(item => item.UserId == actor && item.TaskId == taskId);
        if (beforeStartedAtUtc is { } before && beforeId is { } id)
            query = query.Where(item => item.StartedAtUtc < before || (item.StartedAtUtc == before && item.Id.CompareTo(id) < 0));
        var entries = await query
            .OrderByDescending(item => item.StartedAtUtc).ThenByDescending(item => item.Id)
            .Take(51).ToListAsync(ct);
        var items = entries.Take(50).Select(item => ToDto(item, true)).ToList();
        var next = entries.Count > 50 ? new TaskTimerCursorDto(items[^1].StartedAtUtc, items[^1].Id) : null;
        return Ok(new TaskTimerHistoryDto(items, next is not null, next));
    }

    [HttpGet("task/{taskId:int}/{id:guid}/corrections")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Corrections(int taskId, Guid id, CancellationToken ct, [FromQuery] long? beforeId = null)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        if (beforeId is <= 0) return BadRequest("Неверный указатель истории.");
        if (!await new TaskFilePublicationAuthorizationService(db).CanPublishAsync(actor.Value, taskId, ct)) return NotFound();
        if (!await db.TaskTimeEntries.AsNoTracking().AnyAsync(item => item.Id == id && item.TaskId == taskId && item.UserId == actor, ct)) return NotFound();
        var query = db.TaskTimeCorrections.AsNoTracking().Where(item => item.EntryId == id);
        if (beforeId is { } before) query = query.Where(item => item.Id < before);
        var entries = await query.OrderByDescending(item => item.Id).Take(51).ToListAsync(ct);
        var items = entries.Take(50).Select(item => new TaskTimerCorrectionDto(item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), item.PreviousSeconds, item.NewSeconds,
            item.Reason, DateTime.SpecifyKind(item.CreatedAtUtc, DateTimeKind.Utc))).ToList();
        return Ok(new TaskTimerCorrectionsDto(items, entries.Count > 50 ? items[^1].Id : null));
    }

    [HttpPost("task/{taskId:int}/{id:guid}/correction")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Correct(int taskId, Guid id, [FromBody] CorrectTaskTimeRequest request, CancellationToken ct)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        var reason = request.Reason?.Trim();
        if (request.Seconds is null or < 0 || request.ExpectedRevision is null or < 0 or int.MaxValue
            || string.IsNullOrEmpty(reason) || reason.Length > 500) return BadRequest("Укажите длительность и причину исправления.");
        var authorization = new TaskFilePublicationAuthorizationService(db);
        if (!await authorization.CanPublishAsync(actor.Value, taskId, ct)) return NotFound();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            if (await db.TaskJournalItems.Where(item => item.Id == taskId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct) != 1)
                return NotFound();
            if (!await authorization.CanPublishAsync(actor.Value, taskId, ct)) return NotFound();
            var entry = await db.TaskTimeEntries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.TaskId == taskId && item.UserId == actor, ct);
            if (entry is null) return NotFound();
            if (entry.StoppedAtUtc is null || entry.Revision != request.ExpectedRevision)
                return Conflict("Запись изменилась или таймер ещё работает. Обновите историю.");
            var updated = await db.TaskTimeEntries.Where(item => item.Id == id && item.UserId == actor && item.Revision == request.ExpectedRevision && item.StoppedAtUtc != null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.CorrectedSeconds, request.Seconds)
                    .SetProperty(item => item.Revision, item => item.Revision + 1), ct);
            if (updated != 1) return Conflict("Запись изменилась. Обновите историю.");
            db.TaskTimeCorrections.Add(new TaskTimeCorrection
            {
                EntryId = id,
                PreviousSeconds = entry.CorrectedSeconds,
                NewSeconds = request.Seconds.Value,
                Reason = reason,
                CreatedAtUtc = clock.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            entry.CorrectedSeconds = request.Seconds;
            entry.Revision++;
            return Ok(ToDto(entry, true));
        }
        catch (Exception error) when (IsAdmissionConflict(error))
        {
            db.ChangeTracker.Clear();
            return Conflict("Запись занята. Обновите историю и повторите действие.");
        }
    }

    [HttpPost("start")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Start([FromBody] StartTaskTimerRequest request, CancellationToken ct)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        if (request.RequestId == Guid.Empty || request.TaskId <= 0) return BadRequest("Укажите задачу и идентификатор запуска.");
        var authorization = new TaskFilePublicationAuthorizationService(db);
        if (!await authorization.CanPublishAsync(actor.Value, request.TaskId, ct)) return NotFound();

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", ct);
            // A deactivation locks the same user before stopping their active interval.
            if (await db.Users.Where(item => item.Id == actor && item.IsActive && item.IsApproved)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct) != 1)
                return Forbid();
            // Serialize the status check with changes to the same task; never change its data.
            if (await db.TaskJournalItems.Where(item => item.Id == request.TaskId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt), ct) != 1)
                return NotFound();
            if (!await authorization.CanPublishAsync(actor.Value, request.TaskId, ct)) return NotFound();
            var previous = await db.TaskTimeEntries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.RequestId, ct);
            if (previous is not null)
            {
                if (previous.UserId != actor || previous.TaskId != request.TaskId) return Conflict("Идентификатор запуска уже используется.");
                await transaction.CommitAsync(ct);
                return Ok(ToDto(previous, true));
            }
            var status = await db.TaskJournalItems.AsNoTracking().Where(item => item.Id == request.TaskId)
                .Select(item => item.Status).SingleOrDefaultAsync(ct);
            if (status is null) return NotFound();
            if (status.Equals("done", StringComparison.OrdinalIgnoreCase) || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                return Conflict("Запустить таймер для завершённой или отменённой задачи нельзя.");
            if (await db.TaskTimeEntries.AsNoTracking().AnyAsync(item => item.UserId == actor && item.StoppedAtUtc == null, ct))
                return Conflict("У вас уже запущен таймер. Остановите его перед запуском другого.");
            var userName = await db.Users.AsNoTracking().Where(item => item.Id == actor).Select(item => item.FullName).SingleAsync(ct);
            var entry = new TaskTimeEntry
            {
                Id = request.RequestId,
                TaskId = request.TaskId,
                UserId = actor.Value,
                UserNameSnapshot = userName,
                StartedAtUtc = clock.GetUtcNow().UtcDateTime
            };
            db.TaskTimeEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Ok(ToDto(entry, true));
        }
        catch (Exception error) when (IsAdmissionConflict(error))
        {
            db.ChangeTracker.Clear();
            try
            {
                var existing = await db.TaskTimeEntries.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == request.RequestId && item.UserId == actor && item.TaskId == request.TaskId, ct);
                if (existing is not null)
                    return await authorization.CanPublishAsync(actor.Value, request.TaskId, ct) ? Ok(ToDto(existing, true)) : NotFound();
            }
            catch (Exception retryError) when (IsAdmissionConflict(retryError)) { /* The client can safely retry its request ID. */ }
            return Conflict("Таймер занят или уже запущен. Обновите состояние и повторите действие.");
        }
    }

    [HttpPost("{id:guid}/stop")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var actor = await ActiveActorAsync(ct);
        if (actor is null) return Forbid();
        var existing = await db.TaskTimeEntries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == actor, ct);
        if (existing is null) return NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (now < existing.StartedAtUtc) now = existing.StartedAtUtc;
        try
        {
            await db.TaskTimeEntries.Where(item => item.Id == id && item.UserId == actor && item.StoppedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.StoppedAtUtc, now), ct);
        }
        catch (Exception error) when (IsAdmissionConflict(error))
        {
            return Conflict("Таймер занят. Повторите остановку.");
        }
        var result = await db.TaskTimeEntries.AsNoTracking().SingleAsync(item => item.Id == id && item.UserId == actor, ct);
        var permitted = await new TaskFilePublicationAuthorizationService(db).CanPublishAsync(actor.Value, result.TaskId, ct);
        return Ok(ToDto(result, permitted));
    }

    private async Task<int?> ActiveActorAsync(CancellationToken ct)
    {
        var actor = User.Identity?.IsAuthenticated == true ? User.GetUserId() : null;
        return actor is > 0 && await db.Users.AsNoTracking().AnyAsync(item => item.Id == actor && item.IsActive && item.IsApproved, ct)
            ? actor : null;
    }

    private static TaskTimerDto ToDto(TaskTimeEntry entry, bool canSeeTask)
        => new(entry.Id, canSeeTask ? entry.TaskId : null, DateTime.SpecifyKind(entry.StartedAtUtc, DateTimeKind.Utc),
            entry.StoppedAtUtc is { } stopped ? DateTime.SpecifyKind(stopped, DateTimeKind.Utc) : null, entry.CorrectedSeconds, entry.Revision);

    private static bool IsAdmissionConflict(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }
                || current is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 }
                || current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.LockNotAvailable
                    or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                }) return true;
        return false;
    }
}

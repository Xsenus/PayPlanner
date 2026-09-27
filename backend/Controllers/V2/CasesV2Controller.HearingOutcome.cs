using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    public sealed class RecordHearingOutcomeRequest
    {
        public string Outcome { get; init; } = string.Empty;
        public string? Note { get; init; }
    }

    [HttpPut("{id:int}/court-events/{courtEventId:int}/hearing-outcome")]
    public async Task<ActionResult<ClientCaseCourtEventDto>> RecordHearingOutcome(
        int id,
        int courtEventId,
        [FromBody] RecordHearingOutcomeRequest? request,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(static permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var outcome = request?.Outcome?.Trim().ToLowerInvariant();
        var outcomeLabel = outcome switch
        {
            "postponed" => "Заседание отложено",
            "considered" => "Дело рассмотрено по существу",
            "interim" => "Принято промежуточное решение",
            _ => null,
        };
        if (outcomeLabel is null)
        {
            return BadRequest(new { message = "Выберите итог заседания." });
        }

        var note = request?.Note?.Trim() ?? string.Empty;
        if (note.Length > 1000)
        {
            return BadRequest(new { message = "Пояснение к итогу не должно превышать 1000 символов." });
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return MissingCasesVisibilityResult();
        }

        var clientCase = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (clientCase is null)
        {
            return NotFound();
        }

        var courtEvent = await _db.ClientCaseCourtEvents
            .FirstOrDefaultAsync(item => item.Id == courtEventId && item.ClientCaseId == id, ct);
        if (courtEvent is null)
        {
            return NotFound();
        }
        if (!string.Equals(courtEvent.EventType, CourtSyncNormalization.EventTypes.Hearing, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Итог заседания можно записать только для события типа «Заседание»." });
        }
        if (IsUnavailableHearingOutcomeStatus(courtEvent.MovementStatus))
        {
            return Conflict(new { message = "Отменённому или архивному заседанию нельзя записать итог. Проверьте актуальное событие суда." });
        }

        var clock = await ResolveCaseStatusClockAsync(ct);
        if (!CanRecordHearingOutcome(courtEvent, clock))
        {
            return Conflict(new { message = "Итог можно записать после наступления даты заседания." });
        }
        var now = clock.NowInstantUtc;

        if (string.Equals(courtEvent.HearingOutcome, outcome, StringComparison.Ordinal)
            && string.Equals(courtEvent.HearingOutcomeNote ?? string.Empty, note, StringComparison.Ordinal))
        {
            return Ok(MapCourtEvent(courtEvent, await ResolveCaseStatusClockAsync(ct)));
        }

        var actorUserId = User.GetUserId();
        var actorName = User.GetDisplayName();
        courtEvent.HearingOutcome = outcome;
        courtEvent.HearingOutcomeNote = note;
        courtEvent.HearingOutcomeRecordedAt = now;
        courtEvent.HearingOutcomeRecordedByUserId = actorUserId;
        courtEvent.HearingOutcomeRecordedByUserName = Truncate(actorName, 256);
        courtEvent.UpdatedAt = now;
        courtEvent.UpdatedByUserId = actorUserId;
        courtEvent.UpdatedByUserName = Truncate(actorName, 256);

        var description = $"Итог заседания «{courtEvent.Title}»: {outcomeLabel}. Записал(а): {actorName}.";
        AppendCaseEvent(clientCase, description);
        AddCourtEventHistory(courtEvent, "hearing-outcome", description, actorUserId, actorName);
        _db.SetAuditReason($"Зафиксирован итог судебного заседания: {outcomeLabel}");
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, id, actorUserId, actorName, ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);

        return Ok(MapCourtEvent(courtEvent, await ResolveCaseStatusClockAsync(ct)));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CasesController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly IRealtimeNotificationPublisher _realtimePublisher;

    public CasesController(
        PaymentContext db,
        IRealtimeNotificationPublisher? realtimePublisher = null)
    {
        _db = db;
        _realtimePublisher = realtimePublisher ?? NoOpRealtimeNotificationPublisher.Instance;
    }

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<IActionResult?> RequireCasesPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Cases, isAllowed, ct)
            ? null
            : Forbid();

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? clientId, [FromQuery] int? legalEntityId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var q = _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .AsQueryable();
        if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);
        if (legalEntityId.HasValue) q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
        var items = await q.AsNoTracking()
            .OrderBy(c => c.ClientId ?? int.MaxValue)
            .ThenBy(c => c.LegalEntityId ?? int.MaxValue)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var entity = await _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        if (ValidateCourtDetails(model) is { } courtError) return BadRequest(courtError);
        if (ClientCaseWritSubmissionRules.Validate(model.ExecutiveWritReceivedAt, model.ExecutiveWritSubmittedAt, model.ExecutiveWritSubmittedTo) is { } writError) return BadRequest(writError);
        ApplyCaseDetails(model, model);
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, model.Id, User.GetUserId(), User.GetDisplayName(), ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Created($"/api/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        if (ValidateCourtDetails(model) is { } courtError) return BadRequest(courtError);
        var receivedAt = model.ClearExecutiveWritReceivedAt ? null : model.ExecutiveWritReceivedAt ?? e.ExecutiveWritReceivedAt;
        var submittedAt = model.ClearExecutiveWritSubmission ? null : model.ExecutiveWritSubmittedAt ?? e.ExecutiveWritSubmittedAt;
        var submittedTo = model.ClearExecutiveWritSubmission ? null : model.ExecutiveWritSubmittedTo ?? e.ExecutiveWritSubmittedTo;
        if (ClientCaseWritSubmissionRules.Validate(receivedAt, submittedAt, submittedTo) is { } writError) return BadRequest(writError);

        var shouldCloseLinkedTasks = !IsFinishedCaseStatus(e.Status) && IsFinishedCaseStatus(model.Status);
        var shouldCancelFinalReport = IsFinishedCaseStatus(e.Status) && !IsFinishedCaseStatus(model.Status);
        var isCourtCase = shouldCloseLinkedTasks && (ClientCaseFinalReportTaskService.HasCourtFields(model)
            || (await ClientCaseFinalReportTaskService.FilterCourtCaseIdsAsync(_db, [e], ct)).Contains(e.Id));
        var finalReportAssignee = isCourtCase
            ? await ClientCaseFinalReportTaskService.ResolveAssigneeAsync(_db, ct)
            : null;
        if (isCourtCase && finalReportAssignee is null)
        {
            return BadRequest("Перед закрытием дела выберите в настройках журнала задач ответственного за итоговые отчёты с доступом ко всем делам.");
        }
        var previousSubmittedAt = e.ExecutiveWritSubmittedAt;
        var previousSubmittedTo = e.ExecutiveWritSubmittedTo;
        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        ApplyCaseDetails(e, model);
        if (previousSubmittedAt != e.ExecutiveWritSubmittedAt
            || !string.Equals(previousSubmittedTo, e.ExecutiveWritSubmittedTo, StringComparison.Ordinal))
        {
            var message = e.ExecutiveWritSubmittedAt.HasValue
                ? $"Исполнительный лист подан {e.ExecutiveWritSubmittedAt.Value:dd.MM.yyyy} в {(e.ExecutiveWritSubmittedTo == "bank" ? "банк" : "ФССП")}. Отметил: {User.GetDisplayName()}."
                : $"Отметка о подаче исполнительного листа снята. Изменил: {User.GetDisplayName()}.";
            e.EventHistory = string.IsNullOrWhiteSpace(e.EventHistory) ? message : $"{e.EventHistory.TrimEnd()}\n{message}";
        }

        if (shouldCloseLinkedTasks)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
            if (isCourtCase)
            {
                await ClientCaseFinalReportTaskService.CreateForClosedCasesAsync(_db, [e.Id], finalReportAssignee!, User.GetUserId(), User.GetDisplayName(), ct);
            }
        }
        if (shouldCancelFinalReport)
        {
            await ClientCaseFinalReportTaskService.CancelForReopenedCasesAsync(_db, [e.Id], User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
        foreach (var p in payments) p.ClientCaseId = null;

        await _db.SaveChangesAsync(ct);

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync(ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(
        int? clientId,
        int? legalEntityId,
        DataVisibilityAccess visibility,
        CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(visibility)
                .AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }

    private static void ApplyCaseDetails(ClientCase target, ClientCase source)
    {
        target.CaseNumber = source.CaseNumber?.Trim() ?? string.Empty;
        target.CaseType = source.CaseType?.Trim() ?? string.Empty;
        target.Stage = source.Stage?.Trim() ?? string.Empty;
        target.ResponsibleUserId = source.ResponsibleUserId;
        target.StartDate = source.StartDate;
        target.DueDate = source.DueDate;
        target.FeeAmount = source.FeeAmount;
        target.Priority = string.IsNullOrWhiteSpace(source.Priority) ? "normal" : source.Priority.Trim();
        target.CourtName = source.CourtName?.Trim() ?? string.Empty;
        if (source.OurCourtRole is not null) target.OurCourtRole = source.OurCourtRole.Trim().ToLowerInvariant();
        if (source.CourtClaimAmount.HasValue) target.CourtClaimAmount = source.CourtClaimAmount;
        else if (source.ClearCourtClaimAmount) target.CourtClaimAmount = null;
        if (source.CourtAwardedAmount.HasValue) target.CourtAwardedAmount = source.CourtAwardedAmount;
        else if (source.ClearCourtAwardedAmount) target.CourtAwardedAmount = null;
        if (source.CourtStateFeeAmount.HasValue) target.CourtStateFeeAmount = source.CourtStateFeeAmount;
        else if (source.ClearCourtStateFeeAmount) target.CourtStateFeeAmount = null;
        target.CourtCaseNumber = source.CourtCaseNumber?.Trim() ?? string.Empty;
        target.JudicialUid = source.JudicialUid?.Trim() ?? string.Empty;
        target.CourtUrl = source.CourtUrl?.Trim() ?? string.Empty;
        if (target.NextHearingAt != source.NextHearingAt)
        {
            target.NextHearingConfirmedForAt = null;
            target.NextHearingConfirmedAt = null;
            target.NextHearingConfirmedByUserId = null;
            target.NextHearingConfirmedByName = string.Empty;
        }
        target.NextHearingAt = source.NextHearingAt;
        target.Claimant = source.Claimant?.Trim() ?? string.Empty;
        if (source.ClaimantInn is not null) target.ClaimantInn = source.ClaimantInn.Trim();
        if (source.ClaimantOgrn is not null) target.ClaimantOgrn = source.ClaimantOgrn.Trim();
        target.Defendant = source.Defendant?.Trim() ?? string.Empty;
        if (source.DefendantInn is not null) target.DefendantInn = source.DefendantInn.Trim();
        if (source.DefendantOgrn is not null) target.DefendantOgrn = source.DefendantOgrn.Trim();
        target.ThirdParties = source.ThirdParties?.Trim() ?? string.Empty;
        target.Representatives = source.Representatives?.Trim() ?? string.Empty;
        target.JudgeName = source.JudgeName?.Trim() ?? string.Empty;
        target.Courtroom = source.Courtroom?.Trim() ?? string.Empty;
        target.CourtSecretaryContacts = source.CourtSecretaryContacts?.Trim() ?? string.Empty;
        target.ClaimFiledAt = source.ClaimFiledAt;
        if (source.CourtDecisionEffectiveAt.HasValue) target.CourtDecisionEffectiveAt = source.CourtDecisionEffectiveAt;
        else if (source.ClearCourtDecisionEffectiveAt) target.CourtDecisionEffectiveAt = null;
        if (source.ExecutiveWritReceivedAt.HasValue) target.ExecutiveWritReceivedAt = source.ExecutiveWritReceivedAt;
        else if (source.ClearExecutiveWritReceivedAt) target.ExecutiveWritReceivedAt = null;
        if (source.ClearExecutiveWritSubmission)
        {
            target.ExecutiveWritSubmittedAt = null;
            target.ExecutiveWritSubmittedTo = null;
        }
        else
        {
            if (source.ExecutiveWritSubmittedAt.HasValue) target.ExecutiveWritSubmittedAt = source.ExecutiveWritSubmittedAt;
            if (source.ExecutiveWritSubmittedTo is not null) target.ExecutiveWritSubmittedTo = source.ExecutiveWritSubmittedTo.Trim().ToLowerInvariant();
        }
        target.AppealDeadlineAt = source.AppealDeadlineAt;
        target.MotionDeadlineAt = source.MotionDeadlineAt;
        target.ObjectionDeadlineAt = source.ObjectionDeadlineAt;
        target.DocumentsSummary = source.DocumentsSummary?.Trim() ?? string.Empty;
        target.EventHistory = source.EventHistory?.Trim() ?? string.Empty;
        var updatedStrategy = source.Strategy?.Trim() ?? string.Empty;
        if (!string.Equals(target.Strategy, updatedStrategy, StringComparison.Ordinal))
        {
            target.PositionReviewedAt = null;
            target.PositionReviewedByUserId = null;
            target.PositionReviewedByName = string.Empty;
        }
        target.Strategy = updatedStrategy;
    }

    private static string? ValidateCourtDetails(ClientCase model)
    {
        if (model.OurCourtRole is not null && model.OurCourtRole.Trim().ToLowerInvariant() is not ("" or "claimant" or "respondent" or "third-party"))
            return "Укажите допустимую роль в судебном деле.";
        if (!IsValidIdentifier(model.ClaimantInn, 10, 12) || !IsValidIdentifier(model.DefendantInn, 10, 12)
            || !IsValidIdentifier(model.ClaimantOgrn, 13, 15) || !IsValidIdentifier(model.DefendantOgrn, 13, 15))
            return "ИНН должен содержать 10 или 12 цифр, ОГРН/ОГРНИП — 13 или 15 цифр.";
        if (!IsValidAmount(model.CourtClaimAmount) || !IsValidAmount(model.CourtAwardedAmount) || !IsValidAmount(model.CourtStateFeeAmount))
            return "Судебные суммы должны быть неотрицательными и содержать не более двух знаков после запятой.";
        return null;

        static bool IsValidIdentifier(string? value, int firstLength, int secondLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var trimmed = value.Trim();
            return (trimmed.Length == firstLength || trimmed.Length == secondLength)
                && trimmed.All(static character => character is >= '0' and <= '9');
        }

        static bool IsValidAmount(decimal? value)
            => !value.HasValue || value.Value >= 0m && value.Value <= 999999999999.99m && decimal.Round(value.Value, 2) == value.Value;
    }

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;
}

using PayPlanner.Api.Services.Cases;
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
    private readonly INotificationService? _notificationService;
    private readonly IRealtimeNotificationPublisher _realtimePublisher;

    public CasesController(
        PaymentContext db,
        IRealtimeNotificationPublisher? realtimePublisher = null,
        INotificationService? notificationService = null)
    {
        _db = db;
        _notificationService = notificationService;
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
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Cases, ct);
        if (access?.Permission is not { CanView: true } permission) return Forbid();
        var visibility = access.Visibility;

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
        if (items.Count > 0) CaseResponsePrivacy.ApplyToRead(items, permission.CanEdit,
            await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static value => value.CanView, ct));
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Cases, ct);
        if (access?.Permission is not { CanView: true } permission) return Forbid();
        var visibility = access.Visibility;

        var entity = await _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null) CaseResponsePrivacy.ApplyToRead([entity], permission.CanEdit,
            await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static value => value.CanView, ct));
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

        if (CaseCreationInput.Validate(_db, model) is { } graphError) return graphError;
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        if (ValidateCourtDetails(model) is { } courtError) return BadRequest(courtError);
        if (ClientCaseWritSubmissionRules.Validate(model.ExecutiveWritReceivedAt, model.ExecutiveWritSubmittedAt, model.ExecutiveWritSubmittedTo) is { } writError) return BadRequest(writError);
        ApplyCaseDetails(model, model);
        if (!await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static permission => permission.CanView, ct)) model.FeeAmount = 0m;
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, model.Id, User.GetUserId(), User.GetDisplayName(), ct);
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return Created($"/api/cases/{model.Id}", CaseEditSnapshot.ForReply(model));
    }

    [HttpPut("{id:int}")]
    [HttpPut("{id:int}/checked")]
    public Task<IActionResult> UpdateWithSource(int id, [FromBody] CaseEditRequest model, CancellationToken ct)
        => model.ExpectedSource is null
            ? Task.FromResult<IActionResult>(BadRequest(new { message = "Для сохранения нужны исходные данные дела. Обновите карточку." }))
            : Update(id, model, ct, model.ExpectedSource);

    // In-process business entry point; HTTP commands must use UpdateWithSource.
    [NonAction]
    public async Task<IActionResult> Update(int id, ClientCase model, CancellationToken ct, ClientCase? expectedSource = null)
    {
        DateTime? previousCourtDecisionAt = null;
        var canViewFinance = false;
        var result = await CaseEditTransaction.RunAsync(_db, User, id, async (e, access) =>
        {
            canViewFinance = await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static permission => permission.CanView, ct);
            if (expectedSource is not null && !CaseEditSnapshot.TryMerge(e, model, expectedSource, canViewFinance, legacy: true))
                return Conflict(new { message = "Дело изменилось после открытия редактора. Обновите данные перед сохранением." });
            var visibility = access.Visibility;
            var validation = await CaseEditTransaction.ValidateEditedLinksAsync(_db, e, model, visibility, ct);
            if (validation is not null) return validation;
            if (ValidateCourtDetails(model) is { } courtError) return BadRequest(courtError);
            var receivedAt = model.ClearExecutiveWritReceivedAt ? null : model.ExecutiveWritReceivedAt ?? e.ExecutiveWritReceivedAt;
            var submittedAt = model.ClearExecutiveWritSubmission ? null : model.ExecutiveWritSubmittedAt ?? e.ExecutiveWritSubmittedAt;
            var submittedTo = model.ClearExecutiveWritSubmission ? null : model.ExecutiveWritSubmittedTo ?? e.ExecutiveWritSubmittedTo;
            if (ClientCaseWritSubmissionRules.ValidateEdit(e, receivedAt, submittedAt, submittedTo) is { } writError) return BadRequest(writError);

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
            if (finalReportAssignee is not null && finalReportAssignee.Id != User.GetUserId())
                await CaseEditTransaction.LockUserAccessAsync(_db, finalReportAssignee.Id, ct);
            previousCourtDecisionAt = e.CourtDecisionAt;
            var previousSubmittedAt = e.ExecutiveWritSubmittedAt;
            var previousSubmittedTo = e.ExecutiveWritSubmittedTo;
            e.Title = model.Title;
            e.Description = model.Description;
            e.Status = model.Status;
            e.ClientId = model.ClientId;
            e.LegalEntityId = model.LegalEntityId;
            ApplyCaseDetails(e, model, preserveExistingText: true, canViewFinance: canViewFinance);
            CaseConfirmationResetPersistence.Apply(_db, e);
            if (previousSubmittedAt != e.ExecutiveWritSubmittedAt
                || !string.Equals(previousSubmittedTo, e.ExecutiveWritSubmittedTo, StringComparison.Ordinal))
            {
                var message = e.ExecutiveWritSubmittedAt.HasValue
                    ? $"Исполнительный лист подан {e.ExecutiveWritSubmittedAt.Value:dd.MM.yyyy} в {(e.ExecutiveWritSubmittedTo == "bank" ? "банк" : "ФССП")}. Отметил: {User.GetDisplayName()}."
                    : $"Отметка о подаче исполнительного листа снята. Изменил: {User.GetDisplayName()}.";
                e.EventHistory = string.IsNullOrWhiteSpace(e.EventHistory) ? message : $"{e.EventHistory.TrimEnd()}\n{message}";
            }

            await CaseEditTransaction.LockLinkedTasksAsync(_db, [e.Id], ct);
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
            await _db.Entry(e).ReloadAsync(ct);
            return Ok(e);
        }, ct);
        if (result is not OkObjectResult { Value: ClientCase savedCase }) return result;
        if (_notificationService is not null && !ClientCaseStageClassifier.IsPretrial(savedCase)
            && previousCourtDecisionAt != savedCase.CourtDecisionAt)
        {
            var recipients = await CaseNotificationRecipients.ResolveAsync(_db, savedCase.Id, ct);
            await _notificationService.NotifyCaseDecisionDateChangedAsync(savedCase, previousCourtDecisionAt, savedCase.CourtDecisionAt,
                User.GetDisplayName(), User.GetUserId() ?? 0, recipients, DateTime.UtcNow, ct);
        }
        await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        var reply = CaseEditSnapshot.ForReply(savedCase);
        if (!canViewFinance) CaseResponsePrivacy.HideFinanceData([reply]);
        return Ok(reply);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await CaseEditTransaction.RunDeleteAsync(_db, User, id, async (e, _) =>
        {
            var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
            foreach (var p in payments) p.ClientCaseId = null;
            await _db.SaveChangesAsync(ct);
            _db.ClientCases.Remove(e);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }, ct);
        if (result is NoContentResult) await _realtimePublisher.PublishTaskJournalChangedAsync(ct);
        return result;
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

    private static void ApplyCaseDetails(ClientCase target, ClientCase source, bool preserveExistingText = false, bool canViewFinance = true)
    {
        string Text(string? existing, string? incoming) => preserveExistingText && string.Equals(existing, incoming, StringComparison.Ordinal)
            ? existing!
            : incoming?.Trim() ?? string.Empty;

        target.CaseNumber = Text(target.CaseNumber, source.CaseNumber);
        target.CaseType = Text(target.CaseType, source.CaseType);
        target.Stage = Text(target.Stage, source.Stage);
        target.ResponsibleUserId = source.ResponsibleUserId;
        target.StartDate = source.StartDate;
        target.DueDate = source.DueDate;
        if (canViewFinance) target.FeeAmount = source.FeeAmount;
        target.Priority = string.IsNullOrWhiteSpace(source.Priority) ? "normal" : source.Priority.Trim();
        target.CourtName = Text(target.CourtName, source.CourtName);
        if (source.OurCourtRole is not null) target.OurCourtRole = source.OurCourtRole.Trim().ToLowerInvariant();
        if (source.CourtClaimAmount.HasValue) target.CourtClaimAmount = source.CourtClaimAmount;
        else if (source.ClearCourtClaimAmount) target.CourtClaimAmount = null;
        if (source.CourtAwardedAmount.HasValue) target.CourtAwardedAmount = source.CourtAwardedAmount;
        else if (source.ClearCourtAwardedAmount) target.CourtAwardedAmount = null;
        if (source.CourtStateFeeAmount.HasValue) target.CourtStateFeeAmount = source.CourtStateFeeAmount;
        else if (source.ClearCourtStateFeeAmount) target.CourtStateFeeAmount = null;
        target.CourtCaseNumber = Text(target.CourtCaseNumber, source.CourtCaseNumber);
        target.JudicialUid = Text(target.JudicialUid, source.JudicialUid);
        target.CourtUrl = Text(target.CourtUrl, source.CourtUrl);
        if (target.NextHearingAt != source.NextHearingAt)
        {
            target.NextHearingConfirmedForAt = null;
            target.NextHearingConfirmedAt = null;
            target.NextHearingConfirmedByUserId = null;
            target.NextHearingConfirmedByName = string.Empty;
        }
        target.NextHearingAt = source.NextHearingAt;
        target.Claimant = Text(target.Claimant, source.Claimant);
        if (source.ClaimantInn is not null) target.ClaimantInn = Text(target.ClaimantInn, source.ClaimantInn);
        if (source.ClaimantOgrn is not null) target.ClaimantOgrn = Text(target.ClaimantOgrn, source.ClaimantOgrn);
        target.Defendant = Text(target.Defendant, source.Defendant);
        if (source.DefendantInn is not null) target.DefendantInn = Text(target.DefendantInn, source.DefendantInn);
        if (source.DefendantOgrn is not null) target.DefendantOgrn = Text(target.DefendantOgrn, source.DefendantOgrn);
        target.ThirdParties = Text(target.ThirdParties, source.ThirdParties);
        target.Representatives = Text(target.Representatives, source.Representatives);
        target.JudgeName = Text(target.JudgeName, source.JudgeName);
        target.Courtroom = Text(target.Courtroom, source.Courtroom);
        target.CourtSecretaryContacts = Text(target.CourtSecretaryContacts, source.CourtSecretaryContacts);
        target.ClaimFiledAt = source.ClaimFiledAt;
        if (CaseEditRequest.IncludesLegacyDate(source, nameof(ClientCase.CourtRegisteredAt)))
        {
            if (source.CourtRegisteredAt.HasValue) target.CourtRegisteredAt = source.CourtRegisteredAt;
            else if (source.ClearCourtRegisteredAt) target.CourtRegisteredAt = null;
        }
        if (CaseEditRequest.IncludesLegacyDate(source, nameof(ClientCase.CourtDecisionAt))) target.CourtDecisionAt = source.CourtDecisionAt;
        if (CaseEditRequest.IncludesLegacyDate(source, nameof(ClientCase.CassationDeadlineAt))) target.CassationDeadlineAt = source.CassationDeadlineAt;
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
            if (source.ExecutiveWritSubmittedTo is not null && (!preserveExistingText || source.ExecutiveWritSubmittedTo != target.ExecutiveWritSubmittedTo))
                target.ExecutiveWritSubmittedTo = source.ExecutiveWritSubmittedTo.Trim().ToLowerInvariant();
        }
        target.AppealDeadlineAt = source.AppealDeadlineAt;
        target.MotionDeadlineAt = source.MotionDeadlineAt;
        target.ObjectionDeadlineAt = source.ObjectionDeadlineAt;
        target.DocumentsSummary = Text(target.DocumentsSummary, source.DocumentsSummary);
        target.EventHistory = Text(target.EventHistory, source.EventHistory);
        var updatedStrategy = Text(target.Strategy, source.Strategy);
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

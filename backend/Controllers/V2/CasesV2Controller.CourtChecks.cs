using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Cases;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Controllers.V2;

public partial class CasesV2Controller
{
    private async Task<bool> HasCurrentCourtCheckActorAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (User.Identity?.IsAuthenticated != true || userId is not > 0
            || !AuthSessionClaims.TryGetTokenVersion(User, out var tokenVersion)) return false;
        return await _db.Users.AsNoTracking().AnyAsync(user => user.Id == userId.Value
            && user.IsActive && user.IsApproved && user.AuthTokenVersion == tokenVersion, ct);
    }

    private Task<IActionResult> WithCurrentCourtCheckAsync(int id, CourtCheckSource expectedSource,
        Func<ClientCase, Task<IActionResult>> write, CancellationToken ct, Action? beforeCommit = null)
        => CaseEditTransaction.RunAsync(_db, User, id, async (clientCase, _) =>
        {
            if (!await HasCurrentCourtCheckActorAsync(ct)) return Unauthorized();
            if (CourtCheckSource.Capture(clientCase) != expectedSource)
                return Conflict(new { message = "Судебная привязка или результат проверки изменились. Обновите дело перед повторной проверкой." });
            return await write(clientCase);
        }, ct, beforeCommit);

    private sealed record CourtSyncCaseState(
        string JudgeName, string Courtroom, string Claimant, string Defendant, string ThirdParties, string Representatives,
        DateTime? NextHearingAt, DateTime? CourtDecisionAt, DateTime? AppealDeadlineAt, DateTime? CassationDeadlineAt,
        DateTime? NextHearingConfirmedForAt, DateTime? NextHearingConfirmedAt, int? NextHearingConfirmedByUserId,
        string NextHearingConfirmedByName)
    {
        public static CourtSyncCaseState Capture(ClientCase clientCase) => new(
            clientCase.JudgeName, clientCase.Courtroom, clientCase.Claimant, clientCase.Defendant, clientCase.ThirdParties, clientCase.Representatives,
            clientCase.NextHearingAt, clientCase.CourtDecisionAt, clientCase.AppealDeadlineAt, clientCase.CassationDeadlineAt,
            clientCase.NextHearingConfirmedForAt, clientCase.NextHearingConfirmedAt, clientCase.NextHearingConfirmedByUserId,
            clientCase.NextHearingConfirmedByName);
    }

    private sealed record CourtSyncPrepared(ClientCase Case, Func<Task<CourtSyncApplyResult>> CompleteAsync, CourtPublicationPlan? Changes);

    private static bool HasCaseCourtSourceBinding(ClientCase clientCase)
        => !string.IsNullOrWhiteSpace(clientCase.CourtCaseNumber)
            || !string.IsNullOrWhiteSpace(clientCase.JudicialUid)
            || !string.IsNullOrWhiteSpace(clientCase.CourtUrl);

    private IQueryable<ClientCaseCourtInstance> StoredCourtSourceBindings(int caseId)
        => _db.ClientCaseCourtInstances.AsNoTracking().Where(instance => instance.ClientCaseId == caseId
            && (!string.IsNullOrWhiteSpace(instance.CourtCaseNumber)
                || !string.IsNullOrWhiteSpace(instance.JudicialUid)
                || !string.IsNullOrWhiteSpace(instance.CourtUrl)));

    private async Task<ActionResult<ClientCaseCourtSyncRunDto>> RecordCourtCheckAsync(
        int id, CourtCheckSource expectedSource, DateTime checkedAt, string actorName,
        bool manualOnly, string manualCommentSuffix, CancellationToken ct,
        string? failureReason = null, string? providerName = null)
    {
        ClientCase? recordedCase = null;
        string? recordedFailureMessage = null;
        var result = await WithCurrentCourtCheckAsync(id, expectedSource, async clientCase =>
        {
            var hasCaseBinding = HasCaseCourtSourceBinding(clientCase);
            List<string> instanceSourceUrls = [];
            if (!hasCaseBinding && failureReason is null)
            {
                instanceSourceUrls = await StoredCourtSourceBindings(id)
                    .Select(instance => instance.CourtUrl)
                    .Take(2)
                    .ToListAsync(ct);
            }
            var hasCourtBinding = hasCaseBinding || instanceSourceUrls.Count > 0;
            string status;
            string message;
            if (failureReason is not null)
            {
                status = "external-sync-error";
                message = $"Проверка судебного источника не выполнена: {failureReason} Проверьте ссылку на карточку дела суда и повторите попытку.";
                MarkCourtSyncFailure(clientCase, checkedAt, failureReason);
                recordedFailureMessage = message;
            }
            else if (!hasCourtBinding && !manualOnly)
            {
                status = "binding-required";
                message = "Ручная проверка судебного источника не выполнена: в деле не указан номер дела в суде или ссылка на первоисточник.";
            }
            else if (!hasCaseBinding && instanceSourceUrls.Count > 0 && !manualOnly)
            {
                status = "manual-check-recorded";
                var sourceUrl = instanceSourceUrls.Count == 1 ? instanceSourceUrls[0] : null;
                var isUnavailableKadArbitr = ResolveCourtSyncJournalProvider(sourceUrl).Key == "kadArbitr"
                    && ResolveCourtSyncProvider(sourceUrl) is not ICourtSyncCaseReader;
                message = isUnavailableKadArbitr
                    ? "Попытка проверки судебного источника зафиксирована: в судебной инстанции сохранена ссылка на КАД Арбитр. Автоматическая загрузка из КАД Арбитр пока недоступна. Откройте ссылку в инстанции, чтобы внести сведения вручную."
                    : "Попытка проверки судебного источника зафиксирована: судебные реквизиты сохранены в инстанции. Откройте её, чтобы проверить источник вручную.";
            }
            else
            {
                status = "manual-check-recorded";
                message = manualOnly
                    ? $"Ручная проверка судебного источника зафиксирована без автоматического обновления. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}"
                    : string.IsNullOrWhiteSpace(clientCase.CourtUrl)
                        ? $"Ручная проверка судебного источника зафиксирована: для автоматического обновления добавьте ссылку на карточку дела суда. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}"
                        : $"Ручная проверка судебного источника зафиксирована: выбранный источник пока не поддерживает автоматическое чтение карточки. Суд: {FormatCaseTextValue(clientCase.CourtName)}; № в суде: {FormatCaseTextValue(clientCase.CourtCaseNumber)}.{manualCommentSuffix}";
            }
            AppendCaseEvent(clientCase, $"{message} Инициатор: {actorName}.");
            await _db.SaveChangesAsync(ct);
            recordedCase = clientCase;
            return Ok(new ClientCaseCourtSyncRunDto
            {
                ClientCaseId = clientCase.Id,
                Status = status,
                HasCourtBinding = hasCourtBinding,
                Message = message,
                CheckedAt = checkedAt,
                CheckedByUserId = User.GetUserId(),
                CheckedByUserName = actorName,
                CourtName = clientCase.CourtName,
                CourtCaseNumber = clientCase.CourtCaseNumber,
                JudicialUid = clientCase.JudicialUid,
                CourtUrl = clientCase.CourtUrl,
            });
        }, ct);
        // Notifications may dispatch immediately and must observe the committed record.
        if (result is OkObjectResult && failureReason is not null)
            await NotifyCourtSyncFailedForCaseAsync(recordedCase!, providerName!, recordedFailureMessage!,
                User.GetUserId(), actorName, ct);
        return (ActionResult)result;
    }

    private sealed record CourtCheckSource(
        string CourtName, string CourtCaseNumber, string JudicialUid, string CourtUrl,
        DateTime? LastAttemptAt, DateTime? LastSuccessAt, DateTime? FailureStartedAt, string LastError)
    {
        public static CourtCheckSource Capture(ClientCase clientCase) => new(
            clientCase.CourtName, clientCase.CourtCaseNumber, clientCase.JudicialUid, clientCase.CourtUrl,
            clientCase.CourtSyncLastAttemptAt, clientCase.CourtSyncLastSuccessAt,
            clientCase.CourtSyncFailureStartedAt, clientCase.CourtSyncLastError);
    }
}

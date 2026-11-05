using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed partial class NotificationService
{
    private async Task<IReadOnlyList<NotificationDelivery>> RetryLegacyCourtDeliveryAsync(NotificationDelivery original, CancellationToken ct)
    {
        if (original.Status != NotificationDeliveryStatus.Failed || original.RecipientUserId is not int userId
            || original.Channel is not (NotificationChannels.Email or NotificationChannels.WebPush)) return [];
        var id = await CaseCourtCurrentStateRecovery.AdoptAsync(_context, original.EventKey, userId,
            "delivery:" + original.Id.ToString(CultureInfo.InvariantCulture), _timeProvider.GetUtcNow().UtcDateTime, ct);
        if (!id.HasValue) return [];
        var job = await _context.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == id, ct);
        var before = await _context.NotificationDeliveries.AsNoTracking().Where(row => row.EventKey == job.EventKey)
            .Select(row => (int?)row.Id).MaxAsync(ct) ?? 0;
        await new CaseCourtNotificationDispatchProcessor(_context, this, _realtimePublisher, _timeProvider).RetryAsync(job.Id, ct);
        return await _context.NotificationDeliveries.AsNoTracking().Where(row => row.EventKey == job.EventKey && row.Id > before)
            .OrderBy(row => row.Id).ToArrayAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> NotifyCurrentCourtStateAsync(ClientCase clientCase, CaseCourtNotificationPayload payload,
        DateTime acceptedAt, CaseCourtDeliveryClaim claim, CancellationToken ct)
    {
        await RequireCourtDeliveryClaimAsync(claim, ct);
        if (claim.Kind != CaseCourtCurrentStateRecovery.Kind || clientCase.Id != claim.CaseId || payload.CurrentState is not { } state
            || CaseCourtCurrentStateRecovery.Fingerprint(payload) != claim.SourceFingerprint)
            throw new InvalidOperationException("Current court state does not match its accepted snapshot.");
        var userId = state.RecipientUserId;
        var type = CaseCourtCurrentStateRecovery.Type(payload);
        var zones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(_context, [userId], ct);
        if (!zones.TryGetValue(userId, out var zone) || zone.Id != state.TimeZoneId) return [];
        string Date(DateTime value, bool allDay) => allDay ? value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : TimeZoneInfo.ConvertTimeFromUtc(CalendarTimeZoneHelper.NormalizeUtcInstant(value), zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"Актуальные сведения CRM на {Date(acceptedAt, false)} ({zone.Id}).",
            $"Дело: {BuildClientCaseTitle(clientCase)}.",
            $"Этап: {state.Stage}.",
            "Статус дела: " + (state.Status switch { ClientCaseStatus.Open => "открыто", ClientCaseStatus.OnHold => "приостановлено",
                ClientCaseStatus.Closed => "закрыто", ClientCaseStatus.Lost => "завершено с отрицательным результатом", _ => "не указан" }) + "."
        };
        if (state.SourceMissing) lines.Add("Прежняя судебная запись больше недоступна в этом деле.");
        else if (payload.CourtEvent is { } courtEvent)
        {
            lines.Add($"Судебное событие: {courtEvent.Title}.");
            lines.Add($"Дата: {Date(courtEvent.ScheduledAt, courtEvent.AllDay)}.");
            if (!string.IsNullOrWhiteSpace(courtEvent.MovementStatus)) lines.Add($"Состояние: {FormatCourtMovementStatus(courtEvent.MovementStatus)}.");
            if (!string.IsNullOrWhiteSpace(courtEvent.ResultSummary)) lines.Add($"Результат: {courtEvent.ResultSummary}.");
        }
        else if (payload.Deadline is { } deadline)
        {
            lines.Add($"Процессуальный срок: {deadline.Title}.");
            lines.Add($"Дата: {Date(deadline.DueAt, deadline.AllDay)}.");
            lines.Add($"Состояние: {FormatCaseProceduralDeadlineStatus(deadline.Status)}.");
        }
        else lines.Add((state.SourceKind == "hearing-date" ? "Ближайшее заседание: " : "Дата решения: ")
            + (payload.DateChange!.CurrentAt is { } at ? Date(at, state.SourceKind == "decision-date") : "не указана") + ".");
        var message = new NotificationMessage($"Актуальные сведения суда: {BuildClientCaseTitle(clientCase)}", string.Join(Environment.NewLine, lines));
        await CreateUserNotificationAsync(userId, type, claim.EventKey, message.Subject, message.Body, "blue", "case", clientCase.Id,
            null, null, new { clientCaseId = clientCase.Id, currentState = true, sourceKind = state.SourceKind, sourceId = claim.SourceId, acceptedAt }, ct, courtClaim: claim);
        IReadOnlyList<NotificationDelivery> email = [];
        if (type == UserNotificationTypes.CourtDecisionRecorded)
        {
            var recipient = await _context.Users.AsNoTracking().Where(row => row.Id == userId)
                .Select(row => new NotificationRecipient(row.Id, row.FullName, row.Email, null, null)).SingleOrDefaultAsync(ct);
            if (recipient is not null) email = await SendCourtEmailForEventAsync(clientCase, recipient, message, type, claim, ct);
        }
        return email.Concat(await ReadCourtPushOutcomesAsync(claim, ct)).ToArray();
    }
}

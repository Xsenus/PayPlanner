using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record CaseCourtNotificationPayload(int Version, string CaseTitle, string CaseNumber,
    string Action, int ActorUserId, string ActorName, int[] RecipientUserIds,
    ClientCaseCourtEvent? CourtEvent, ClientCaseProceduralDeadline? Deadline, CaseCourtDateChange? DateChange = null, string? ReminderTimeZoneId = null,
    CaseCourtCurrentState? CurrentState = null);

public sealed record CaseCourtDateChange(DateTime? PreviousAt, DateTime? CurrentAt, bool SuppressDelivery = false);

public sealed record CaseCourtNotificationRequest(string Kind, string Action, ClientCaseCourtEvent? CourtEvent,
    ClientCaseProceduralDeadline? Deadline, bool JournalChanged, CaseCourtDateChange? DateChange = null);

public sealed class CaseCourtNotificationQueue(PaymentContext db, TimeProvider? timeProvider = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<CaseCourtNotificationRequest> CaseDateSupersessionMarkers(ClientCase clientCase,
        DateTime? previousHearingAt, DateTime? previousDecisionAt)
    {
        var requests = new List<CaseCourtNotificationRequest>(2);
        Add("hearing-marker", previousHearingAt, clientCase.NextHearingAt);
        Add("decision-marker", previousDecisionAt, clientCase.CourtDecisionAt);
        return requests;

        void Add(string kind, DateTime? previous, DateTime? current)
        {
            if (CaseCourtNotificationVersion.CaseDateValue(kind, previous) != CaseCourtNotificationVersion.CaseDateValue(kind, current))
                requests.Add(new(kind, "updated", null, null, false, new(previous, current, true)));
        }
    }

    public Task<IReadOnlyList<long>> EnqueueCaseEditAsync(ClientCase clientCase, DateTime? previousHearingAt, DateTime? previousDecisionAt,
        int actorUserId, string actorName, CancellationToken ct)
    {
        var requests = new List<CaseCourtNotificationRequest>(3);
        var suppressed = ClientCaseStageClassifier.IsPretrial(clientCase);
        if (CaseCourtNotificationVersion.CaseDateValue("hearing-date", previousHearingAt)
            != CaseCourtNotificationVersion.CaseDateValue("hearing-date", clientCase.NextHearingAt))
            requests.Add(new("hearing-date", "updated", null, null, false,
                new(previousHearingAt, clientCase.NextHearingAt, suppressed || !clientCase.NextHearingAt.HasValue)));
        if (CaseCourtNotificationVersion.CaseDateValue("decision-date", previousDecisionAt)
            != CaseCourtNotificationVersion.CaseDateValue("decision-date", clientCase.CourtDecisionAt))
            requests.Add(new("decision-date", "updated", null, null, false, new(previousDecisionAt, clientCase.CourtDecisionAt, suppressed)));
        requests.Add(new("journal", "updated", null, null, true));
        return EnqueueBatchAsync(clientCase, actorUserId, actorName, requests, ct);
    }

    public async Task<IReadOnlyList<long>> EnqueueBatchAsync(ClientCase clientCase, int actorUserId, string actorName,
        IReadOnlyCollection<CaseCourtNotificationRequest> requests, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || clientCase.Id <= 0 || actorUserId < 0)
            throw new InvalidOperationException("Court notification intents require their accepted case transaction.");
        if (requests.Count == 0) return [];
        var recipients = requests.All(row => row.Kind == "journal" || CaseCourtNotificationVersion.IsCaseDateMarker(row.Kind))
            ? [] : (await CaseNotificationRecipients.ResolveAsync(db, clientCase.Id, ct)).Order().ToArray();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var rows = new List<CaseCourtNotificationOutbox>(requests.Count);
        var seen = new HashSet<(string Kind, int SourceId)>();
        foreach (var request in requests)
        {
            var courtEvent = request.CourtEvent is null ? null : (ClientCaseCourtEvent)db.Entry(request.CourtEvent).CurrentValues.ToObject();
            var deadline = request.Deadline is null ? null : (ClientCaseProceduralDeadline)db.Entry(request.Deadline).CurrentValues.ToObject();
            var marker = CaseCourtNotificationVersion.IsCaseDateMarker(request.Kind);
            var caseDate = CaseCourtNotificationVersion.IsCaseDateKind(request.Kind) || marker;
            var sourceId = caseDate ? clientCase.Id : courtEvent?.Id ?? deadline?.Id ?? 0;
            var validAction = request.Kind switch
            {
                "event" => request.Action is "created" or "updated" or "rescheduled",
                "deadline" => request.Action is "created" or "updated" or "closed",
                "journal" => request.Action == "updated",
                "hearing-date" or "decision-date" or "hearing-marker" or "decision-marker" => request.Action == "updated",
                _ => false
            };
            if ((request.Kind == "event" && (courtEvent is null || deadline is not null))
                || (request.Kind == "deadline" && (deadline is null || courtEvent is not null))
                || (request.Kind == "journal" && (courtEvent is not null || deadline is not null || !request.JournalChanged))
                || (caseDate && (courtEvent is not null || deadline is not null || request.DateChange is null))
                || (!caseDate && request.DateChange is not null)
                || (caseDate && request.DateChange is not null
                    && (CaseCourtNotificationVersion.CaseDateValue(request.Kind, request.DateChange.CurrentAt)
                        != CaseCourtNotificationVersion.CaseDateValue(request.Kind, request.Kind is "hearing-date" or "hearing-marker" ? clientCase.NextHearingAt : clientCase.CourtDecisionAt)
                        || request.DateChange.SuppressDelivery != (marker || ClientCaseStageClassifier.IsPretrial(clientCase)
                            || (request.Kind == "hearing-date" && !clientCase.NextHearingAt.HasValue))
                        || (marker && CaseCourtNotificationVersion.CaseDateValue(request.Kind, request.DateChange.PreviousAt)
                            == CaseCourtNotificationVersion.CaseDateValue(request.Kind, request.DateChange.CurrentAt))))
                || !validAction
                || (request.Kind != "journal" && sourceId <= 0)
                || (courtEvent is not null && courtEvent.ClientCaseId != clientCase.Id)
                || (deadline is not null && deadline.ClientCaseId != clientCase.Id))
                throw new InvalidOperationException("Court notification source does not belong to its accepted case.");
            if (!seen.Add((request.Kind, sourceId))) continue;
            var payload = new CaseCourtNotificationPayload(1, clientCase.Title, clientCase.CaseNumber, request.Action,
                actorUserId, actorName, marker ? [] : recipients, courtEvent, deadline, request.DateChange);
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > 65536)
                throw new InvalidOperationException("Court notification snapshot exceeds its bounded payload size.");
            var eventId = Guid.NewGuid();
            rows.Add(new CaseCourtNotificationOutbox
            {
                ClientCaseId = clientCase.Id,
                Kind = request.Kind,
                SourceId = sourceId,
                EventKey = $"case-court:{eventId:N}",
                PayloadJson = json,
                JournalChanged = request.JournalChanged,
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }
        db.CaseCourtNotificationOutboxes.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Select(row => row.Id).ToArray();
    }
}

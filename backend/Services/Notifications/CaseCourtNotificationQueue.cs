using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record CaseCourtNotificationPayload(int Version, string CaseTitle, string CaseNumber,
    string Action, int ActorUserId, string ActorName, int[] RecipientUserIds,
    ClientCaseCourtEvent? CourtEvent, ClientCaseProceduralDeadline? Deadline);

public sealed record CaseCourtNotificationRequest(string Kind, string Action, ClientCaseCourtEvent? CourtEvent,
    ClientCaseProceduralDeadline? Deadline, bool JournalChanged);

public sealed class CaseCourtNotificationQueue(PaymentContext db, TimeProvider? timeProvider = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<long>> EnqueueBatchAsync(ClientCase clientCase, int actorUserId, string actorName,
        IReadOnlyCollection<CaseCourtNotificationRequest> requests, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || clientCase.Id <= 0 || actorUserId < 0)
            throw new InvalidOperationException("Court notification intents require their accepted case transaction.");
        if (requests.Count == 0) return [];
        var recipients = requests.All(row => row.Kind == "journal")
            ? [] : (await CaseNotificationRecipients.ResolveAsync(db, clientCase.Id, ct)).Order().ToArray();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var rows = new List<CaseCourtNotificationOutbox>(requests.Count);
        var seen = new HashSet<(string Kind, int SourceId)>();
        foreach (var request in requests)
        {
            var courtEvent = request.CourtEvent is null ? null : (ClientCaseCourtEvent)db.Entry(request.CourtEvent).CurrentValues.ToObject();
            var deadline = request.Deadline is null ? null : (ClientCaseProceduralDeadline)db.Entry(request.Deadline).CurrentValues.ToObject();
            var sourceId = courtEvent?.Id ?? deadline?.Id ?? 0;
            var validAction = request.Kind switch
            {
                "event" => request.Action is "created" or "updated" or "rescheduled",
                "deadline" => request.Action is "created" or "updated" or "closed",
                "journal" => request.Action == "updated",
                _ => false
            };
            if ((request.Kind == "event" && (courtEvent is null || deadline is not null))
                || (request.Kind == "deadline" && (deadline is null || courtEvent is not null))
                || (request.Kind == "journal" && (courtEvent is not null || deadline is not null || !request.JournalChanged))
                || !validAction
                || (request.Kind != "journal" && sourceId <= 0)
                || (courtEvent is not null && courtEvent.ClientCaseId != clientCase.Id)
                || (deadline is not null && deadline.ClientCaseId != clientCase.Id))
                throw new InvalidOperationException("Court notification source does not belong to its accepted case.");
            if (!seen.Add((request.Kind, sourceId))) continue;
            var payload = new CaseCourtNotificationPayload(1, clientCase.Title, clientCase.CaseNumber, request.Action,
                actorUserId, actorName, recipients, courtEvent, deadline);
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

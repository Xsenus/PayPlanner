using System.Security.Cryptography;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Notifications;

internal static class CaseCourtNotificationVersion
{
    public static bool IsCaseDateKind(string kind) => kind is "hearing-date" or "decision-date";

    public static bool IsCaseDateMarker(string kind) => kind is "hearing-marker" or "decision-marker";

    public static string? CaseDateMarkerKind(string kind) => kind switch
    {
        "hearing-date" => "hearing-marker",
        "decision-date" => "decision-marker",
        _ => null
    };

    public static long? CaseDateValue(string kind, DateTime? value) => value.HasValue
        ? kind is "decision-date" or "decision-marker" ? value.Value.Date.Ticks : CalendarTimeZoneHelper.NormalizeUtcInstant(value.Value).Ticks / 10 : null;

    public static string Capture(string kind, CaseCourtNotificationPayload payload) => kind switch
    {
        "current-state" => CaseCourtCurrentStateRecovery.Fingerprint(payload),
        "event" => Capture(payload.CourtEvent!),
        "deadline" => Capture(payload.Deadline!),
        "reminder" => Hash(new { Deadline = Capture(payload.Deadline!), payload.CaseTitle, payload.CaseNumber }),
        "hearing-date" or "decision-date" => Capture(kind, payload.DateChange!),
        _ => throw new InvalidOperationException("Unsupported court notification source.")
    };

    private static string Capture(string kind, CaseCourtDateChange change) => Hash(new
    {
        CurrentTicks = CaseDateValue(kind, change.CurrentAt),
        change.SuppressDelivery
    });

    public static bool IsValidEventKey(string? key)
        => key is not null && key.Length == 43 && key.StartsWith("case-court:", StringComparison.Ordinal)
            && Guid.TryParseExact(key[11..], "N", out _);

    public static string Capture(ClientCaseCourtEvent source) => Hash(new
    {
        source.Title,
        source.Description,
        source.EventType,
        source.MovementStatus,
        ScheduledTicks = CalendarTimeZoneHelper.NormalizeUtcInstant(source.ScheduledAt).Ticks / 10,
        source.AllDay,
        PreviousScheduledTicks = source.PreviousScheduledAt.HasValue
            ? CalendarTimeZoneHelper.NormalizeUtcInstant(source.PreviousScheduledAt.Value).Ticks / 10 : (long?)null,
        source.PreviousAllDay,
        source.CourtInstance,
        source.CourtId,
        source.CourtName,
        source.CourtCaseNumber,
        source.Courtroom,
        source.JudgeName,
        source.SecretaryName,
        source.ResultSummary,
        source.SourceType,
        source.SourceUrl,
        source.SourceConfidence,
        source.ExternalKey,
        source.ProceduralDeadlineId,
        source.HearingOutcome,
        source.HearingOutcomeNote
    });

    public static string Capture(ClientCaseProceduralDeadline source) => Hash(new
    {
        source.Title,
        source.Description,
        source.DeadlineType,
        source.Status,
        DueTicks = CalendarTimeZoneHelper.NormalizeUtcInstant(source.DueAt).Ticks / 10,
        source.AllDay,
        source.WarningDaysBefore,
        source.Notes,
        source.CloseReason,
        source.SourceType,
        source.SourceId,
        source.SourceUrl,
        source.SourceDocumentKey,
        source.SourceConfidence,
        source.TaskJournalItemId
    });

    public static async Task<bool> IsCurrentAsync(PaymentContext db, long outboxId, int caseId, string kind, int sourceId,
        string fingerprint, CancellationToken ct)
    {
        if (kind == "current-state")
        {
            var accepted = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleOrDefaultAsync(row => row.Id == outboxId
                && row.ClientCaseId == caseId && row.Kind == kind && row.SourceId == sourceId, ct);
            return accepted is not null && CaseCourtNotificationDispatchProcessor.TryReadPayload(accepted, out var payload)
                && Capture(kind, payload) == fingerprint
                && await CaseCourtCurrentStateRecovery.IsCurrentAsync(db, caseId, sourceId, payload, ct);
        }
        var markerKind = CaseDateMarkerKind(kind);
        var versionKind = kind == "reminder" ? "deadline" : kind;
        if (await db.CaseCourtNotificationOutboxes.AsNoTracking().AnyAsync(row => row.ClientCaseId == caseId
            && (row.Kind == versionKind || (markerKind != null && row.Kind == markerKind)) && row.SourceId == sourceId && row.Id > outboxId, ct)) return false;
        if (kind == "reminder")
        {
            var source = await db.ClientCaseProceduralDeadlines.AsNoTracking()
                .Where(row => row.Id == sourceId && row.ClientCaseId == caseId)
                .Select(row => new { Deadline = row, row.ClientCase.Title, row.ClientCase.CaseNumber }).SingleOrDefaultAsync(ct);
            return source is not null && source.Deadline.Status == "active"
                && Hash(new { Deadline = Capture(source.Deadline), CaseTitle = source.Title, source.CaseNumber }) == fingerprint;
        }
        if (IsCaseDateKind(kind))
        {
            if (sourceId != caseId) return false;
            var source = await db.ClientCases.AsNoTracking().Where(row => row.Id == caseId)
                .Select(row => new { row.NextHearingAt, row.CourtDecisionAt, row.Stage, row.CaseType }).SingleOrDefaultAsync(ct);
            if (source is null || ClientCaseStageClassifier.IsPretrial(source.Stage, source.CaseType)) return false;
            var date = kind == "hearing-date" ? source.NextHearingAt : source.CourtDecisionAt;
            return Capture(kind, new CaseCourtDateChange(null, date, kind == "hearing-date" && !date.HasValue)) == fingerprint;
        }
        if (kind == "event")
        {
            var source = await db.ClientCaseCourtEvents.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sourceId && row.ClientCaseId == caseId, ct);
            return source is not null && Capture(source) == fingerprint;
        }
        if (kind == "deadline")
        {
            var source = await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sourceId && row.ClientCaseId == caseId, ct);
            return source is not null && Capture(source) == fingerprint;
        }
        return false;
    }

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

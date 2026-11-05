using System.Security.Cryptography;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Services.Notifications;

internal static class CaseCourtNotificationVersion
{
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
        if (await db.CaseCourtNotificationOutboxes.AsNoTracking().AnyAsync(row => row.ClientCaseId == caseId
            && row.Kind == kind && row.SourceId == sourceId && row.Id > outboxId, ct)) return false;
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

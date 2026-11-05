using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal sealed record CaseCourtReminderIdentity(int CaseId, int DeadlineId, int UserId, DateTime Day);

internal static class CaseCourtReminderKeys
{
    internal static bool IsManaged(string? key) => key?.StartsWith("case:", StringComparison.OrdinalIgnoreCase) == true
        && key.Contains(":procedural-deadline:", StringComparison.OrdinalIgnoreCase)
        && key.Contains(":reminder:", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParse(string? key, out CaseCourtReminderIdentity identity)
    {
        identity = null!;
        if (key is null || key.Length > 160) return false;
        var parts = key.Split(':');
        if (parts.Length != 7 || parts[0] != "case" || parts[2] != "procedural-deadline" || parts[4] != "reminder"
            || !Id(parts[1], out var caseId) || !Id(parts[3], out var deadlineId) || !Id(parts[6], out var userId)
            || !DateTime.TryParseExact(parts[5], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return false;
        identity = new(caseId, deadlineId, userId, day);
        return true;

        static bool Id(string text, out int id) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id > 0 && id.ToString(CultureInfo.InvariantCulture) == text;
    }

    internal static async Task<bool> IsCurrentWindowAsync(PaymentContext db, string key, int userId, string? timeZoneId, DateTime now, CancellationToken ct)
    {
        if (!TryParse(key, out var identity) || identity.UserId != userId) return false;
        var source = await db.ClientCaseProceduralDeadlines.AsNoTracking()
            .Where(row => row.Id == identity.DeadlineId && row.ClientCaseId == identity.CaseId)
            .Select(row => new
            {
                row.DueAt,
                row.AllDay,
                row.WarningDaysBefore,
                row.Status,
                CaseStatus = row.ClientCase.Status,
                row.ClientCase.Stage,
                row.ClientCase.CaseType,
                row.ClientCase.ResponsibleUserId,
                ClientOwner = row.ClientCase.Client != null ? row.ClientCase.Client.ResponsibleUserId : null,
                CompanyOwner = row.ClientCase.LegalEntity != null ? row.ClientCase.LegalEntity.ResponsibleUserId : null
            }).SingleOrDefaultAsync(ct);
        if (source is null || source.Status != "active" || source.WarningDaysBefore < 0 || source.CaseStatus is not (ClientCaseStatus.Open or ClientCaseStatus.OnHold)
            || ClientCaseStageClassifier.IsPretrial(source.Stage, source.CaseType)
            || !new[] { source.ResponsibleUserId, source.ClientOwner, source.CompanyOwner }.Contains(userId)) return false;
        var zones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(db, [userId], ct);
        return zones.TryGetValue(userId, out var zone) && zone.Id == timeZoneId && CaseDeadlineReminderCalendar.LocalDay(now, zone) == identity.Day
            && CaseDeadlineReminderCalendar.IsInWarningWindow(new()
            {
                DueAt = source.DueAt,
                AllDay = source.AllDay,
                WarningDaysBefore = source.WarningDaysBefore
            }, now, zone, 90);
    }
}

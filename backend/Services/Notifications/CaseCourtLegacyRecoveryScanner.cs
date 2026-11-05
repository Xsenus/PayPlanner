using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal static class CaseCourtLegacyRecoveryScanner
{
    private const string CursorKey = "notifications.courtLegacyRecovery.cursor";

    internal static async Task<IReadOnlyList<NotificationDelivery>> ReadAsync(PaymentContext db, DateTime cutoff, int batchSize, int maxAttempts, CancellationToken ct)
    {
        var candidates = db.NotificationDeliveries.AsNoTracking().Where(row => row.Status == NotificationDeliveryStatus.Failed
            && row.CreatedAt <= cutoff && (row.Channel == NotificationChannels.Email || row.Channel == NotificationChannels.WebPush)
            && row.EventKey.ToLower().StartsWith("case:")
            && (row.EventKey.ToLower().Contains(":court-event:") || row.EventKey.ToLower().Contains(":procedural-deadline:")
                || row.EventKey.ToLower().Contains(":hearing-date-changed:") || row.EventKey.ToLower().Contains(":decision-date-changed:"))
            && !db.CaseCourtNotificationOutboxes.Any(job => job.EventKey == "case-court-origin:delivery:" + row.Id || job.EventKey == row.EventKey));
        if (!await candidates.AnyAsync(ct)) return [];
        await using var lease = await CaseCourtNotificationLease.BeginAsync(db, ct);
        if (db.Database.IsNpgsql() && !await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(831035, hashtext({CursorKey})) AS \"Value\"").SingleAsync(ct)) return [];
        var setting = await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == CursorKey, ct);
        int.TryParse(setting?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor);
        var scan = await candidates.Where(row => row.Id > cursor).OrderBy(row => row.Id).Take(Math.Clamp(batchSize, 1, 100) * 5).ToArrayAsync(ct);
        if (scan.Length == 0 && cursor > 0) scan = await candidates.OrderBy(row => row.Id).Take(Math.Clamp(batchSize, 1, 100) * 5).ToArrayAsync(ct);
        var selected = scan.Where(row => row.EventKey.Split(":retry:", StringSplitOptions.None).Length - 1 < maxAttempts)
            .Take(batchSize).ToArray();
        if (scan.Length == 0 && setting is null) return [];
        var value = (selected.Length > 0 ? selected[^1].Id : scan.Length == 0 ? 0 : scan[^1].Id).ToString(CultureInfo.InvariantCulture);
        if (setting is null) db.SystemSettings.Add(new() { Key = CursorKey, Value = value });
        else if (setting.Value != value) { setting.Value = value; setting.UpdatedAt = DateTime.UtcNow; }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        return selected;
    }
}

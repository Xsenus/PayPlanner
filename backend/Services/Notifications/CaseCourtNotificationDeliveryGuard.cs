using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal static class CaseCourtNotificationDeliveryGuard
{
    internal static bool IsManaged(string? eventKey) => eventKey?.StartsWith("case-court:", StringComparison.OrdinalIgnoreCase) == true
        || CaseCourtReminderKeys.IsManaged(eventKey);

    internal static string? RootKey(string? eventKey)
    {
        if (CaseCourtReminderKeys.IsManaged(eventKey))
        {
            var suffix = eventKey!.IndexOf(":retry:", StringComparison.Ordinal);
            var key = suffix < 0 ? eventKey : eventKey[..suffix];
            return CaseCourtReminderKeys.TryParse(key, out _) ? key : null;
        }
        if (eventKey is null || eventKey.Length < 43) return null;
        var root = eventKey[..43];
        return CaseCourtNotificationVersion.IsValidEventKey(root)
            && (eventKey.Length == 43 || eventKey[43..].StartsWith(":retry:", StringComparison.Ordinal)) ? root : null;
    }

    internal static async Task<bool> CanSendAsync(PaymentContext db, string eventKey, int? userId, string channel, CancellationToken ct)
    {
        if (!IsManaged(eventKey)) return true;
        var root = RootKey(eventKey);
        if (root is null || userId is not > 0) return false;
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleOrDefaultAsync(row => row.EventKey == root, ct);
        if (intent is null || intent.PayloadJson.Length > 65536 || intent.Kind is not ("event" or "deadline" or "reminder" or "hearing-date" or "decision-date" or "current-state")
            || !CaseCourtNotificationDispatchProcessor.TryReadPayload(intent, out var payload)) return false;
        if (payload.DateChange?.SuppressDelivery == true) return false;
        if (payload.CurrentState is not null && payload.CurrentState.RecipientUserId != userId.Value) return false;
        var fingerprint = CaseCourtNotificationVersion.Capture(intent.Kind, payload);
        if (!await CaseCourtNotificationVersion.IsCurrentAsync(db, intent.Id, intent.ClientCaseId, intent.Kind, intent.SourceId, fingerprint, ct)) return false;
        if (intent.Kind == "reminder" && !await CaseCourtReminderKeys.IsCurrentWindowAsync(db, root, userId.Value, payload.ReminderTimeZoneId, DateTime.UtcNow, ct)) return false;
        var type = intent.Kind == "current-state" ? CaseCourtCurrentStateRecovery.Type(payload)
            : intent.Kind is "deadline" or "reminder" ? UserNotificationTypes.CaseProceduralDeadline
            : intent.Kind == "hearing-date" ? UserNotificationTypes.CourtHearing
            : intent.Kind == "decision-date" ? UserNotificationTypes.CourtDecisionRecorded
            : payload.Action == "rescheduled" ? UserNotificationTypes.CourtHearing
            : NotificationService.ResolveCourtEventNotificationType(payload.CourtEvent!);
        if (channel == NotificationChannels.Email && type != UserNotificationTypes.CourtDecisionRecorded) return false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString(CultureInfo.InvariantCulture))], "CourtDelivery"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
        if (access?.Permission?.CanView != true || !await db.ClientCases.AsNoTracking()
            .Where(row => row.Id == intent.ClientCaseId).ApplyClientCaseVisibility(access.Visibility).AnyAsync(ct)) return false;
        var preferences = await MaxNotificationRecipientPolicy.LoadAsync(db, [userId.Value], ct);
        if (!preferences.TryGetValue(userId.Value, out var preference)
            || !UserNotificationPreferenceSettings.IsChannelEnabled(preference, type, channel)) return false;
        if (channel is NotificationChannels.WebPush or NotificationChannels.Max)
            return await db.UserNotifications.AsNoTracking().AnyAsync(row => row.EventKey == root && row.RecipientUserId == userId
                && row.Type == type && row.ObjectType == "case" && row.ObjectId == intent.ClientCaseId
                && !row.IsRead && !row.IsArchived && row.DeletedAt == null, ct);
        return true;
    }
}

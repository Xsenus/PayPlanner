using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record CaseCourtCurrentState(string SourceKind, int RecipientUserId, string TimeZoneId,
    long SourceVersion, bool SourceMissing, string Stage, string CaseType, ClientCaseStatus Status);

internal static class CaseCourtCurrentStateRecovery
{
    internal const string Kind = "current-state";

    internal static string Fingerprint(CaseCourtNotificationPayload payload) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload.CaseTitle,
            payload.CaseNumber,
            payload.CurrentState,
            Event = payload.CourtEvent is null ? null : CaseCourtNotificationVersion.Capture(payload.CourtEvent),
            Deadline = payload.Deadline is null ? null : CaseCourtNotificationVersion.Capture(payload.Deadline),
            Date = payload.DateChange is null ? null : CaseCourtNotificationVersion.CaseDateValue(payload.CurrentState!.SourceKind, payload.DateChange.CurrentAt)
        })));

    internal static string Type(CaseCourtNotificationPayload payload) => payload.CurrentState!.SourceKind switch
    {
        "deadline" => UserNotificationTypes.CaseProceduralDeadline,
        "hearing-date" => UserNotificationTypes.CourtHearing,
        "decision-date" => UserNotificationTypes.CourtDecisionRecorded,
        _ => payload.CourtEvent is null ? UserNotificationTypes.CourtHearing : NotificationService.ResolveCourtEventNotificationType(payload.CourtEvent)
    };

    internal static bool IsValid(CaseCourtNotificationOutbox item, CaseCourtNotificationPayload payload)
    {
        var state = payload.CurrentState;
        if (state is null || state.SourceKind is not ("event" or "deadline" or "hearing-date" or "decision-date")
            || state.RecipientUserId <= 0 || state.SourceVersion < 0 || string.IsNullOrWhiteSpace(state.TimeZoneId) || state.TimeZoneId.Length > 128
            || state.Stage is null || state.CaseType is null || !Enum.IsDefined(state.Status)
            || item.JournalChanged || item.SourceId <= 0 || payload.Action != "current" || payload.ActorUserId != 0 || payload.ActorName != "CRM"
            || payload.RecipientUserIds.Length != 1 || payload.RecipientUserIds[0] != state.RecipientUserId
            || payload.ReminderTimeZoneId is not null) return false;
        if (state.SourceKind == "event") return payload.Deadline is null && payload.DateChange is null
            && (state.SourceMissing ? payload.CourtEvent is null : payload.CourtEvent is { } e && e.Id == item.SourceId && e.ClientCaseId == item.ClientCaseId);
        if (state.SourceKind == "deadline") return payload.CourtEvent is null && payload.DateChange is null
            && (state.SourceMissing ? payload.Deadline is null : payload.Deadline is { } d && d.Id == item.SourceId && d.ClientCaseId == item.ClientCaseId);
        return item.SourceId == item.ClientCaseId && !state.SourceMissing && payload.CourtEvent is null && payload.Deadline is null
            && payload.DateChange is { PreviousAt: null, SuppressDelivery: false };
    }

    private static async Task<CaseCourtNotificationPayload?> ReadAsync(PaymentContext db, CaseCourtLegacyKey source, CancellationToken ct)
    {
        var clientCase = await db.ClientCases.AsNoTracking().Where(row => row.Id == source.CaseId)
            .Select(row => new { row.Title, row.CaseNumber, row.Stage, row.CaseType, row.Status, row.NextHearingAt, row.CourtDecisionAt }).SingleOrDefaultAsync(ct);
        if (clientCase is null) return null;
        var courtEvent = source.Kind == "event" ? await db.ClientCaseCourtEvents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == source.SourceId && row.ClientCaseId == source.CaseId, ct) : null;
        var deadline = source.Kind == "deadline" ? await db.ClientCaseProceduralDeadlines.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == source.SourceId && row.ClientCaseId == source.CaseId, ct) : null;
        var marker = CaseCourtNotificationVersion.CaseDateMarkerKind(source.Kind);
        var version = await db.CaseCourtNotificationOutboxes.AsNoTracking()
            .Where(row => row.ClientCaseId == source.CaseId && row.SourceId == source.SourceId
                && (row.Kind == source.Kind || (marker != null && row.Kind == marker)))
            .Select(row => (long?)row.Id).MaxAsync(ct) ?? 0;
        var zones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(db, [source.UserId], ct);
        if (!zones.TryGetValue(source.UserId, out var zone)) return null;
        var state = new CaseCourtCurrentState(source.Kind, source.UserId, zone.Id, version,
            source.Kind == "event" && courtEvent is null || source.Kind == "deadline" && deadline is null,
            clientCase.Stage, clientCase.CaseType, clientCase.Status);
        var date = CaseCourtNotificationVersion.IsCaseDateKind(source.Kind)
            ? new CaseCourtDateChange(null, source.Kind == "hearing-date" ? clientCase.NextHearingAt : clientCase.CourtDecisionAt) : null;
        return new(1, clientCase.Title, clientCase.CaseNumber, "current", 0, "CRM", [source.UserId], courtEvent, deadline, date, CurrentState: state);
    }

    internal static async Task<bool> IsCurrentAsync(PaymentContext db, int caseId, int sourceId, CaseCourtNotificationPayload accepted, CancellationToken ct)
    {
        var state = accepted.CurrentState!;
        var current = await ReadAsync(db, new(caseId, state.SourceKind, sourceId, state.RecipientUserId), ct);
        return current is not null && Fingerprint(current) == Fingerprint(accepted);
    }

    internal static async Task<long?> AdoptAsync(PaymentContext db, string eventKey, int userId, string origin, DateTime now, CancellationToken ct, int? maxMessageId = null)
    {
        if (!CaseCourtLegacyKey.TryRead(eventKey, userId, out var source)) return null;
        var originKey = "case-court-origin:" + origin;
        if (originKey.Length > 160) throw new InvalidOperationException("Legacy court notification origin is too long.");
        await using var lease = await CaseCourtNotificationLease.BeginAsync(db, ct);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {source.CaseId} FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR SHARE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) FOR SHARE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"RoleId\" FROM \"RolePermissions\" WHERE \"RoleId\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {userId}) ORDER BY \"Section\" FOR SHARE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserSettings\" WHERE \"UserId\" = {userId} ORDER BY \"Key\" FOR SHARE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" IN (SELECT \"ClientId\" FROM \"ClientCases\" WHERE \"Id\" = {source.CaseId}) FOR SHARE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" IN (SELECT \"LegalEntityId\" FROM \"ClientCases\" WHERE \"Id\" = {source.CaseId}) FOR SHARE NOWAIT", ct);
            var settings = new[] { RoleNotificationPreferenceSettings.SettingKey };
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Key\" FROM \"SystemSettings\" WHERE \"Key\" = ANY ({settings}) ORDER BY \"Key\" FOR SHARE NOWAIT", ct);
            if (source.Kind == "event") await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEvents\" WHERE \"Id\" = {source.SourceId} AND \"ClientCaseId\" = {source.CaseId} FOR SHARE NOWAIT", ct);
            if (source.Kind == "deadline") await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"Id\" = {source.SourceId} AND \"ClientCaseId\" = {source.CaseId} FOR SHARE NOWAIT", ct);
        }
        var handled = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleOrDefaultAsync(row => row.EventKey == originKey, ct);
        if (handled is not null)
        {
            var targetKey = JsonSerializer.Deserialize<string>(handled.PayloadJson);
            return await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.EventKey == targetKey && row.Kind == Kind)
                .Select(row => (long?)row.Id).SingleOrDefaultAsync(ct);
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))], "CourtLegacyRecovery"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
        if (access?.Permission?.CanView != true || !await db.ClientCases.AsNoTracking().Where(row => row.Id == source.CaseId)
            .ApplyClientCaseVisibility(access.Visibility).AnyAsync(ct)) return null;
        var root = eventKey.Split(":retry:", StringSplitOptions.None)[0];
        if (await db.UserNotifications.AsNoTracking().AnyAsync(row => row.EventKey == root && row.RecipientUserId == userId
            && (row.IsRead || row.IsArchived || row.DeletedAt != null), ct)) return null;
        // A pending transport may already have reached its provider. Do not turn it into a new automatic attempt.
        if (await db.NotificationDeliveries.AsNoTracking().AnyAsync(row => row.RecipientUserId == userId
            && (row.EventKey == root || row.EventKey.StartsWith(root + ":retry:")) && row.Status == NotificationDeliveryStatus.Pending, ct)) return null;
        var maxKey = MaxOutboxService.NormalizeEventKey(root);
        if (await db.MaxOutboxMessages.AsNoTracking().AnyAsync(row => row.RecipientUserId == userId && row.EventKey == maxKey
            && row.Id != maxMessageId && (row.Status == MaxOutboxStatuses.Processing
                || row.LastErrorCode == "delivery-outcome-unknown" || row.LastErrorCode == "provider-timeout"), ct)) return null;
        var payload = await ReadAsync(db, source, ct);
        if (payload is null) return null;
        var preferences = await MaxNotificationRecipientPolicy.LoadAsync(db, [userId], ct);
        if (!preferences.TryGetValue(userId, out var preference)
            || !UserNotificationPreferenceSettings.IsChannelEnabled(preference, Type(payload), NotificationChannels.InApp)) return null;
        var fingerprint = Fingerprint(payload);
        var target = "case-court:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{source.CaseId}:{source.Kind}:{source.SourceId}:{userId}:{fingerprint}")))[..32].ToLowerInvariant();
        var json = JsonSerializer.Serialize(payload, CaseCourtNotificationQueue.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidOperationException("Current court notification exceeds its bounded payload size.");
        var job = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleOrDefaultAsync(row => row.EventKey == target, ct);
        if (job is null)
        {
            job = new()
            {
                ClientCaseId = source.CaseId,
                SourceId = source.SourceId,
                Kind = Kind,
                EventKey = target,
                PayloadJson = json,
                CreatedAt = now,
                NextAttemptAt = now
            };
            db.CaseCourtNotificationOutboxes.Add(job);
        }
        db.CaseCourtNotificationOutboxes.Add(new()
        {
            ClientCaseId = source.CaseId,
            SourceId = source.SourceId,
            Kind = "legacy-origin",
            EventKey = originKey,
            PayloadJson = JsonSerializer.Serialize(target),
            CreatedAt = now,
            NextAttemptAt = now,
            NotificationCompletedAt = now,
            CompletedAt = now
        });
        await db.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        return job.Id;
    }
}

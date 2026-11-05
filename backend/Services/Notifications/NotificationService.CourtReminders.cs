using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed partial class NotificationService
{
    private async Task<int> PersistCourtRemindersAsync(IReadOnlyCollection<CaseProceduralDeadlineReminderBatchItem> reminders, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await PersistCourtReminderBatchAsync(reminders, ct); }
            catch (Exception error) when (attempt < 2 && FinancialConcurrencyGuard.IsSerializationFailure(error))
            {
                _logger.LogWarning("Court reminder admission restarts after a rolled-back serialization conflict; attempt {Attempt}.", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), ct);
            }
        }
    }

    private async Task<int> PersistCourtReminderBatchAsync(IReadOnlyCollection<CaseProceduralDeadlineReminderBatchItem> reminders, CancellationToken ct)
    {
        var inputs = reminders.Where(row => row.ClientCase.Id > 0 && row.Deadline.Id > 0)
            .GroupBy(row => (DeadlineId: row.Deadline.Id, CaseId: row.ClientCase.Id)).ToDictionary(group => group.Key, group => group.ToArray());
        if (inputs.Count == 0) return 0;
        await using var lease = await CaseCourtNotificationLease.BeginAsync(_context, ct);
        var caseIds = inputs.Keys.Select(row => row.CaseId).Distinct().Order().ToArray();
        var deadlineIds = inputs.Keys.Select(row => row.DeadlineId).Distinct().Order().ToArray();
        if (_context.Database.IsNpgsql())
        {
            caseIds = await _context.ClientCases.FromSqlInterpolated($"SELECT * FROM \"ClientCases\" WHERE \"Id\" = ANY ({caseIds}) ORDER BY \"Id\" FOR UPDATE SKIP LOCKED")
                .AsNoTracking().Select(row => row.Id).ToArrayAsync(ct);
            if (caseIds.Length == 0) return 0;
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"Id\" = ANY ({deadlineIds}) AND \"ClientCaseId\" = ANY ({caseIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" IN (SELECT \"ClientId\" FROM \"ClientCases\" WHERE \"Id\" = ANY ({caseIds})) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" IN (SELECT \"LegalEntityId\" FROM \"ClientCases\" WHERE \"Id\" = ANY ({caseIds})) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
        var sources = await _context.ClientCaseProceduralDeadlines.AsNoTracking()
            .Where(row => deadlineIds.Contains(row.Id) && caseIds.Contains(row.ClientCaseId) && row.Status == "active" && row.WarningDaysBefore >= 0
                && (row.ClientCase.Status == ClientCaseStatus.Open || row.ClientCase.Status == ClientCaseStatus.OnHold))
            .Select(row => new
            {
                Deadline = row,
                Case = new ClientCase
                {
                    Id = row.ClientCase.Id,
                    Title = row.ClientCase.Title,
                    CaseNumber = row.ClientCase.CaseNumber,
                    Stage = row.ClientCase.Stage,
                    CaseType = row.ClientCase.CaseType,
                    Status = row.ClientCase.Status,
                    ResponsibleUserId = row.ClientCase.ResponsibleUserId,
                    IsHiddenFromExecutor = row.ClientCase.IsHiddenFromExecutor,
                    ClientId = row.ClientCase.ClientId,
                    LegalEntityId = row.ClientCase.LegalEntityId
                },
                ClientOwner = row.ClientCase.Client != null ? row.ClientCase.Client.ResponsibleUserId : null,
                CompanyOwner = row.ClientCase.LegalEntity != null ? row.ClientCase.LegalEntity.ResponsibleUserId : null
            }).ToArrayAsync(ct);
        var candidates = sources.Where(row => inputs.ContainsKey((row.Deadline.Id, row.Case.Id)) && !ClientCaseStageClassifier.IsPretrial(row.Case))
            .SelectMany(row =>
            {
                var owners = new[] { row.Case.ResponsibleUserId, row.ClientOwner, row.CompanyOwner }.OfType<int>().Where(id => id > 0).Distinct().ToArray();
                var requested = inputs[(row.Deadline.Id, row.Case.Id)];
                var recipients = requested.Any(item => item.ResolveCurrentRecipients) ? owners
                    : requested.SelectMany(item => item.RecipientUserIds).Intersect(owners).ToArray();
                return recipients.Select(userId => new { row.Deadline, row.Case, Owners = owners, UserId = userId });
            }).ToArray();
        var userIds = candidates.Select(row => row.UserId).Distinct().Order().ToArray();
        if (userIds.Length == 0) return 0;
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({userIds}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = ANY ({userIds})) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"RoleId\" FROM \"RolePermissions\" WHERE \"RoleId\" IN (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = ANY ({userIds})) ORDER BY \"RoleId\", \"Section\" FOR SHARE NOWAIT", ct);
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"UserId\" FROM \"UserSettings\" WHERE \"UserId\" = ANY ({userIds}) ORDER BY \"UserId\", \"Key\" FOR SHARE NOWAIT", ct);
            var settingKeys = new[] { NotificationTemplatesKey, RoleNotificationPreferenceSettings.SettingKey };
            await _context.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Key\" FROM \"SystemSettings\" WHERE \"Key\" = ANY ({settingKeys}) ORDER BY \"Key\" FOR SHARE NOWAIT", ct);
        }
        var approved = await _context.Users.AsNoTracking().Where(user => userIds.Contains(user.Id) && user.IsActive && user.IsApproved
            && user.Role != null && (user.Role.Name == "admin" || user.Role.Permissions.Any(permission => permission.Section == RolePermissionCatalog.Cases && permission.CanView)))
            .Select(user => user.Id).ToHashSetAsync(ct);
        var visibility = await _context.ResolveDataVisibilitiesForUsersAsync(approved, RolePermissionCatalog.Cases, ct);
        var zones = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(_context, approved, ct);
        var channels = await ResolveUserNotificationChannelsAsync(approved, UserNotificationTypes.CaseProceduralDeadline,
            [NotificationChannels.InApp, NotificationChannels.Toast, NotificationChannels.WebPush], ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var accepted = candidates.Where(row => approved.Contains(row.UserId) && visibility.TryGetValue(row.UserId, out var access)
            && access is not null && (access.SeesAll || !row.Case.IsHiddenFromExecutor && row.Owners.Any(access.CanSeeUser))
            && channels[row.UserId].Contains(NotificationChannels.InApp) && zones.TryGetValue(row.UserId, out var zone)
            && CaseDeadlineReminderCalendar.IsInWarningWindow(row.Deadline, now, zone, 90))
            .Select(row => new
            {
                row.Deadline,
                row.Case,
                row.UserId,
                Zone = zones[row.UserId],
                Key = BuildCaseProceduralDeadlineReminderEventKey(row.Case.Id, row.Deadline.Id, row.UserId,
                    CaseDeadlineReminderCalendar.LocalDay(now, zones[row.UserId]).ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            }).GroupBy(row => row.Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (accepted.Length == 0) return 0;
        var keys = accepted.Select(row => row.Key).ToArray();
        var existing = await _context.UserNotifications.AsNoTracking().Where(row => keys.Contains(row.EventKey)).Select(row => row.EventKey)
            .Union(_context.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => keys.Contains(row.EventKey)).Select(row => row.EventKey)).ToHashSetAsync(ct);
        var definition = NotificationTemplateCatalog.Find(NotificationTemplateKeys.CaseProceduralDeadline);
        var storage = definition is null ? new NotificationTemplateStorage() : await LoadTemplateStorageAsync(ct);
        var messages = new Dictionary<(int DeadlineId, string Zone), NotificationMessage>();
        var created = 0;
        foreach (var row in accepted.Where(row => !existing.Contains(row.Key)))
        {
            var payload = new CaseCourtNotificationPayload(1, row.Case.Title, row.Case.CaseNumber, "reminder", 0, "CRM", [row.UserId], null, row.Deadline, ReminderTimeZoneId: row.Zone.Id);
            var json = JsonSerializer.Serialize(payload, CaseCourtNotificationQueue.JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidOperationException("Court reminder snapshot exceeds its bounded payload size.");
            if (!messages.TryGetValue((row.Deadline.Id, row.Zone.Id), out var message))
            {
                message = RenderTemplate(definition, storage,
                    BuildCaseProceduralDeadlineSubject(row.Case, row.Deadline, "reminder"),
                    BuildCaseProceduralDeadlineBody(row.Case, row.Deadline, "reminder", "CRM", row.Zone),
                    BuildCaseProceduralDeadlineTemplateValues(row.Case, row.Deadline, "reminder", "CRM", row.Zone), isSensitive: false, purpose: NotificationPurposes.TaskReminder);
                messages[(row.Deadline.Id, row.Zone.Id)] = message;
            }
            _context.UserNotifications.Add(new()
            {
                RecipientUserId = row.UserId,
                Type = UserNotificationTypes.CaseProceduralDeadline,
                EventKey = row.Key,
                Title = Truncate(message.Subject.Trim(), 300),
                BodyPreview = Truncate(message.Body.Trim(), 1000),
                Tone = ResolveCaseProceduralDeadlineTone("reminder", row.Deadline),
                ObjectType = "case",
                ObjectId = row.Case.Id,
                CreatedAt = now,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    clientCaseId = row.Case.Id,
                    proceduralDeadlineId = row.Deadline.Id,
                    action = "reminder",
                    actionText = ResolveCaseProceduralDeadlineActionText("reminder"),
                    actorUserId = 0,
                    actorName = "CRM",
                    row.Deadline.DeadlineType,
                    row.Deadline.DueAt,
                    row.Deadline.AllDay,
                    row.Deadline.Status,
                    row.Deadline.WarningDaysBefore,
                    row.Deadline.TaskJournalItemId,
                    row.Deadline.ClientCaseDocumentId,
                    row.Deadline.SourceType,
                    row.Deadline.SourceId
                }, JsonOptions)
            });
            _context.CaseCourtNotificationOutboxes.Add(new()
            {
                ClientCaseId = row.Case.Id,
                Kind = "reminder",
                SourceId = row.Deadline.Id,
                EventKey = row.Key,
                PayloadJson = json,
                CreatedAt = now,
                NextAttemptAt = now
            });
            created++;
        }
        if (created > 0) await _context.SaveChangesAsync(ct);
        await lease.CommitAsync(ct);
        return created;
    }
}

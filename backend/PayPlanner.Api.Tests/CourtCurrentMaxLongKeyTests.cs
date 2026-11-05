using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData("active", false)]
    [InlineData("active", true)]
    [InlineData("stale", false)]
    [InlineData("stale", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    [InlineData("missing-origin", false)]
    [InlineData("missing-origin", true)]
    [InlineData("processing", false)]
    [InlineData("processing", true)]
    [InlineData("timeout", false)]
    [InlineData("timeout", true)]
    [InlineData("legacy-denied", false)]
    [InlineData("legacy-denied", true)]
    public async Task CourtCurrentMax_LongKeyPreservesSourceAdmissionAndLegacyRecovery(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var clientCase = new ClientCase { Id = 1900000001, ClientId = seed.Case.ClientId, Title = "Дело длинного ключа", Stage = "Суд", ResponsibleUserId = seed.UserId };
        var deadline = new ClientCaseProceduralDeadline
        {
            Id = 1900000002,
            ClientCase = clientCase,
            Title = "Срок длинного ключа",
            DeadlineType = "appeal",
            DueAt = seed.Deadline.DueAt,
            AllDay = true,
            WarningDaysBefore = 7,
            Status = "active"
        };
        db.ClientCaseProceduralDeadlines.Add(deadline);
        db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        var realtime = new ReminderAdmissionRealtime();
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var api = new SecondaryMaxApi();
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), realtime, maxService: runtime,
            maxOutboxService: new MaxOutboxService(db, runtime, protection, TimeProvider.System),
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        if (mode is "legacy" or "processing" or "timeout" or "legacy-denied") await service.NotifyCaseProceduralDeadlineAsync(clientCase, deadline, "updated", "Сотрудник", 0, [seed.UserId]);
        else
        {
            Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(clientCase, deadline, [seed.UserId])]));
            var reminder = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(reminder.Id, default));
        }
        var original = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.True(original.EventKey.Length > 64);
        var queued = await db.MaxOutboxMessages.SingleAsync();
        Assert.StartsWith("sha256:", queued.EventKey);
        if (mode is "processing" or "timeout")
        {
            queued.Status = mode == "processing" ? MaxOutboxStatuses.Processing : MaxOutboxStatuses.Failed;
            queued.AttemptCount = 1;
            queued.UpdatedAt = DateTime.UtcNow.AddMinutes(-6);
            queued.NextAttemptAt = DateTime.UnixEpoch;
            queued.LastErrorCode = mode == "timeout" ? "provider-timeout" : null;
            await db.SaveChangesAsync();
        }
        if (mode == "missing-origin")
        {
            queued.ProtectedText = protection.CreateProtector("PayPlanner.Notifications.Max.Outbox.v1").Protect("pp-max-notification:v1:" + UserNotificationTypes.CaseProceduralDeadline);
            await db.SaveChangesAsync();
        }
        if (mode is "stale" or "legacy") await db.ClientCaseProceduralDeadlines.Where(row => row.Id == deadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "CURRENT длинный срок"));
        if (mode == "legacy-denied") await ChangeReminderAsync(db, seed, "access");
        db.ChangeTracker.Clear(); var domain = await ReminderDomainAsync(db);
        var processor = new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessBatchAsync(default));
        Assert.Equal(mode == "active" ? 1 : 0, api.Calls);
        var oldMax = await db.MaxOutboxMessages.AsNoTracking().SingleAsync(row => row.Id == queued.Id);
        Assert.Equal(mode == "active" ? MaxOutboxStatuses.Sent : mode is "processing" or "timeout" ? MaxOutboxStatuses.Failed : MaxOutboxStatuses.Cancelled, oldMax.Status);
        if (mode is "processing" or "timeout")
        {
            Assert.Equal("delivery-outcome-unknown", oldMax.LastErrorCode);
            Assert.Equal(oldMax.MaxAttempts, oldMax.AttemptCount);
            Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state").ToArrayAsync());
        }
        if (mode == "legacy")
        {
            var current = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "current-state");
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(current.Id, default));
            var fresh = await db.UserNotifications.AsNoTracking().SingleAsync(row => row.Id != original.Id);
            Assert.Contains("CURRENT", fresh.BodyPreview);
            Assert.Equal(1, await processor.ProcessBatchAsync(default));
            Assert.Equal(1, api.Calls);
        }
        if (mode == "missing-origin") Assert.Equal("court-source-unresolved", oldMax.LastErrorCode);
        if (mode == "legacy-denied")
        {
            Assert.Equal("court-current-state-not-permitted", oldMax.LastErrorCode);
            Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "current-state").ToArrayAsync());
        }
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }
}

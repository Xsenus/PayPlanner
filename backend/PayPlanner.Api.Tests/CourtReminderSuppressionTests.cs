using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task CourtReminder_SuppressedInboxStopsQueuedMaxAndFailedPush(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        db.UserPushSubscriptions.Add(new() { UserId = seed.UserId, Endpoint = "https://push.synthetic.local/reminder-suppressed", P256dh = "fake", Auth = "fake" });
        db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        var publisher = new ReminderAdmissionRealtime();
        var push = new SecondaryPush(db) { Fail = true };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var api = new SecondaryMaxApi();
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, push, maxService: runtime,
            maxOutboxService: new MaxOutboxService(db, runtime, protection, TimeProvider.System),
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var job = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        Assert.False(await processor.ProcessAsync(job.Id, default));
        Assert.Single(publisher.Delivered);
        Assert.Equal(1, push.Calls);
        var inbox = await db.UserNotifications.SingleAsync();
        if (mode == "read") { inbox.IsRead = true; inbox.ReadAt = DateTime.UtcNow; }
        if (mode == "archive") { inbox.IsArchived = true; inbox.ArchivedAt = DateTime.UtcNow; }
        if (mode == "delete") inbox.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var old = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync());
        var domain = await ReminderDomainAsync(db);
        push.Fail = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == job.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await processor.ProcessAsync(job.Id, default));
        Assert.Equal(1, await new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance).ProcessBatchAsync(default));
        Assert.Single(publisher.Delivered);
        Assert.Equal(1, push.Calls);
        Assert.Equal(0, api.Calls);
        Assert.Equal(MaxOutboxStatuses.Cancelled, (await db.MaxOutboxMessages.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(old, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync()));
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtReminder_UnknownPushOutcomeIsNotRetried(bool stale, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        db.UserPushSubscriptions.Add(new() { UserId = seed.UserId, Endpoint = "https://push.synthetic.local/reminder-unknown", P256dh = "fake", Auth = "fake" });
        await db.SaveChangesAsync();
        var publisher = new ReminderAdmissionRealtime();
        var push = new SecondaryPush(db) { AfterSend = () => Task.FromException(new OperationCanceledException("Synthetic unknown push result")) };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, push);
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var job = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        Assert.False(await processor.ProcessAsync(job.Id, default));
        Assert.Equal(1, push.Calls);
        var ledger = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Pending, ledger.Status);
        if (stale) await ChangeReminderAsync(db, seed, "source");
        var domain = await ReminderDomainAsync(db);
        push.AfterSend = null;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == job.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.Equal(stale, await processor.ProcessAsync(job.Id, default));
        Assert.Equal(1, push.Calls);
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryDeliveryAsync(ledger.Id));
        Assert.Equal("NotificationDeliveryPending", denied.Message);
        Assert.Equal(1, push.Calls);
        Assert.Equal(NotificationDeliveryStatus.Pending, (await db.NotificationDeliveries.AsNoTracking().SingleAsync()).Status);
        var saved = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == job.Id);
        if (stale) Assert.NotNull(saved.CompletedAt); else Assert.NotNull(saved.BlockedAt);
        Assert.Single(await db.UserNotifications.ToArrayAsync());
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtReminder_MultipleRecipientsAndLocalDaysDoNotSupersedeEachOther(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var first = await db.Users.AsNoTracking().SingleAsync();
        var second = new User { Email = "second-reminder@synthetic.local", FullName = "Второй получатель", PasswordHash = "synthetic", RoleId = first.RoleId, IsActive = true, IsApproved = true };
        db.Users.Add(second); await db.SaveChangesAsync();
        await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, second.Id));
        db.UserSettings.Add(new() { UserId = seed.UserId, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "UTC" });
        db.UserSettings.Add(new() { UserId = second.Id, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "Pacific/Kiritimati" });
        db.UserSettings.Add(new()
        {
            UserId = second.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var publisher = new ReminderAdmissionRealtime();
        var service = ReminderService(db, publisher);
        Assert.Equal(2, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [], ResolveCurrentRecipients: true)]));
        Assert.Equal(0, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [], ResolveCurrentRecipients: true)]));
        var jobs = await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind == "reminder").OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        foreach (var job in jobs) Assert.True(await processor.ProcessAsync(job.Id, default));
        Assert.Equal(new[] { seed.UserId, second.Id }.Order(), publisher.Delivered.Select(row => row.RecipientUserId).Order());
        Assert.Equal(2, await db.UserNotifications.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtReminder_AcceptedDeadlineChangeAndReturnSupersedesOldDailyNotice(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var publisher = new ReminderAdmissionRealtime();
        var service = ReminderService(db, publisher);
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var job = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        var originalDate = seed.Deadline.DueAt;
        foreach (var date in new[] { originalDate.AddDays(1), originalDate })
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var source = await db.ClientCaseProceduralDeadlines.SingleAsync(row => row.Id == seed.Deadline.Id);
            source.DueAt = date; await db.SaveChangesAsync();
            await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(seed.Case, 0, "CRM", [new("deadline", "updated", null, source, false)], default);
            await transaction.CommitAsync(); db.ChangeTracker.Clear();
        }
        var domain = await ReminderDomainAsync(db);
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(job.Id, default));
        Assert.Empty(publisher.Delivered);
        Assert.Single(await db.UserNotifications.ToArrayAsync());
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }
}

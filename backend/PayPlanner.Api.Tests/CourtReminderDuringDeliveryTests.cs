using System.Text.Json;
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
    [InlineData("push", false)]
    [InlineData("push", true)]
    [InlineData("max", false)]
    [InlineData("max", true)]
    public async Task CourtReminder_PostWaitRechecksSourceBeforeNextSubscriptionOrMessage(string channel, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var publisher = new ReminderAdmissionRealtime();
        var push = new SecondaryPush(db);
        var api = new SecondaryMaxApi();
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var inputs = new List<CaseProceduralDeadlineReminderBatchItem> { new(seed.Case, seed.Deadline, [seed.UserId]) };
        var changedId = seed.Deadline.Id;
        if (channel == "push")
        {
            foreach (var suffix in new[] { "first", "second" }) db.UserPushSubscriptions.Add(new()
            {
                UserId = seed.UserId,
                Endpoint = "https://push.synthetic.local/reminder-" + suffix,
                P256dh = "fake",
                Auth = "fake"
            });
        }
        else
        {
            var second = new ClientCaseProceduralDeadline
            {
                ClientCaseId = seed.Case.Id,
                Title = "Следующий срок",
                DeadlineType = "appeal",
                Status = "active",
                DueAt = seed.Deadline.DueAt,
                WarningDaysBefore = 7
            };
            db.ClientCaseProceduralDeadlines.Add(second);
            db.MaxRecipientLinks.Add(new()
            {
                UserId = seed.UserId,
                MaxUserId = 79001,
                Status = MaxRecipientLinkStatuses.Verified,
                LinkTokenHash = Guid.NewGuid().ToString("N"),
                VerifiedAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
            inputs.Add(new(seed.Case, second, [seed.UserId]));
            changedId = second.Id;
        }
        await db.SaveChangesAsync();
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, channel == "push" ? push : null,
            maxService: channel == "max" ? runtime : null,
            maxOutboxService: channel == "max" ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        Assert.Equal(channel == "push" ? 1 : 2, await service.NotifyCaseProceduralDeadlineRemindersAsync(inputs));
        var inbox = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        async Task Change()
        {
            if ((channel == "push" ? push.Calls : api.Calls) == 1) await db.ClientCaseProceduralDeadlines.Where(row => row.Id == changedId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Срок изменился во время отправки"));
        }
        if (channel == "push") push.AfterSend = Change; else api.AfterSend = Change;
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        var jobs = await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind == "reminder").OrderBy(row => row.SourceId).ToArrayAsync();
        foreach (var job in jobs) Assert.True(await processor.ProcessAsync(job.Id, default));
        if (channel == "max") Assert.Equal(2, await new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System,
            NullLogger<MaxOutboxProcessor>.Instance).ProcessBatchAsync(default));
        Assert.Equal(1, channel == "push" ? push.Calls : api.Calls);
        if (channel == "push")
        {
            Assert.Equal(1, await db.NotificationDeliveries.CountAsync(row => row.Status == NotificationDeliveryStatus.Sent));
            Assert.Equal(1, await db.NotificationDeliveries.CountAsync(row => row.Status == NotificationDeliveryStatus.Skipped));
        }
        else
        {
            Assert.Equal(1, await db.MaxOutboxMessages.CountAsync(row => row.Status == MaxOutboxStatuses.Sent));
            Assert.Equal(1, await db.MaxOutboxMessages.CountAsync(row => row.Status == MaxOutboxStatuses.Cancelled));
        }
        Assert.Equal(inbox, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.Contains(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync(), row => row.Id == changedId && row.Title == "Срок изменился во время отправки");
        foreach (var job in jobs) Assert.False(await processor.ProcessAsync(job.Id, default));
        Assert.Equal(1, channel == "push" ? push.Calls : api.Calls);
    }
}

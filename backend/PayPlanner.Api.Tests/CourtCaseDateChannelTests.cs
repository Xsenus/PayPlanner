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
    public static IEnumerable<object[]> CaseDateChannelCases() =>
        from channel in new[] { "email", "push", "max" }
        from mode in new[] { "active", "stale", "ban", "access", "newer", "auto-marker" }
        from postgres in new[] { false, true }
        select new object[] { channel, mode, postgres };

    [Theory]
    [MemberData(nameof(CaseDateChannelCases))]
    public async Task CaseDateNotice_SecondaryChannelsUseStableClaimAndCurrentCaseDate(string channel, string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, channel == "email", channel == "email");
        var clientCase = await db.ClientCases.SingleAsync();
        var date = DateTime.UtcNow.AddDays(3).Date;
        var kind = channel == "email" ? "decision-date" : "hearing-date";
        clientCase.Stage = "Суд";
        if (channel == "email") clientCase.CourtDecisionAt = date; else clientCase.NextHearingAt = date;
        if (channel != "email") db.UserSettings.Add(new()
        {
            UserId = seed.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
        });
        if (channel == "push") db.UserPushSubscriptions.Add(new()
        {
            UserId = seed.UserId,
            Endpoint = "https://push.synthetic.local/case-date",
            P256dh = "fake",
            Auth = "fake"
        });
        if (channel == "max") db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new CaseCourtNotificationQueue(db).EnqueueCaseEditAsync(clientCase, null, null, 0, "Сотрудник", default);
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == kind);
        var email = new CourtEmail { Fail = true };
        var push = new SecondaryPush(db) { Fail = true };
        var api = new SecondaryMaxApi { Fail = true };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, email, new NotificationServiceTests.RecordingTelegramService(), realtime,
            channel == "push" ? push : null, maxService: channel == "max" ? runtime : null,
            maxOutboxService: channel == "max" ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        Assert.Equal(channel == "max", await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(intent.Id, default));
        var max = new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance);
        if (channel == "max") Assert.Equal(1, await max.ProcessBatchAsync(default));
        int Attempts() => channel == "email" ? email.Attempts.Count : channel == "push" ? push.Calls : api.Calls;
        Assert.Equal(1, Attempts());
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        var original = channel == "max" ? null : await db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(row => row.EventKey == intent.EventKey && row.Channel == (channel == "email" ? NotificationChannels.Email : NotificationChannels.WebPush));
        if (mode == "stale")
        {
            var current = await db.ClientCases.SingleAsync();
            if (channel == "email") current.CourtDecisionAt = date.AddDays(1); else current.NextHearingAt = date.AddDays(1);
            await db.SaveChangesAsync();
        }
        if (mode == "newer")
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new CaseCourtNotificationQueue(db).EnqueueCaseEditAsync(await db.ClientCases.SingleAsync(),
                channel == "email" ? null : date.AddDays(-1), channel == "email" ? date.AddDays(-1) : null, 0, "Сотрудник", default);
            await transaction.CommitAsync();
        }
        if (mode == "auto-marker")
        {
            var current = await db.ClientCases.SingleAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var markers = CaseCourtNotificationQueue.CaseDateSupersessionMarkers(current,
                channel == "email" ? null : date.AddDays(2), channel == "email" ? date.AddDays(2) : null);
            Assert.Single(markers);
            await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(current, 0, "Сотрудник", markers, default);
            await transaction.CommitAsync();
        }
        if (mode == "access") await db.Roles.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "restricted-synthetic"));
        if (mode == "ban")
        {
            var preference = await db.UserSettings.SingleAsync(row => row.UserId == seed.UserId && row.Key == UserNotificationPreferenceSettings.SettingKey);
            preference.Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                new Dictionary<string, IReadOnlyList<string>> { [inbox.Type] = [channel == "email" ? NotificationChannels.Email : channel == "push" ? NotificationChannels.WebPush : NotificationChannels.Max] }));
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        var domain = await DomainAsync(db);
        email.Fail = push.Fail = api.Fail = false;
        if (channel == "max")
        {
            await db.MaxOutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            Assert.Equal(1, await max.ProcessBatchAsync(default));
            Assert.Equal(mode == "active" ? MaxOutboxStatuses.Sent : MaxOutboxStatuses.Cancelled,
                (await db.MaxOutboxMessages.AsNoTracking().SingleAsync()).Status);
        }
        else
        {
            if (channel == "push") Assert.Null(await push.RetryDeliveryAsync(original!.Id));
            var retries = await service.RetryDeliveryAsync(original!.Id);
            Assert.All(retries, row => Assert.Equal(intent.EventKey, row.EventKey));
            Assert.DoesNotContain(await db.NotificationDeliveries.ToArrayAsync(), row => row.EventKey.Contains(":retry:", StringComparison.Ordinal));
        }
        Assert.Equal(mode == "active" ? 2 : 1, Attempts());
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(domain, await DomainAsync(db));
    }
}

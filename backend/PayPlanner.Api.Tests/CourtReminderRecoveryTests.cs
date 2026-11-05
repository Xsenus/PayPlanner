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
    private sealed record ReminderSeed(int UserId, ClientCase Case, ClientCaseProceduralDeadline Deadline);

    private static async Task<ReminderSeed> SeedReminderAsync(PaymentContext db)
    {
        var seed = await SeedAsync(db, false, false);
        var clientCase = await db.ClientCases.SingleAsync();
        clientCase.Stage = "Суд";
        var deadline = new ClientCaseProceduralDeadline
        {
            ClientCaseId = clientCase.Id,
            DeadlineType = "appeal",
            Title = "Срок апелляции",
            DueAt = DateTime.UtcNow.Date.AddDays(2),
            AllDay = true,
            WarningDaysBefore = 7,
            Status = "active"
        };
        db.ClientCaseProceduralDeadlines.Add(deadline);
        db.UserSettings.Add(new()
        {
            UserId = seed.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
        });
        await db.SaveChangesAsync();
        return new(seed.UserId, clientCase, deadline);
    }

    private static NotificationService ReminderService(PaymentContext db, IRealtimeNotificationPublisher publisher, TimeProvider? clock = null)
        => NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, timeProvider: clock);

    private static async Task<string> ReminderDomainAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Existing = await DomainAsync(db),
        Deadlines = await db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
    });

    private static async Task ChangeReminderAsync(PaymentContext db, ReminderSeed seed, string mode, string channel = NotificationChannels.InApp)
    {
        if (mode == "deadline") await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Deadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "closed"));
        if (mode == "source") await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Deadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Новое принятое содержание срока"));
        if (mode == "date") await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Deadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.DueAt, seed.Deadline.DueAt.AddDays(30)));
        if (mode == "case") await db.ClientCases.Where(row => row.Id == seed.Case.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ClientCaseStatus.Closed));
        if (mode == "pretrial") await db.ClientCases.Where(row => row.Id == seed.Case.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Stage, "Досудебная стадия"));
        if (mode == "access")
        {
            await db.Roles.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "restricted-synthetic"));
            db.RolePermissions.Add(new() { RoleId = (await db.Users.AsNoTracking().SingleAsync()).RoleId, Section = RolePermissionCatalog.Cases, CanView = false });
            await db.SaveChangesAsync();
        }
        if (mode == "personal")
        {
            var settings = await db.UserSettings.SingleAsync(row => row.UserId == seed.UserId && row.Key == UserNotificationPreferenceSettings.SettingKey);
            settings.Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.CaseProceduralDeadline] = [channel] }));
            await db.SaveChangesAsync();
        }
        if (mode == "owner")
        {
            await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
            await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
        }
        if (mode == "timezone")
        {
            var old = await CalendarTimeZoneHelper.ResolveUserTimeZonesAsync(db, [seed.UserId]);
            db.UserSettings.Add(new()
            {
                UserId = seed.UserId,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = old[seed.UserId].Id == "UTC" ? "Asia/Novosibirsk" : "UTC"
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
    }

    public static IEnumerable<object[]> ReminderWindowCases() =>
        from mode in new[] { "active", "deadline", "date", "case", "pretrial", "access", "personal", "owner", "timezone", "day" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(ReminderWindowCases))]
    public async Task CourtReminder_LateWindowAndRecipientChangesSuppressOnlyObsoleteDelivery(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var publisher = new ReminderAdmissionRealtime();
        var service = ReminderService(db, publisher, mode == "day" ? new ReminderFixedClock(DateTimeOffset.UtcNow.AddDays(-1)) : null);
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        var old = JsonSerializer.Serialize(inbox);
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        await ChangeReminderAsync(db, seed, mode);
        var domain = await ReminderDomainAsync(db);
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, ReminderService(db, publisher), publisher).ProcessAsync(intent.Id, default));
        Assert.Equal(mode == "active" ? 1 : 0, publisher.Delivered.Count);
        Assert.Equal(old, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync()));
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == intent.Id)).CompletedAt);
    }

    public static IEnumerable<object[]> ReminderRetryCases() =>
        from channel in new[] { "realtime", "push", "max" }
        from mode in new[] { "active", "source", "personal", "timezone" }
        from postgres in new[] { false, true }
        select new object[] { channel, mode, postgres };

    [Theory]
    [MemberData(nameof(ReminderRetryCases))]
    public async Task CourtReminder_PostFailureChannelsKeepDailyIdentityAndCurrentSource(string channel, string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        if (channel == "push") db.UserPushSubscriptions.Add(new() { UserId = seed.UserId, Endpoint = "https://push.synthetic.local/reminder", P256dh = "fake", Auth = "fake" });
        if (channel == "max") db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        var publisher = new ReminderAdmissionRealtime { Fail = channel == "realtime" };
        var push = new SecondaryPush(db) { Fail = true };
        var api = new SecondaryMaxApi { Fail = true };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, channel == "push" ? push : null,
            maxService: channel == "max" ? runtime : null,
            maxOutboxService: channel == "max" ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        var old = JsonSerializer.Serialize(inbox);
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        Assert.Equal(channel == "max", await processor.ProcessAsync(intent.Id, default));
        var max = new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance);
        if (channel == "max") Assert.Equal(1, await max.ProcessBatchAsync(default));
        Assert.Equal(channel == "realtime" ? 0 : 1, channel == "max" ? api.Calls : channel == "push" ? push.Calls : publisher.Delivered.Count);
        await ChangeReminderAsync(db, seed, mode, channel == "max" ? NotificationChannels.Max : channel == "push" ? NotificationChannels.WebPush : NotificationChannels.InApp);
        var domain = await ReminderDomainAsync(db);
        publisher.Fail = push.Fail = api.Fail = false;
        if (channel == "max")
        {
            await db.MaxOutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            Assert.Equal(1, await max.ProcessBatchAsync(default));
            Assert.Equal(mode == "active" ? MaxOutboxStatuses.Sent : MaxOutboxStatuses.Cancelled, (await db.MaxOutboxMessages.AsNoTracking().SingleAsync()).Status);
        }
        else
        {
            await db.CaseCourtNotificationOutboxes.Where(row => row.Id == intent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            Assert.True(await processor.ProcessAsync(intent.Id, default));
        }
        Assert.Equal(channel == "realtime" ? mode == "active" ? 1 : 0 : mode == "active" ? 2 : 1,
            channel == "max" ? api.Calls : channel == "push" ? push.Calls : publisher.Delivered.Count);
        Assert.Equal(old, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync()));
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.DoesNotContain(await db.NotificationDeliveries.ToArrayAsync(), row => row.EventKey.Contains(":retry:", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> ReminderInvalidCases() =>
        from mode in new[] { "source-id", "case-id", "recipient", "bad-day", "leading-zero", "timezone", "actor", "journal", "action", "kind" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(ReminderInvalidCases))]
    public async Task CourtReminder_RejectsMalformedDailyIdentityAndSnapshots(string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var publisher = new ReminderAdmissionRealtime();
        var service = ReminderService(db, publisher);
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var job = await db.CaseCourtNotificationOutboxes.SingleAsync(row => row.Kind == "reminder");
        var parts = job.EventKey.Split(':');
        if (mode == "case-id") parts[1] = (seed.Case.Id + 1).ToString();
        if (mode == "source-id") parts[3] = (seed.Deadline.Id + 1).ToString();
        if (mode == "recipient") parts[6] = (seed.UserId + 1).ToString();
        if (mode == "bad-day") parts[5] = "20260230";
        if (mode == "leading-zero") parts[1] = "0" + parts[1];
        job.EventKey = string.Join(':', parts);
        var payload = JsonSerializer.Deserialize<CaseCourtNotificationPayload>(job.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        payload = mode switch
        {
            "timezone" => payload with { ReminderTimeZoneId = null },
            "actor" => payload with { ActorUserId = 99 },
            "action" => payload with { Action = "created" },
            _ => payload
        };
        job.PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (mode == "journal") job.JournalChanged = true;
        if (mode == "kind") job.Kind = "deadline";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var domain = await ReminderDomainAsync(db);
        var inbox = JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync());
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(job.Id, default));
        Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == job.Id)).BlockedAt);
        Assert.Empty(publisher.Delivered);
        Assert.Equal(inbox, JsonSerializer.Serialize(await db.UserNotifications.AsNoTracking().SingleAsync()));
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtReminder_RejectsCrossedCaseAndDeadlinePairsBeforePersistence(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        var other = new ClientCase { ClientId = seed.Case.ClientId, Title = "Другое дело", Stage = "Суд", ResponsibleUserId = seed.UserId };
        var deadline = new ClientCaseProceduralDeadline { ClientCase = other, Title = "Другой срок", DeadlineType = "appeal", DueAt = seed.Deadline.DueAt, WarningDaysBefore = 7, Status = "active" };
        db.ClientCaseProceduralDeadlines.Add(deadline); await db.SaveChangesAsync();
        var domain = await ReminderDomainAsync(db);
        Assert.Equal(0, await ReminderService(db, new ReminderAdmissionRealtime()).NotifyCaseProceduralDeadlineRemindersAsync(
            [new(seed.Case, deadline, [seed.UserId]), new(other, seed.Deadline, [seed.UserId])]));
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    private sealed class ReminderFixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

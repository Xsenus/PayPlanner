using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    public static IEnumerable<object[]> SecondaryCases() =>
        from channel in new[] { "email", "push", "max" }
        from mode in new[] { "active", "stale", "ban", "access", "missing", "newer", "orphan", "malformed" }
        from postgres in new[] { false, true }
        select new object[] { channel, mode, postgres };

    [Theory]
    [MemberData(nameof(SecondaryCases))]
    public async Task CourtNoticeSecondary_RetryUsesCurrentSourcePermissionAndConsent(string channel, string mode, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, decision: channel == "email", emailEnabled: channel == "email");
        if (channel != "email")
            db.UserSettings.Add(new()
            {
                UserId = seed.UserId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
            });
        if (channel == "push") db.UserPushSubscriptions.Add(new()
        {
            UserId = seed.UserId,
            Endpoint = "https://push.synthetic.local/one",
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
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var email = new CourtEmail { Fail = true };
        var push = new SecondaryPush(db) { Fail = true };
        var api = new SecondaryMaxApi { Fail = true };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, email, new NotificationServiceTests.RecordingTelegramService(),
            realtime, channel == "push" ? push : null, maxService: channel == "max" ? runtime : null,
            maxOutboxService: channel == "max" ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        var dispatch = new CaseCourtNotificationDispatchProcessor(db, service, realtime);
        Assert.Equal(channel == "max", await dispatch.ProcessAsync(seed.IntentId, default));
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        var max = new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance);
        if (channel == "max") Assert.Equal(1, await max.ProcessBatchAsync(default));
        int Attempts() => channel == "email" ? email.Attempts.Count : channel == "push" ? push.Calls : api.Calls;
        Assert.Equal(1, Attempts());
        var original = channel == "max" ? null : await db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(row => row.Channel == (channel == "email" ? NotificationChannels.Email : NotificationChannels.WebPush));
        if (original is not null) Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
        await ChangeSecondaryStateAsync(db, seed, inbox, channel, mode);
        db.ChangeTracker.Clear();
        var domain = await DomainAsync(db);
        email.Fail = push.Fail = api.Fail = false;
        if (channel == "max")
        {
            await db.MaxOutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            Assert.Equal(1, await max.ProcessBatchAsync(default));
            var row = await db.MaxOutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal(mode == "active" ? MaxOutboxStatuses.Sent : MaxOutboxStatuses.Cancelled, row.Status);
        }
        else
        {
            // Direct push retries cannot bypass the accepted intent's claim and stable ledger.
            if (channel == "push")
            {
                Assert.Null(await push.RetryDeliveryAsync(original!.Id));
                Assert.Equal(1, Attempts());
            }
            var retries = await service.RetryDeliveryAsync(original!.Id);
            Assert.All(retries, row => Assert.Equal(inbox.EventKey, row.EventKey));
            Assert.DoesNotContain(await db.NotificationDeliveries.ToArrayAsync(), row => row.EventKey.Contains(":retry:", StringComparison.Ordinal));
        }
        Assert.Equal(mode == "active" ? 2 : 1, Attempts());
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(domain, await DomainAsync(db));
    }

    private static async Task ChangeSecondaryStateAsync(PaymentContext db, Seed seed, UserNotification inbox, string channel, string mode)
    {
        if (mode == "stale") await db.ClientCaseCourtEvents.Where(row => row.Id == seed.SourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Актуальное состояние"));
        if (mode == "missing") await db.ClientCaseCourtEvents.Where(row => row.Id == seed.SourceId).ExecuteDeleteAsync();
        if (mode == "orphan")
        {
            var replacementKey = "case-court:" + Guid.NewGuid().ToString("N");
            await db.CaseCourtNotificationOutboxes.Where(row => row.Id == seed.IntentId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.EventKey, replacementKey));
        }
        if (mode == "malformed")
        {
            const string malformed = "CASE-COURT:invalid";
            await db.NotificationDeliveries.ExecuteUpdateAsync(set => set.SetProperty(row => row.EventKey, malformed));
            await db.MaxOutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(row => row.EventKey, malformed));
        }
        if (mode == "access") await db.Roles.ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "restricted-synthetic"));
        if (mode == "ban")
        {
            var preference = await db.UserSettings.SingleAsync(row => row.UserId == seed.UserId && row.Key == UserNotificationPreferenceSettings.SettingKey);
            preference.Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([],
                new Dictionary<string, IReadOnlyList<string>> { [inbox.Type] = [channel == "email" ? NotificationChannels.Email : channel == "push" ? NotificationChannels.WebPush : NotificationChannels.Max] }));
            await db.SaveChangesAsync();
        }
        if (mode == "newer")
        {
            var source = await db.ClientCaseCourtEvents.SingleAsync(row => row.Id == seed.SourceId);
            var clientCase = await db.ClientCases.SingleAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(clientCase, 0, "Сотрудник",
                [new("event", "updated", source, null, false)], default));
            await transaction.CommitAsync();
        }
    }

    public static IEnumerable<object[]> FenceCases() =>
        from mode in new[] { "lease", "blocked", "pending" }
        from push in new[] { false, true }
        from postgres in new[] { false, true }
        select new object[] { mode, push, postgres };

    [Theory]
    [MemberData(nameof(FenceCases))]
    public async Task CourtNoticeSecondary_ManualRetryPreservesLeaseAndUnknownOutcomeFences(string mode, bool pushChannel, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, true, true);
        if (pushChannel) db.UserPushSubscriptions.Add(new() { UserId = seed.UserId, Endpoint = "https://push.synthetic.local/one", P256dh = "fake", Auth = "fake" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var email = new CourtEmail { Fail = true };
        var push = new SecondaryPush(db) { Fail = true };
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, email, new NotificationServiceTests.RecordingTelegramService(), realtime, pushChannel ? push : null);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(seed.IntentId, default));
        var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.Channel == (pushChannel ? NotificationChannels.WebPush : NotificationChannels.Email));
        var intent = await db.CaseCourtNotificationOutboxes.SingleAsync();
        if (mode == "lease") { intent.LeaseToken = "other-worker"; intent.LeaseUntil = DateTime.UtcNow.AddMinutes(1); }
        if (mode == "blocked") intent.BlockedAt = DateTime.UtcNow;
        if (mode == "pending") await db.NotificationDeliveries.Where(row => row.Id == delivery.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, NotificationDeliveryStatus.Pending));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var count = await db.NotificationDeliveries.CountAsync();
        email.Fail = push.Fail = false;
        if (mode == "pending") Assert.Equal("NotificationDeliveryPending", (await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryDeliveryAsync(delivery.Id))).Message);
        else Assert.Empty(await service.RetryDeliveryAsync(delivery.Id));
        Assert.Single(email.Attempts);
        Assert.Equal(pushChannel ? 1 : 0, push.Calls);
        Assert.Equal(count, await db.NotificationDeliveries.CountAsync());
        var after = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(before.LeaseToken, after.LeaseToken);
        Assert.Equal(before.BlockedAt, after.BlockedAt);
        Assert.Equal(before.NotificationCompletedAt, after.NotificationCompletedAt);
    }

    public static IEnumerable<object[]> SourceFieldCases() =>
        from field in new[] { "CourtInstance", "CourtId", "ExternalKey", "SecretaryName", "SourceConfidence", "PreviousScheduledAt", "PreviousAllDay", "DeadlineConfidence" }
        from postgres in new[] { false, true }
        select new object[] { field, postgres };

    [Theory]
    [MemberData(nameof(SourceFieldCases))]
    public async Task CourtNoticeSecondary_SourceIdentityAndPreviousScheduleSupersedeWaitingNotice(string field, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, false, true);
        var intentId = seed.IntentId;
        if (field == "DeadlineConfidence")
        {
            var deadline = new ClientCaseProceduralDeadline { ClientCaseId = (await db.ClientCases.SingleAsync()).Id, Title = "Срок", DueAt = DateTime.UtcNow.AddDays(4) };
            db.ClientCaseProceduralDeadlines.Add(deadline); await db.SaveChangesAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            intentId = Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(await db.ClientCases.SingleAsync(), 0,
                "Сотрудник", [new("deadline", "created", null, deadline, false)], default));
            await transaction.CommitAsync();
        }
        db.UserPushSubscriptions.Add(new() { UserId = seed.UserId, Endpoint = "https://push.synthetic.local/one", P256dh = "fake", Auth = "fake" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var push = new SecondaryPush(db) { Fail = true };
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, new CourtEmail(), new NotificationServiceTests.RecordingTelegramService(), realtime, push);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(intentId, default));
        Assert.Equal(1, push.Calls);
        if (field == "DeadlineConfidence") (await db.ClientCaseProceduralDeadlines.SingleAsync()).SourceConfidence = "confirmed";
        else
        {
            var source = await db.ClientCaseCourtEvents.SingleAsync();
            if (field == "CourtId")
            {
                var court = new ClientCaseCourt { Name = "Другой суд" }; db.ClientCaseCourts.Add(court); await db.SaveChangesAsync(); source.CourtId = court.Id;
            }
            else db.Entry(source).Property(field).CurrentValue = field == "PreviousScheduledAt" ? DateTime.UtcNow.AddDays(-1)
                : field == "PreviousAllDay" ? true : "changed";
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var domain = await DomainAsync(db);
        var deadlines = System.Text.Json.JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        var inboxId = (await db.UserNotifications.AsNoTracking().SingleAsync()).Id;
        push.Fail = false;
        Assert.Empty(await service.RetryDeliveryAsync((await db.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.Channel == NotificationChannels.WebPush)).Id));
        Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == intentId)).CompletedAt);
        Assert.Equal(1, push.Calls);
        Assert.Equal(inboxId, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(domain, await DomainAsync(db));
        Assert.Equal(deadlines, System.Text.Json.JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtNoticeSecondary_DedicatedRetriesDoNotStarveOrdinaryScheduler(bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, true, true);
        var email = new CourtEmail { Fail = true };
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, email, new NotificationServiceTests.RecordingTelegramService(), realtime);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, realtime).ProcessAsync(seed.IntentId, default));
        var old = DateTime.UtcNow.AddMinutes(-30);
        await db.NotificationDeliveries.ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedAt, old));
        var original = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
        db.NotificationDeliveries.Add(new()
        {
            EventKey = "ordinary:synthetic-failure",
            Channel = NotificationChannels.Email,
            SenderName = "Email",
            RecipientUserId = seed.UserId,
            RecipientAddress = "court-channel-first@synthetic.local",
            Subject = "Обычное уведомление",
            BodyPreview = "Синтетический текст",
            Status = NotificationDeliveryStatus.Failed,
            CreatedAt = old
        });
        db.SystemSettings.AddRange(new SystemSetting { Key = NotificationRetrySchedulerService.EnabledSettingKey, Value = "true" },
            new SystemSetting { Key = NotificationRetrySchedulerService.DelayMinutesSettingKey, Value = "1" },
            new SystemSetting { Key = NotificationRetrySchedulerService.BatchSizeSettingKey, Value = "1" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var services = new ServiceCollection().AddSingleton(db).AddSingleton<INotificationService>(service);
        await using var provider = services.BuildServiceProvider();
        using var scheduler = new NotificationRetrySchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), NullLogger<NotificationRetrySchedulerService>.Instance);
        Assert.Equal(1, await scheduler.CountEligibleRetriesAsync(default));
        email.Fail = false;
        var result = await scheduler.RunOnceAsync();
        Assert.Equal(1, result.RetriedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(2, email.Attempts.Count);
        Assert.Equal(0, await scheduler.CountEligibleRetriesAsync(default));
        var after = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Equal(before.NextAttemptAt, after.NextAttemptAt);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
        Assert.Equal(NotificationDeliveryStatus.Failed, (await db.NotificationDeliveries.AsNoTracking().SingleAsync(row => row.Id == original.Id)).Status);
        Assert.Single(await db.NotificationDeliveries.AsNoTracking().Where(row => row.Status == NotificationDeliveryStatus.Sent).ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtNoticeSecondary_RechecksSourceAfterEachTransportAwait(bool postgres, bool maxChannel)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedAsync(db, false, false);
        db.UserSettings.Add(new()
        {
            UserId = seed.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>()))
        });
        if (maxChannel) db.MaxRecipientLinks.Add(new()
        {
            UserId = seed.UserId,
            MaxUserId = 79001,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow.AddDays(-1)
        });
        else foreach (var endpoint in new[] { "one", "two" }) db.UserPushSubscriptions.Add(new()
        {
            UserId = seed.UserId,
            Endpoint = "https://push.synthetic.local/" + endpoint,
            P256dh = "fake",
            Auth = "fake"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var secondSource = maxChannel ? new ClientCaseCourtEvent
        {
            ClientCaseId = (await db.ClientCases.SingleAsync()).Id,
            Title = "Второе событие",
            ScheduledAt = DateTime.UtcNow.AddDays(2)
        } : null;
        long? secondId = null;
        if (secondSource is not null)
        {
            db.ClientCaseCourtEvents.Add(secondSource); await db.SaveChangesAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            secondId = Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(await db.ClientCases.SingleAsync(), 0,
                "Сотрудник", [new("event", "created", secondSource, null, false)], default));
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        async Task ChangeSource() => await db.ClientCaseCourtEvents.Where(row => row.Id == (secondSource == null ? seed.SourceId : secondSource.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Изменилось во время отправки"));
        var push = new SecondaryPush(db) { AfterSend = ChangeSource };
        var api = new SecondaryMaxApi { AfterSend = ChangeSource };
        var runtime = new CourtMaxSettings();
        var protection = new EphemeralDataProtectionProvider();
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var service = NotificationServiceTests.CreateService(db, new CourtEmail(), new NotificationServiceTests.RecordingTelegramService(), realtime,
            maxChannel ? null : push, maxService: maxChannel ? runtime : null,
            maxOutboxService: maxChannel ? new MaxOutboxService(db, runtime, protection, TimeProvider.System) : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.synthetic.local/" });
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, realtime);
        Assert.True(await processor.ProcessAsync(seed.IntentId, default));
        if (maxChannel)
        {
            Assert.True(await processor.ProcessAsync(secondId!.Value, default));
            await new MaxOutboxProcessor(db, runtime, api, protection, TimeProvider.System, NullLogger<MaxOutboxProcessor>.Instance).ProcessBatchAsync(default);
            Assert.Equal(1, api.Calls);
            var rows = await db.MaxOutboxMessages.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            Assert.Equal(MaxOutboxStatuses.Sent, rows[0].Status);
            Assert.Equal(MaxOutboxStatuses.Cancelled, rows[1].Status);
        }
        else
        {
            Assert.Equal(1, push.Calls);
            var rows = await db.NotificationDeliveries.AsNoTracking().Where(row => row.Channel == NotificationChannels.WebPush).ToArrayAsync();
            Assert.Single(rows, row => row.Status == NotificationDeliveryStatus.Sent);
            Assert.Single(rows, row => row.Status == NotificationDeliveryStatus.Skipped);
        }
        Assert.Empty(await db.TaskJournalItems.ToArrayAsync());
    }

    private sealed class SecondaryPush : WebPushNotificationService
    {
        private readonly PaymentContext context;
        public SecondaryPush(PaymentContext db) : base(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WebPush:Enabled"] = "true",
            ["WebPush:PublicKey"] = "public-key",
            ["WebPush:PrivateKey"] = "private-key",
            ["WebPush:Subject"] = "mailto:test@synthetic.local"
        }).Build(), NullLogger<WebPushNotificationService>.Instance) => context = db;
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Func<Task>? AfterSend { get; set; }
        protected override async Task SendSubscriptionAsync(UserPushSubscription subscription, string payload, string subject,
            string publicKey, string privateKey, CancellationToken cancellationToken)
        {
            Assert.Null(context.Database.CurrentTransaction);
            Calls++;
            if (Fail) throw new IOException("Synthetic push failure");
            if (AfterSend is not null) await AfterSend();
        }
    }

    private sealed class SecondaryMaxApi : IMaxApiClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Func<Task>? AfterSend { get; set; }
        public Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken ct) => throw new InvalidOperationException();
        public Task<MaxUploadSlotResult> CreateUploadSlotAsync(string mediaType, string botToken, CancellationToken ct) => throw new InvalidOperationException();
        public async Task<MaxApiResult> SendTextAsync(long recipientId, string text, string botToken, string? actionText = null, Uri? actionUrl = null, CancellationToken ct = default)
        {
            Calls++;
            if (AfterSend is not null) await AfterSend();
            return Fail ? new MaxApiResult(false, ErrorCode: "synthetic-failure", IsTransient: true) : new MaxApiResult(true, "synthetic-sent");
        }
    }
}

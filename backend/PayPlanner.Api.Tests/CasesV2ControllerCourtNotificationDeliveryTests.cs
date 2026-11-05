using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData("pending", false)]
    [InlineData("pending", true)]
    [InlineData("inactive", false)]
    [InlineData("inactive", true)]
    [InlineData("failed-then-sent", false)]
    [InlineData("failed-then-sent", true)]
    public async Task CourtRecovery_PushOutcomeRequiresReviewOnlyForCurrentUncertainEndpoint(string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var publisher = new CourtRecoveryPublisher(db) { FailUser = true };
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User,
            notificationService: NewCourtRecoveryService(db, publisher), realtimePublisher: publisher), seed, "event-create", default), "event-create");
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var notification = await db.UserNotifications.AsNoTracking().SingleAsync();
        const string endpoint = "https://push.synthetic.local/owned";
        db.UserPushSubscriptions.Add(new() { UserId = notification.RecipientUserId, Endpoint = endpoint, IsActive = mode != "inactive" });
        db.NotificationDeliveries.Add(new()
        {
            EventKey = intent.EventKey,
            RecipientUserId = notification.RecipientUserId,
            RecipientAddress = endpoint,
            Channel = NotificationChannels.WebPush,
            Status = mode == "failed-then-sent" ? NotificationDeliveryStatus.Failed : NotificationDeliveryStatus.Pending
        });
        await db.SaveChangesAsync();
        if (mode == "failed-then-sent")
        {
            db.NotificationDeliveries.Add(new()
            {
                EventKey = intent.EventKey,
                RecipientUserId = notification.RecipientUserId,
                RecipientAddress = endpoint,
                Channel = NotificationChannels.WebPush,
                Status = NotificationDeliveryStatus.Sent
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        var push = new NotificationServiceTests.RecordingWebPushNotificationService();
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == intent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var processor = new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, publisher, push), publisher);
        Assert.Equal(mode != "pending", await processor.ProcessAsync(intent.Id, default));
        var final = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        if (mode == "pending")
        {
            Assert.NotNull(final.BlockedAt);
            Assert.Null(final.CompletedAt);
            Assert.Empty(push.Notifications);
            var calls = publisher.UserCalls;
            Assert.False(await processor.ProcessAsync(intent.Id, default));
            Assert.Equal(calls, publisher.UserCalls);
        }
        else Assert.NotNull(final.CompletedAt);
        Assert.Single(await db.UserNotifications.ToArrayAsync());
    }

    [Theory]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("permission", false)]
    [InlineData("permission", true)]
    public async Task CourtRecovery_RechecksSourceAndAccessBetweenRealtimeAndPush(string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var push = new NotificationServiceTests.RecordingWebPushNotificationService();
        var publisher = new CourtRecoveryPublisher(db);
        publisher.BeforeUser = async () =>
        {
            var row = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
            if (mode == "source")
                await db.ClientCaseCourtEvents.Where(source => source.Id == row.SourceId).ExecuteUpdateAsync(set => set.SetProperty(source => source.Title, "Новое состояние"));
            else
            {
                var recipientId = await db.UserNotifications.Select(n => n.RecipientUserId).SingleAsync();
                await db.Users.Where(user => user.Id == recipientId).ExecuteUpdateAsync(set => set.SetProperty(user => user.IsActive, false));
            }
        };
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User,
            notificationService: NewCourtRecoveryService(db, publisher, push), realtimePublisher: publisher), seed, "event-create", default), "event-create");
        Assert.Single(publisher.Notifications);
        Assert.Empty(push.Notifications);
        Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
    }

    [Theory]
    [InlineData("parallel")]
    [InlineData("expired")]
    [InlineData("replaced")]
    [InlineData("payload")]
    public async Task CourtRecovery_PostgreSqlLeaseFencesConcurrentWorkerAndStaleAcknowledgement(string mode)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var seed = await SeedCourtRecoveryAsync(db);
        var publisher = new CourtRecoveryPublisher(db) { FailUser = true };
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User,
            notificationService: NewCourtRecoveryService(db, publisher), realtimePublisher: publisher), seed, "event-create", default), "event-create");
        var item = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.FailUser = false;
        publisher.BeforeUser = async () => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); };
        var active = new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, publisher), publisher).ProcessAsync(item.Id, default);
        await using var second = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var otherPublisher = new CourtRecoveryPublisher(second);
        var otherProcessor = new CaseCourtNotificationDispatchProcessor(second, NewCourtRecoveryService(second, otherPublisher), otherPublisher);
        bool completed;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(await otherProcessor.ProcessAsync(item.Id, default));
            if (mode == "expired") await second.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseUntil, (DateTime?)DateTime.UnixEpoch));
            if (mode == "replaced") await second.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, "other-owned-test-token"));
            if (mode == "payload") await second.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PayloadJson, "{}"));
        }
        finally { release.TrySetResult(); completed = await active; }
        Assert.Equal(mode == "parallel", completed);
        var current = await second.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        if (mode != "parallel")
        {
            Assert.Null(current.CompletedAt);
            Assert.Null(current.NotificationCompletedAt);
            await second.CaseCourtNotificationOutboxes.Where(row => row.Id == item.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.LeaseUntil, (DateTime?)DateTime.UnixEpoch).SetProperty(row => row.PayloadJson, item.PayloadJson));
            Assert.True(await otherProcessor.ProcessAsync(item.Id, default));
        }
        Assert.Single(await second.UserNotifications.ToArrayAsync());
        Assert.NotNull((await second.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Fact]
    public async Task TaskCoalescing_DeferredAuthorChangeToActorDoesNotGrantChangedFallback()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        var token = fixture.Entry.StatusTransitionToken;
        await db.TaskJournalItems.Where(task => task.Id == fixture.Entry.TaskJournalItemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.AuthorUserId, fixture.Reviewer.Id)
                .SetProperty(task => task.AuthorName, fixture.Reviewer.FullName));
        db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.Reviewer.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskCompleted"]}"""
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(token, (await db.TaskJournalItems.AsNoTracking().SingleAsync()).StatusTransitionToken);
        Assert.NotNull((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(publisher.Notifications);
    }

    [Theory]
    [InlineData("default", false)]
    [InlineData("typeOff", true)]
    [InlineData("inAppOff", true)]
    [InlineData("windowOff", true)]
    public async Task TaskCoalescing_OneAuthorNotificationPreservesOtherRecipientsAndChannelFallback(string preference, bool toast)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        if (preference != "default")
        {
            var value = preference switch
            {
                "typeOff" => """{"disabledTypes":["taskCompleted"]}""",
                "inAppOff" => """{"disabledChannels":{"taskCompleted":["inApp"]}}""",
                _ => """{"disabledChannels":{"taskCompleted":["forcedModal"]}}"""
            };
            db.UserSettings.Add(new UserSetting { UserId = fixture.Author.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = value });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);
        await service.NotifyTaskChangedForCompletionEventAsync(fixture.Entry.TaskJournalItem,
            ["Статус: Новая -> Готово", "Результат: — -> Готово"], fixture.Assignee.FullName, fixture.Assignee.Id, fixture.Entry.EventKey);
        db.ChangeTracker.Clear();

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var rows = await db.UserNotifications.ToListAsync();
        var authorRow = Assert.Single(rows, row => row.RecipientUserId == fixture.Author.Id);
        Assert.Equal(UserNotificationTypes.TaskCompleted, authorRow.Type);
        Assert.Equal(fixture.Entry.EventKey, authorRow.EventKey);
        using var metadata = JsonDocument.Parse(authorRow.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("coalescedTaskChange").GetBoolean());
        Assert.Equal(new[] { fixture.Reviewer.Id, fixture.Observer.Id }.Order(),
            rows.Where(row => row.Type == UserNotificationTypes.TaskChanged).Select(row => row.RecipientUserId).Order());
        Assert.Equal(toast, publisher.ToastEnabledValues[publisher.Notifications.FindIndex(row => row.Id == authorRow.Id)]);
    }

    [Theory]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType)]
    [InlineData("completion")]
    public async Task TaskCoalescing_ExplicitKeyCannotCoalesceUnrelatedOrLegacyCompletion(string eventType)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, eventType);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());

        await service.NotifyTaskChangedForCompletionEventAsync(fixture.Entry.TaskJournalItem,
            ["Статус: Новая -> Готово"], fixture.Assignee.FullName, fixture.Assignee.Id, fixture.Entry.EventKey);

        Assert.Single(await db.UserNotifications.Where(row => row.RecipientUserId == fixture.Author.Id && row.Type == UserNotificationTypes.TaskChanged).ToListAsync());
    }

    [Fact]
    public async Task TaskCoalescing_EmailFallbackWithoutInAppIsDurableAndIdempotent()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        await SeedEmailSettingsAsync(db);
        await db.TaskJournalItems.Where(task => task.Id == fixture.Entry.TaskJournalItemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.ChannelsJson, "[\"email\"]"));
        db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.Author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskCompleted"],"disabledChannels":{"taskChanged":["inApp"]}}"""
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var email = new RecordingEmailService();
        var service = CreateService(db, email, new RecordingTelegramService());
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);

        Assert.True(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(fixture.Author.Email, Assert.Single(email.Messages).To);
        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Equal(fixture.Entry.EventKey, (await db.NotificationDeliveries.SingleAsync()).EventKey);
    }

    [Fact]
    public async Task TaskCoalescing_PushFallbackUsesCanonicalInboxRow()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.Author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskCompleted"]}"""
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), webPushNotificationService: push);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var canonical = await db.UserNotifications.SingleAsync();
        Assert.Equal(canonical.Id, Assert.Single(push.Notifications).Id);
        Assert.Equal(UserNotificationTypes.TaskCompleted, canonical.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskCoalescing_FallbackReplayKeepsOneRowAndRespectsAcknowledgement(bool acknowledged)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.Author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskCompleted"]}"""
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new CompletionRetryPublisher(1);
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher, push);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
        var row = await db.UserNotifications.SingleAsync();
        row.IsRead = acknowledged;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ExpireCompletionLeaseAsync(db, fixture.Entry.Id);

        Assert.True(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(row.Id, (await db.UserNotifications.SingleAsync()).Id);
        Assert.Equal(acknowledged ? 0 : 1, publisher.Delivered.Count);
        Assert.Equal(acknowledged ? 0 : 1, push.Notifications.Count);
    }

    [Fact]
    public async Task TaskCoalescing_PushFallbackRequiresChangedInAppAlongsideItsOwnChannel()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.Author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledChannels":{"taskChanged":["inApp"]}}"""
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), webPushNotificationService: push);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(UserNotificationTypes.TaskCompleted, (await db.UserNotifications.SingleAsync()).Type);
        Assert.Empty(push.Notifications);
    }

    [Fact]
    public async Task TaskCoalescing_LateResultCorrectionRetainsIndependentChangeEvent()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        await service.NotifyTaskChangedAsync(fixture.Entry.TaskJournalItem, ["Результат: прежний -> уточнение"],
            fixture.Assignee.FullName, fixture.Assignee.Id);

        var authorRows = await db.UserNotifications.Where(row => row.RecipientUserId == fixture.Author.Id).ToListAsync();
        Assert.Single(authorRows, row => row.Type == UserNotificationTypes.TaskCompleted);
        Assert.Single(authorRows, row => row.Type == UserNotificationTypes.TaskChanged);
        Assert.Single(await db.TaskStatusNotificationOutboxes.ToListAsync());
    }

    [Fact]
    public async Task TaskCoalescing_RechecksEmailAndPushPreferencesAfterRealtimeAwait()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.CoalescedEventType);
        await SeedEmailSettingsAsync(db);
        await db.TaskJournalItems.Where(task => task.Id == fixture.Entry.TaskJournalItemId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.ChannelsJson, "[\"inApp\",\"email\"]"));
        var setting = new UserSetting
        {
            UserId = fixture.Author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskCompleted"]}"""
        };
        db.UserSettings.Add(setting);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var realtime = new CoalescedPreferenceChangingRealtime(async () =>
        {
            var current = await db.UserSettings.SingleAsync(item => item.Id == setting.Id);
            current.Value = """{"disabledTypes":["taskCompleted"],"disabledChannels":{"taskChanged":["email","webPush"]}}""";
            await db.SaveChangesAsync();
        });
        var email = new RecordingEmailService();
        var push = new RecordingWebPushNotificationService();
        var service = CreateService(db, email, new RecordingTelegramService(), realtime, push);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Empty(email.Messages);
        Assert.Empty(push.Notifications);
        Assert.Single(await db.UserNotifications.ToListAsync());
    }

    private sealed class CoalescedPreferenceChangingRealtime(Func<Task> change) : IRealtimeNotificationPublisher
    {
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default) => change();
    }
}

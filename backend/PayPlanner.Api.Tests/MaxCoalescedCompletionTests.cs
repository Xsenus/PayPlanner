using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class MaxOutboxServiceTests
{
    [Fact]
    public async Task CoalescedCompletion_MaxDoesNotFallbackForOriginalActorAtQueueOrSend()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        const string key = "task:coalesced:actor-recipient";
        await AddCoalescedMaxSourceAsync(fixture, task, key);
        var source = await fixture.Db.TaskStatusNotificationOutboxes.SingleAsync();
        source.ActorUserId = fixture.UserId;
        await fixture.Db.SaveChangesAsync();
        var request = TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key };
        var message = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        await SetCoalescedMaxPreferencesAsync(fixture, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(request, CancellationToken.None));
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, message.Status);
        Assert.Equal("recipient-preference-disabled", message.LastErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescedCompletion_MaxUsesChangedFallbackWithoutChangingPayloadType(bool longKey)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        var key = longKey ? new string('д', 120) : "task:coalesced:max";
        await AddCoalescedMaxSourceAsync(fixture, task, key);
        await SetCoalescedMaxPreferencesAsync(fixture, false);
        var request = TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key };

        var message = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        Assert.Equal(message.Id, (await fixture.Outbox.QueueAsync(request, CancellationToken.None)).Id);
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Single(api.Messages);
        Assert.Contains(UserNotificationTypes.Find(UserNotificationTypes.TaskCompleted)!.Label, api.Messages[0].Text);
        Assert.Equal(7, api.Messages[0].Text.Split('\n').Length);
        Assert.Contains($"Объект: задача №{task.Id},", api.Messages[0].Text);
        Assert.Equal(MaxOutboxStatuses.Sent, message.Status);
        Assert.Single(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CoalescedCompletion_MaxRechecksBothPreferenceTypesAfterEarlierMessageSend()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        await SetCoalescedMaxPreferencesAsync(fixture, false);
        foreach (var key in new[] { "task:coalesced:first", "task:coalesced:second" })
        {
            await AddCoalescedMaxSourceAsync(fixture, task, key);
            await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key }, CancellationToken.None);
        }
        var api = new CallbackMaxApi(() => SetCoalescedMaxPreferencesAsync(fixture, true));

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, api.Calls);
        var messages = await fixture.Db.MaxOutboxMessages.ToListAsync();
        Assert.Single(messages, message => message.Status == MaxOutboxStatuses.Sent);
        Assert.Single(messages, message => message.Status == MaxOutboxStatuses.Cancelled && message.LastErrorCode == "recipient-preference-disabled");
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("completed")]
    public async Task CoalescedCompletion_MaxRequiresItsOwnInAppChannelAtQueueAndSend(string source)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        const string key = "task:coalesced:source-in-app";
        await AddCoalescedMaxSourceAsync(fixture, task, key);
        var request = TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key };
        var message = await fixture.Outbox.QueueAsync(request, CancellationToken.None);
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = source == "changed"
                ? """{"disabledChannels":{"taskCompleted":["max"],"taskChanged":["inApp"]}}"""
                : """{"disabledChannels":{"taskCompleted":["inApp"],"taskChanged":["max"]}}"""
        });
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(request, CancellationToken.None));
        var api = new RecordingApiClient();
        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, message.Status);
        Assert.Equal("recipient-preference-disabled", message.LastErrorCode);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("legacy")]
    [InlineData("approval")]
    [InlineData("wrongTask")]
    public async Task CoalescedCompletion_MaxRejectsUnprovenMetadataFallback(string proof)
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        const string key = "task:coalesced:unproven";
        await AddCoalescedMaxSourceAsync(fixture, task, key);
        var source = await fixture.Db.TaskStatusNotificationOutboxes.SingleAsync();
        if (proof == "wrongTask")
        {
            var different = new TaskJournalItem { Title = "Другая задача", ObjectName = "Другое дело", DueAt = fixture.Now.AddDays(1) };
            fixture.Db.TaskJournalItems.Add(different);
            await fixture.Db.SaveChangesAsync();
            source.TaskJournalItemId = different.Id;
        }
        else
        {
            source.EventType = proof == "ordinary" ? TaskCompletionNotificationPolicy.OrdinaryEventType
                : proof == "approval" ? TaskCompletionNotificationPolicy.ApprovalEventType : "completion";
        }
        await fixture.Db.SaveChangesAsync();
        await SetCoalescedMaxPreferencesAsync(fixture, false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Outbox.QueueAsync(
            TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key }, CancellationToken.None));

        Assert.Empty(await fixture.Db.MaxOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CoalescedCompletion_MaxRechecksPersonalQuietHoursAtSendTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var task = await AddMaxTaskAsync(fixture);
        const string key = "task:coalesced:quiet";
        await AddCoalescedMaxSourceAsync(fixture, task, key);
        await SetCoalescedMaxPreferencesAsync(fixture, false);
        var message = await fixture.Outbox.QueueAsync(TaskRequest(fixture, task, UserNotificationTypes.TaskCompleted) with { EventKey = key }, CancellationToken.None);
        fixture.Db.UserSettings.Add(new UserSetting
        {
            UserId = fixture.UserId,
            Key = UserNotificationDoNotDisturbSettings.SettingKey,
            Value = """{"enabled":true,"vacationUntil":"2099-01-01","allowCritical":false}"""
        });
        await fixture.Db.SaveChangesAsync();
        var api = new RecordingApiClient();

        await fixture.Processor(api, TaskFrontend).ProcessBatchAsync(CancellationToken.None);

        Assert.Empty(api.Messages);
        Assert.Equal(MaxOutboxStatuses.Cancelled, message.Status);
    }

    private static async Task AddCoalescedMaxSourceAsync(Fixture fixture, TaskJournalItem task, string key)
    {
        var actor = await fixture.Db.Users.FirstOrDefaultAsync(user => user.Id != fixture.UserId);
        if (actor is null)
        {
            var recipient = await fixture.Db.Users.SingleAsync(user => user.Id == fixture.UserId);
            actor = new User
            {
                Email = "coalesced-actor@example.test",
                FullName = "Исполнитель",
                PasswordHash = "synthetic",
                RoleId = recipient.RoleId,
                IsActive = true,
                IsApproved = true
            };
            fixture.Db.Users.Add(actor);
            await fixture.Db.SaveChangesAsync();
        }
        task.AuthorUserId = fixture.UserId;
        task.Status = "done";
        fixture.Db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
        {
            TaskJournalItemId = task.Id,
            EventKey = key,
            EventType = TaskCompletionNotificationPolicy.CoalescedEventType,
            StatusTransitionToken = task.StatusTransitionToken,
            ActorUserId = actor.Id,
            CreatedAt = fixture.Now
        });
        fixture.Db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.UserId,
            Type = UserNotificationTypes.TaskCompleted,
            EventKey = key,
            TaskJournalItemId = task.Id,
            ObjectType = "task",
            ObjectId = task.Id,
            Title = "Задача выполнена",
            MetadataJson = """{"coalescedTaskChange":true}"""
        });
        await fixture.Db.SaveChangesAsync();
    }

    private static async Task SetCoalescedMaxPreferencesAsync(Fixture fixture, bool disableChanged)
    {
        var setting = await fixture.Db.UserSettings.SingleOrDefaultAsync(item => item.UserId == fixture.UserId && item.Key == UserNotificationPreferenceSettings.SettingKey);
        if (setting is null)
        {
            setting = new UserSetting { UserId = fixture.UserId, Key = UserNotificationPreferenceSettings.SettingKey };
            fixture.Db.UserSettings.Add(setting);
        }
        setting.Value = disableChanged
            ? """{"disabledChannels":{"taskCompleted":["max"],"taskChanged":["max"]}}"""
            : """{"disabledChannels":{"taskCompleted":["max"]}}""";
        await fixture.Db.SaveChangesAsync();
    }
}

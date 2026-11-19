using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationInboxControllerTests
{
    [Fact]
    public async Task TaskCompletion_InboxOriginProofFiltersBeforePagination()
    {
        await using var db = await CreateContextAsync();
        var (user, task, ordinary) = await SeedInboxCompletionAsync(db);
        ordinary.MetadataJson = "{broken";
        ordinary.CreatedAt = DateTime.UtcNow.AddDays(-2);
        for (var index = 0; index < 30; index++)
        {
            var legacy = CreateNotification(user.Id, $"task:legacy:{index}", UserNotificationTypes.TaskCompleted);
            legacy.TaskJournalItemId = task.Id;
            legacy.ObjectType = "task";
            legacy.ObjectId = task.Id;
            legacy.MetadataJson = """{"completionOrigin":"ordinary"}""";
            db.UserNotifications.Add(legacy);
        }
        var approved = CreateNotification(user.Id, "task:approval:proof", UserNotificationTypes.TaskCompleted);
        approved.TaskJournalItemId = task.Id;
        db.UserNotifications.Add(approved);
        db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
        {
            TaskJournalItemId = task.Id,
            EventKey = approved.EventKey,
            EventType = TaskCompletionNotificationPolicy.ApprovalEventType,
            StatusTransitionToken = task.StatusTransitionToken,
            ActorUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        var differentTask = CreateTask(user.Id, "Другая задача");
        db.TaskJournalItems.Add(differentTask);
        await db.SaveChangesAsync();
        var mismatch = CreateNotification(user.Id, "task:wrong-task:proof", UserNotificationTypes.TaskCompleted);
        mismatch.TaskJournalItemId = task.Id;
        db.UserNotifications.Add(mismatch);
        db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
        {
            TaskJournalItemId = differentTask.Id,
            EventKey = mismatch.EventKey,
            EventType = TaskCompletionNotificationPolicy.OrdinaryEventType,
            StatusTransitionToken = differentTask.StatusTransitionToken,
            ActorUserId = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var result = await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, requiresAction: true, pageSize: 25);

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        var dto = Assert.Single(page.Items);
        Assert.Equal(ordinary.Id, dto.Id);
        Assert.True(dto.ForcedModalEnabled);
        Assert.False(dto.ToastEnabled);
        Assert.Equal("{broken", dto.MetadataJson);
        var all = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("active", UserNotificationTypes.TaskCompleted, null, pageSize: 100)).Result).Value);
        Assert.Equal(33, all.Total);
        Assert.All(all.Items.Where(item => item.Id != ordinary.Id), item => Assert.False(item.ForcedModalEnabled));
    }

    [Theory]
    [InlineData("unread", "forcedOff", false, true)]
    [InlineData("snooze", "forcedOff", false, true)]
    [InlineData("unread", "inAppOff", false, false)]
    [InlineData("snooze", "inAppOff", false, false)]
    [InlineData("unread", "quietRole", false, false)]
    [InlineData("snooze", "quietRole", false, false)]
    [InlineData("unread", "default", true, false)]
    public async Task TaskCompletion_MutationDtoUsesCurrentChannelPreferences(string action, string setting, bool forced, bool toast)
    {
        await using var db = await CreateContextAsync();
        var (user, _, notification) = await SeedInboxCompletionAsync(db);
        notification.IsRead = true;
        var disabledChannel = setting == "inAppOff" ? NotificationChannels.InApp : NotificationChannels.ForcedModal;
        var preferences = new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>
        {
            [UserNotificationTypes.TaskCompleted] = [disabledChannel]
        });
        if (setting == "quietRole")
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = RoleNotificationPreferenceSettings.SettingKey,
                Value = RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot> { [user.RoleId] = preferences })
            });
            db.UserSettings.Add(new UserSetting
            {
                UserId = user.Id,
                Key = UserNotificationDoNotDisturbSettings.SettingKey,
                Value = """{"enabled":true,"vacationUntil":"2099-01-01","allowCritical":false}"""
            });
        }
        else if (setting != "default")
        {
            db.UserSettings.Add(new UserSetting { UserId = user.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(preferences) });
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var result = action == "unread"
            ? await controller.MarkUnread(notification.Id, CancellationToken.None)
            : await controller.Snooze(notification.Id, new SnoozeUserNotificationRequest { Minutes = 30 }, CancellationToken.None);

        var dto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.IsRead);
        Assert.Equal(forced, dto.ForcedModalEnabled);
        Assert.Equal(toast, dto.ToastEnabled);
        if (action == "snooze") Assert.NotNull(dto.SnoozedUntil);
    }

    [Fact]
    public async Task TaskCompletion_MarkAllReadRequiresActionLeavesLegacyAndApprovalUnread()
    {
        await using var db = await CreateContextAsync();
        var (user, task, ordinary) = await SeedInboxCompletionAsync(db);
        var legacy = CreateNotification(user.Id, "task:legacy:mark-all", UserNotificationTypes.TaskCompleted);
        legacy.TaskJournalItemId = task.Id;
        db.UserNotifications.Add(legacy);
        await db.SaveChangesAsync();

        var result = await CreateController(db, user.Id).MarkAllRead(null, UserNotificationTypes.TaskCompleted, null, requiresAction: true);

        var dto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, dto.UpdatedCount);
        Assert.True((await db.UserNotifications.SingleAsync(item => item.Id == ordinary.Id)).IsRead);
        Assert.False((await db.UserNotifications.SingleAsync(item => item.Id == legacy.Id)).IsRead);
    }

    [Theory]
    [InlineData("typeDisabled")]
    [InlineData("roleTypeDisabled")]
    [InlineData("inAppDisabled")]
    public async Task TaskCompletion_CoalescedInboxRetainsChangedFallbackAcrossReadsAndActions(string setting)
    {
        await using var db = await CreateContextAsync();
        var (user, _, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        notification.MetadataJson = "{broken";
        var preferences = setting == "inAppDisabled"
            ? new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>
            {
                [UserNotificationTypes.TaskCompleted] = [NotificationChannels.InApp]
            })
            : new UserNotificationPreferenceSnapshot([UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>());
        if (setting == "roleTypeDisabled")
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = RoleNotificationPreferenceSettings.SettingKey,
                Value = RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot> { [user.RoleId] = preferences })
            });
        }
        else
        {
            db.UserSettings.Add(new UserSetting { UserId = user.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(preferences) });
        }
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        Assert.Equal(1, Assert.IsType<OkObjectResult>((await controller.GetUnreadCount(default)).Result).Value);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, pageSize: 1)).Result).Value);
        Assert.Equal(1, page.Total);
        var dto = Assert.Single(page.Items);
        Assert.Equal(notification.Id, dto.Id);
        Assert.Equal(UserNotificationTypes.TaskCompleted, dto.Type);
        Assert.False(dto.ForcedModalEnabled);
        Assert.True(dto.ToastEnabled);

        var read = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>((await controller.MarkRead(notification.Id, default)).Result).Value);
        Assert.True(read.IsRead);
        Assert.False(read.ForcedModalEnabled);
        var unread = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>((await controller.MarkUnread(notification.Id, default)).Result).Value);
        Assert.False(unread.IsRead);
        Assert.False(unread.ForcedModalEnabled);
        Assert.True(unread.ToastEnabled);
        var snoozed = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(
            (await controller.Snooze(notification.Id, new SnoozeUserNotificationRequest { Minutes = 30 }, default)).Result).Value);
        Assert.NotNull(snoozed.SnoozedUntil);
        Assert.False(snoozed.ForcedModalEnabled);
        Assert.True(snoozed.ToastEnabled);
        await controller.MarkUnread(notification.Id, default);
        var markAll = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(
            (await controller.MarkAllRead(null, UserNotificationTypes.TaskCompleted, null)).Result).Value);
        Assert.Equal(1, markAll.UpdatedCount);
        var archived = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(
            (await controller.ArchiveSelected(new BulkIdsRequest { Ids = [notification.Id] }, default)).Result).Value);
        Assert.Equal(1, archived.UpdatedCount);
        Assert.True((await db.UserNotifications.SingleAsync()).IsArchived);
    }

    [Fact]
    public async Task TaskCompletion_CoalescedProofRetainsOrdinaryCentralWindowWithoutMetadata()
    {
        await using var db = await CreateContextAsync();
        var (user, _, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        notification.MetadataJson = "{broken";
        await db.SaveChangesAsync();

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await CreateController(db, user.Id).GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, requiresAction: true, pageSize: 1)).Result).Value);

        Assert.Equal(1, page.Total);
        var dto = Assert.Single(page.Items);
        Assert.Equal(notification.Id, dto.Id);
        Assert.True(dto.ForcedModalEnabled);
        Assert.False(dto.ToastEnabled);
    }

    [Theory]
    [InlineData("metadataOnly")]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType)]
    [InlineData("completion")]
    [InlineData(TaskCompletionNotificationPolicy.ApprovalEventType)]
    [InlineData("wrongTask")]
    public async Task TaskCompletion_ChangedFallbackRequiresExactCoalescedOutboxProof(string proof)
    {
        await using var db = await CreateContextAsync();
        var (user, task, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        var origin = await db.TaskStatusNotificationOutboxes.SingleAsync();
        notification.MetadataJson = """{"coalescedChange":true,"completionOrigin":"ordinary"}""";
        if (proof == "metadataOnly")
        {
            db.TaskStatusNotificationOutboxes.Remove(origin);
        }
        else if (proof == "wrongTask")
        {
            var otherTask = CreateTask(user.Id, "Другая задача");
            db.TaskJournalItems.Add(otherTask);
            await db.SaveChangesAsync();
            origin.TaskJournalItemId = otherTask.Id;
            origin.EventType = TaskCompletionNotificationPolicy.CoalescedEventType;
        }
        else
        {
            origin.EventType = proof;
        }
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>()))
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        Assert.Equal(0, Assert.IsType<OkObjectResult>((await controller.GetUnreadCount(default)).Result).Value);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("active", UserNotificationTypes.TaskCompleted, null)).Result).Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        Assert.IsType<NotFoundResult>((await controller.MarkUnread(notification.Id, default)).Result);
        Assert.Equal(task.Id, (await db.UserNotifications.SingleAsync()).TaskJournalItemId);
    }

    [Fact]
    public async Task TaskCompletion_CoalescedFallbackFiltersBeforePaginationAndUsesCurrentQuietHours()
    {
        await using var db = await CreateContextAsync();
        var (user, task, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        notification.CreatedAt = DateTime.UtcNow.AddDays(-2);
        for (var index = 0; index < 30; index++)
        {
            var legacy = CreateNotification(user.Id, $"task:coalesced-filter:legacy:{index}", UserNotificationTypes.TaskCompleted);
            legacy.TaskJournalItemId = task.Id;
            legacy.ObjectType = "task";
            legacy.ObjectId = task.Id;
            legacy.MetadataJson = """{"coalescedChange":true}""";
            db.UserNotifications.Add(legacy);
        }
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>()))
        });
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationDoNotDisturbSettings.SettingKey,
            Value = """{"enabled":true,"vacationUntil":"2099-01-01","allowCritical":false}"""
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, pageSize: 1)).Result).Value);
        Assert.Equal(1, page.Total);
        var dto = Assert.Single(page.Items);
        Assert.Equal(notification.Id, dto.Id);
        Assert.False(dto.ForcedModalEnabled);
        Assert.False(dto.ToastEnabled);
        Assert.Equal(1, Assert.IsType<OkObjectResult>((await controller.GetUnreadCount(default)).Result).Value);
        var markAll = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(
            (await controller.MarkAllRead("unread", UserNotificationTypes.TaskCompleted, null)).Result).Value);
        Assert.Equal(1, markAll.UpdatedCount);
        Assert.All(await db.UserNotifications.Where(item => item.Id != notification.Id).ToListAsync(), item => Assert.False(item.IsRead));
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("taskAccess")]
    [InlineData("bothTypesDisabled")]
    public async Task TaskCompletion_CoalescedFallbackPreservesCurrentOwnershipAndTypeRestrictions(string restriction)
    {
        await using var db = await CreateContextAsync();
        var (user, task, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        var outsider = await CreateUserAsync(db, "coalesced-outsider@example.test");
        if (restriction == "recipient") notification.RecipientUserId = outsider.Id;
        if (restriction == "taskAccess")
        {
            task.AuthorUserId = outsider.Id;
            task.AssigneeUserId = outsider.Id;
        }
        var disabledTypes = restriction == "bothTypesDisabled"
            ? new[] { UserNotificationTypes.TaskCompleted, UserNotificationTypes.TaskChanged }
            : new[] { UserNotificationTypes.TaskCompleted };
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(disabledTypes, new Dictionary<string, IReadOnlyList<string>>()))
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        Assert.Equal(0, Assert.IsType<OkObjectResult>((await controller.GetUnreadCount(default)).Result).Value);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("active", null, null)).Result).Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        Assert.IsType<NotFoundResult>((await controller.MarkRead(notification.Id, default)).Result);
    }

    [Fact]
    public async Task TaskCompletion_CoalescedActorCannotUseChangedFallbackButRetainsPrimaryCompletion()
    {
        await using var db = await CreateContextAsync();
        var (user, _, notification) = await SeedInboxCompletionAsync(db, coalesced: true);
        (await db.TaskStatusNotificationOutboxes.SingleAsync()).ActorUserId = user.Id;
        var preference = new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot(
                [UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>()))
        };
        db.UserSettings.Add(preference);
        await db.SaveChangesAsync();
        var controller = CreateController(db, user.Id);

        Assert.Equal(0, Assert.IsType<OkObjectResult>((await controller.GetUnreadCount(default)).Result).Value);
        var hidden = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null)).Result).Value);
        Assert.Equal(0, hidden.Total);
        Assert.Empty(hidden.Items);
        Assert.IsType<NotFoundResult>((await controller.MarkUnread(notification.Id, default)).Result);

        preference.Value = """{"disabledChannels":{"taskCompleted":["inApp"]}}""";
        await db.SaveChangesAsync();
        var withoutWindow = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null)).Result).Value);
        var withoutWindowDto = Assert.Single(withoutWindow.Items);
        Assert.False(withoutWindowDto.ForcedModalEnabled);
        Assert.False(withoutWindowDto.ToastEnabled);

        preference.Value = "{}";
        await db.SaveChangesAsync();
        var visible = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await controller.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, requiresAction: true)).Result).Value);
        Assert.Equal(1, visible.Total);
        var dto = Assert.Single(visible.Items);
        Assert.Equal(notification.Id, dto.Id);
        Assert.True(dto.ForcedModalEnabled);
        Assert.False(dto.ToastEnabled);
    }

    private static async Task<(User User, TaskJournalItem Task, UserNotification Notification)> SeedInboxCompletionAsync(PaymentContext db, bool coalesced = false)
    {
        var user = await CreateUserAsync(db, "completion-inbox@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var task = CreateTask(user.Id, "Завершённая задача");
        task.Status = "done";
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var actor = coalesced ? await CreateUserAsync(db, "completion-inbox-actor@example.test") : user;
        var notification = CreateNotification(user.Id, "task:inbox:completion", UserNotificationTypes.TaskCompleted);
        notification.TaskJournalItemId = task.Id;
        notification.ObjectType = "task";
        notification.ObjectId = task.Id;
        db.UserNotifications.Add(notification);
        db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
        {
            TaskJournalItemId = task.Id,
            EventKey = notification.EventKey,
            EventType = coalesced ? TaskCompletionNotificationPolicy.CoalescedEventType : TaskCompletionNotificationPolicy.OrdinaryEventType,
            StatusTransitionToken = task.StatusTransitionToken,
            ActorUserId = actor.Id,
            ActorName = "Автор",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (user, task, notification);
    }
}

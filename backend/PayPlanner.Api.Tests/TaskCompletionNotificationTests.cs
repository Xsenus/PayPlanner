using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Theory]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType, "ordinary", false, false)]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType, "ordinary", false, true)]
    [InlineData(TaskCompletionNotificationPolicy.ApprovalEventType, "approval", true, false)]
    [InlineData("completion", "legacy", true, false)]
    public async Task TaskCompletion_OriginMatchesInboxAndRealtime(string eventType, string origin, bool toast, bool authorCompletes)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, eventType);
        if (authorCompletes)
            await db.TaskStatusNotificationOutboxes.Where(item => item.Id == fixture.Entry.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ActorUserId, fixture.Author.Id));
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var completed = Assert.Single(await db.UserNotifications.Where(item => item.Type == UserNotificationTypes.TaskCompleted).ToListAsync());
        Assert.Equal(fixture.Author.Id, completed.RecipientUserId);
        using var metadata = JsonDocument.Parse(completed.MetadataJson);
        Assert.Equal(origin, metadata.RootElement.GetProperty("completionOrigin").GetString());
        var publishedIndex = publisher.Notifications.FindIndex(item => item.Id == completed.Id);
        Assert.True(publishedIndex >= 0);
        Assert.Equal(toast, publisher.ToastEnabledValues[publishedIndex]);
        var inbox = CompletionInbox(db, fixture.Author.Id);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await inbox.GetNotifications("active", UserNotificationTypes.TaskCompleted, null)).Result).Value);
        var dto = Assert.Single(page.Items);
        Assert.Equal(!toast, dto.ForcedModalEnabled);
        Assert.Equal(toast, dto.ToastEnabled);
        var pending = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await inbox.GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, requiresAction: true)).Result).Value);
        Assert.Equal(eventType == TaskCompletionNotificationPolicy.OrdinaryEventType ? 1 : 0, pending.Total);
        if (eventType == TaskCompletionNotificationPolicy.ApprovalEventType)
        {
            var approved = await db.UserNotifications.Where(item => item.Type == UserNotificationTypes.TaskApproved).ToListAsync();
            Assert.Equal(new[] { fixture.Author.Id, fixture.Assignee.Id, fixture.Observer.Id }.Order(), approved.Select(item => item.RecipientUserId).Order());
            Assert.All(approved, item => Assert.Equal(TaskCompletionNotificationPolicy.ApprovalDecisionEventKey(fixture.Entry.EventKey), item.EventKey));
        }
    }

    [Theory]
    [InlineData("default", true, false)]
    [InlineData("personal", false, true)]
    [InlineData("role", false, true)]
    [InlineData("personalOverride", true, false)]
    [InlineData("toastDisabled", true, false)]
    public async Task TaskCompletion_RespectsPersonalAndRoleChannels(string setting, bool forced, bool toast)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db);
        var disabledWindow = new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>
        {
            [UserNotificationTypes.TaskCompleted] = [NotificationChannels.ForcedModal]
        });
        if (setting is "role" or "personalOverride")
        {
            db.SystemSettings.Add(Setting(RoleNotificationPreferenceSettings.SettingKey,
                RoleNotificationPreferenceSettings.Serialize(new Dictionary<int, UserNotificationPreferenceSnapshot>
                {
                    [fixture.Author.RoleId] = disabledWindow
                })));
        }
        if (setting is "personal" or "personalOverride" or "toastDisabled")
        {
            var preferences = setting == "personal" ? disabledWindow
                : setting == "toastDisabled" ? new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>
                {
                    [UserNotificationTypes.TaskCompleted] = [NotificationChannels.Toast]
                }) : UserNotificationPreferenceSettings.Empty;
            db.UserSettings.Add(new UserSetting { UserId = fixture.Author.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(preferences) });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(toast, Assert.Single(publisher.ToastEnabledValues));
        var inbox = CompletionInbox(db, fixture.Author.Id);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await inbox.GetNotifications("active", null, null)).Result).Value);
        var dto = Assert.Single(page.Items);
        Assert.Equal(forced, dto.ForcedModalEnabled);
        Assert.Equal(toast, dto.ToastEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskCompletion_DisabledInAppOrTypeDoesNotCreateNotification(bool disableType)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db);
        var preferences = disableType
            ? new UserNotificationPreferenceSnapshot([UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>())
            : new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>>
            {
                [UserNotificationTypes.TaskCompleted] = [NotificationChannels.InApp]
            });
        db.UserSettings.Add(new UserSetting { UserId = fixture.Author.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(preferences) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(publisher.Notifications);
    }

    [Fact]
    public async Task TaskCompletion_InboxLoadsOrdinaryProofOnceForWholePage()
    {
        var commands = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(commands);
        var fixture = await SeedCompletionEventAsync(db);
        for (var index = 0; index < 20; index++)
        {
            var eventKey = $"task:page:completion:{index}";
            db.UserNotifications.Add(new UserNotification
            {
                RecipientUserId = fixture.Author.Id,
                Type = UserNotificationTypes.TaskCompleted,
                EventKey = eventKey,
                Title = "Задача выполнена",
                ObjectType = "task",
                ObjectId = fixture.Entry.TaskJournalItemId,
                TaskJournalItemId = fixture.Entry.TaskJournalItemId
            });
            db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
            {
                TaskJournalItemId = fixture.Entry.TaskJournalItemId,
                EventKey = eventKey,
                EventType = TaskCompletionNotificationPolicy.OrdinaryEventType,
                StatusTransitionToken = fixture.Entry.StatusTransitionToken,
                ActorUserId = fixture.Author.Id,
                CreatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        commands.ReaderCommands.Clear();

        var result = await CompletionInbox(db, fixture.Author.Id).GetNotifications("unread", UserNotificationTypes.TaskCompleted, null);

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(20, page.Items.Count);
        Assert.All(page.Items, item => Assert.True(item.ForcedModalEnabled));
        Assert.Single(commands.ReaderCommands, command => command.Contains("TaskStatusNotificationOutboxes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TaskCompletion_ReplaysDurablyWithCurrentChannelsAndReadState(bool readBeforeRetry, bool disableWindow)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db);
        var publisher = new CompletionRetryPublisher(1);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
        var saved = await db.UserNotifications.SingleAsync();
        var metadata = saved.MetadataJson;
        saved.IsRead = readBeforeRetry;
        if (disableWindow)
            db.UserSettings.Add(new UserSetting { UserId = fixture.Author.Id, Key = UserNotificationPreferenceSettings.SettingKey, Value = """{"disabledChannels":{"taskCompleted":["forcedModal"]}}""" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ExpireCompletionLeaseAsync(db, fixture.Entry.Id);

        Assert.True(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var recovered = await db.UserNotifications.SingleAsync();
        Assert.Equal(saved.Id, recovered.Id);
        Assert.Equal(metadata, recovered.MetadataJson);
        Assert.Equal(readBeforeRetry ? 0 : 1, publisher.Delivered.Count);
        if (!readBeforeRetry) Assert.Equal(disableWindow, Assert.Single(publisher.Delivered).Toast);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("{\"completionOrigin\":\"approval\"}")]
    public async Task TaskCompletion_StoredMetadataCannotOverridePersistedOrdinaryOrigin(string metadata)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db);
        db.UserNotifications.Add(new UserNotification
        {
            RecipientUserId = fixture.Author.Id,
            Type = UserNotificationTypes.TaskCompleted,
            EventKey = fixture.Entry.EventKey,
            ObjectType = "task",
            ObjectId = fixture.Entry.TaskJournalItemId,
            TaskJournalItemId = fixture.Entry.TaskJournalItemId,
            MetadataJson = metadata,
            Title = "Задача выполнена"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(metadata, (await db.UserNotifications.SingleAsync()).MetadataJson);
        Assert.False(Assert.Single(publisher.ToastEnabledValues));
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await CompletionInbox(db, fixture.Author.Id).GetNotifications("unread", UserNotificationTypes.TaskCompleted, null, requiresAction: true)).Result).Value);
        Assert.True(Assert.Single(page.Items).ForcedModalEnabled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TaskCompletion_ConflictingStoredIdentityDoesNotReplayAnyTransport(bool wrongType, bool coalesced)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, coalesced ? TaskCompletionNotificationPolicy.CoalescedEventType : TaskCompletionNotificationPolicy.OrdinaryEventType);
        if (coalesced)
        {
            await SeedEmailSettingsAsync(db);
            await db.TaskJournalItems.Where(task => task.Id == fixture.Entry.TaskJournalItemId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.ChannelsJson, "[\"inApp\",\"email\"]"));
        }
        var anotherTask = new TaskJournalItem
        {
            ObjectName = "Другой контекст",
            Title = "Другая задача",
            AuthorUserId = fixture.Author.Id,
            AssigneeUserId = fixture.Author.Id,
            DueAt = DateTime.UtcNow.AddHours(1)
        };
        db.TaskJournalItems.Add(anotherTask);
        db.MaxRecipientLinks.Add(new MaxRecipientLink
        {
            UserId = fixture.Author.Id,
            MaxUserId = 7991,
            Status = MaxRecipientLinkStatuses.Verified,
            LinkTokenHash = Guid.NewGuid().ToString("N"),
            VerifiedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var storedTaskId = wrongType ? fixture.Entry.TaskJournalItemId : anotherTask.Id;
        var stored = new UserNotification
        {
            RecipientUserId = fixture.Author.Id,
            EventKey = fixture.Entry.EventKey,
            Type = wrongType ? UserNotificationTypes.TaskChanged : UserNotificationTypes.TaskCompleted,
            TaskJournalItemId = storedTaskId,
            ObjectType = "task",
            ObjectId = storedTaskId,
            Title = "Прежнее уведомление",
            BodyPreview = "Прежний контекст",
            MetadataJson = " {\"legacy\":\"kept\"} "
        };
        db.UserNotifications.Add(stored);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var push = new RecordingWebPushNotificationService();
        var max = new RecordingMaxOutboxService();
        var email = new RecordingEmailService();
        var service = CreateService(db, email, new RecordingTelegramService(), publisher, push,
            maxService: new ReadyMaxNotificationService(), maxOutboxService: max,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" });

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var retained = await db.UserNotifications.SingleAsync();
        Assert.Equal(stored.Id, retained.Id);
        Assert.Equal(stored.MetadataJson, retained.MetadataJson);
        Assert.Equal(stored.Type, retained.Type);
        Assert.Equal(storedTaskId, retained.TaskJournalItemId);
        Assert.Empty(publisher.Notifications);
        Assert.Empty(push.Notifications);
        Assert.Empty(max.Requests);
        Assert.Empty(email.Messages);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await CompletionInbox(db, fixture.Author.Id).GetNotifications("active", null, null)).Result).Value);
        Assert.False(Assert.Single(page.Items).ForcedModalEnabled);
    }

    [Theory]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType)]
    [InlineData(TaskCompletionNotificationPolicy.ApprovalEventType)]
    public async Task TaskCompletion_ReopenedTaskDiscardsOldEvent(string eventType)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, eventType);
        var task = await db.TaskJournalItems.SingleAsync();
        task.Status = "inProgress";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(publisher.Notifications);
    }

    [Theory]
    [InlineData(TaskCompletionNotificationPolicy.OrdinaryEventType)]
    [InlineData(TaskCompletionNotificationPolicy.ApprovalEventType)]
    public async Task TaskCompletion_DeferredEventRechecksRecipientAccess(string eventType)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, eventType);
        await db.Users.Where(user => user.Id == fixture.Author.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.DoesNotContain(await db.UserNotifications.ToListAsync(), item => item.RecipientUserId == fixture.Author.Id);
        Assert.DoesNotContain(publisher.Notifications, item => item.RecipientUserId == fixture.Author.Id);
        Assert.Equal(eventType == TaskCompletionNotificationPolicy.ApprovalEventType ? 2 : 0, publisher.Notifications.Count);
    }

    [Fact]
    public async Task TaskCompletion_ApprovalRecoveryRetainsRecipientsWithoutDuplicateCentralType()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.ApprovalEventType);
        var publisher = new CompletionRetryPublisher(2);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
        Assert.Equal(3, await db.UserNotifications.CountAsync(item => item.Type == UserNotificationTypes.TaskApproved));
        var acknowledged = await db.UserNotifications.SingleAsync(item => item.RecipientUserId == fixture.Author.Id);
        acknowledged.IsRead = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ExpireCompletionLeaseAsync(db, fixture.Entry.Id);

        Assert.True(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        var rows = await db.UserNotifications.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(3, rows.Count(item => item.Type == UserNotificationTypes.TaskApproved));
        Assert.Single(rows, item => item.Type == UserNotificationTypes.TaskCompleted);
        Assert.DoesNotContain(rows, item => item.RecipientUserId == fixture.Reviewer.Id);
        Assert.Equal(acknowledged.Id, rows.Single(item => item.RecipientUserId == fixture.Author.Id && item.Type == UserNotificationTypes.TaskApproved).Id);
        Assert.Single(publisher.Delivered, item => item.Type == UserNotificationTypes.TaskApproved && item.Recipient == fixture.Author.Id);
        var inbox = CompletionInbox(db, fixture.Author.Id);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(
            (await inbox.GetNotifications("active", null, null)).Result).Value);
        Assert.True(page.Items.Single(item => item.Type == UserNotificationTypes.TaskApproved).ForcedModalEnabled);
        Assert.False(page.Items.Single(item => item.Type == UserNotificationTypes.TaskCompleted).ForcedModalEnabled);
    }

    [Fact]
    public async Task TaskCompletion_ApprovalUnknownExternalAttemptRemainsQueued()
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.ApprovalEventType);
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = TaskCompletionNotificationPolicy.ApprovalDecisionEventKey(fixture.Entry.EventKey),
            RecipientUserId = fixture.Author.Id,
            RecipientAddress = fixture.Author.Email,
            Channel = NotificationChannels.Email,
            Status = NotificationDeliveryStatus.Pending
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new RecordingRealtimeNotificationPublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher);

        Assert.False(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Null((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Empty(await db.UserNotifications.ToListAsync());
        Assert.Empty(publisher.Notifications);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("max")]
    public async Task TaskCompletion_ApprovalSavesEveryInboxBeforeFirstUnknownRemoteAttempt(string channel)
    {
        await using var db = await CreateContextAsync();
        var fixture = await SeedCompletionEventAsync(db, TaskCompletionNotificationPolicy.ApprovalEventType);
        var publisher = new RecordingRealtimeNotificationPublisher();
        var push = new CompletionPendingPush(db);
        var max = new CompletionPendingMax(db);
        if (channel == "max")
        {
            foreach (var userId in new[] { fixture.Author.Id, fixture.Assignee.Id, fixture.Observer.Id })
                db.MaxRecipientLinks.Add(new MaxRecipientLink
                {
                    UserId = userId,
                    MaxUserId = 7000 + userId,
                    Status = MaxRecipientLinkStatuses.Verified,
                    LinkTokenHash = Guid.NewGuid().ToString("N"),
                    VerifiedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), publisher,
            webPushNotificationService: channel == "push" ? push : null,
            maxService: channel == "max" ? new ReadyMaxNotificationService() : null,
            maxOutboxService: channel == "max" ? max : null,
            configurationValues: new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://crm.example.test/" });
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);

        Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));

        Assert.Equal(3, await db.UserNotifications.CountAsync(item => item.Type == UserNotificationTypes.TaskApproved));
        Assert.Equal(1, channel == "push" ? push.Attempts : max.Attempts);
        db.ChangeTracker.Clear();
        await ExpireCompletionLeaseAsync(db, fixture.Entry.Id);
        if (channel == "push")
        {
            Assert.Single(await db.NotificationDeliveries.Where(item => item.Status == NotificationDeliveryStatus.Pending).ToListAsync());
            Assert.False(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
            Assert.Equal(1, push.Attempts);
            Assert.Null((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
        }
        else
        {
            Assert.Empty(await db.NotificationDeliveries.ToListAsync());
            Assert.Single(await db.MaxOutboxMessages.Where(item => item.Status == MaxOutboxStatuses.Processing).ToListAsync());
            Assert.True(await processor.ProcessAsync(fixture.Entry.Id, CancellationToken.None));
            Assert.Equal(4, max.Attempts);
            Assert.Equal(4, await db.MaxOutboxMessages.CountAsync());
            Assert.Single(await db.MaxOutboxMessages.Where(item => item.Status == MaxOutboxStatuses.Processing).ToListAsync());
        }
    }

    private static NotificationInboxController CompletionInbox(PaymentContext db, int userId)
        => new(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "CompletionTest"))
                }
            }
        };

    private static async Task ExpireCompletionLeaseAsync(PaymentContext db, long eventId)
    {
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == eventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
    }

    private static async Task<CompletionFixture> SeedCompletionEventAsync(PaymentContext db, string eventType = TaskCompletionNotificationPolicy.OrdinaryEventType)
    {
        var author = await CreateUserAsync(db, "completion-author@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = author.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var assignee = CompletionUser(author.RoleId, "completion-assignee@example.test");
        var reviewer = CompletionUser(author.RoleId, "completion-reviewer@example.test");
        var observer = CompletionUser(author.RoleId, "completion-observer@example.test");
        db.Users.AddRange(assignee, reviewer, observer);
        await db.SaveChangesAsync();
        var participant = new TaskJournalParticipant { UserId = reviewer.Id, DisplayName = "Согласующий", Role = "approver", ApprovalStatus = "approved", Resolution = "Исходная рецензия", RespondedAt = DateTime.UtcNow };
        var task = new TaskJournalItem
        {
            ObjectName = "Синтетическое дело",
            Title = "Завершение задачи",
            AuthorUserId = author.Id,
            AssigneeUserId = assignee.Id,
            Status = "done",
            Result = "Исходный результат",
            DueAt = DateTime.UtcNow.AddHours(1),
            ChannelsJson = "[\"inApp\"]",
            Participants = [participant, new TaskJournalParticipant { UserId = observer.Id, DisplayName = "Наблюдатель", Role = "observer" }]
        };
        var entry = new TaskStatusNotificationOutbox
        {
            TaskJournalItem = task,
            EventKey = "task:completion:" + Guid.NewGuid().ToString("N"),
            EventType = eventType,
            StatusTransitionToken = task.StatusTransitionToken,
            ActorUserId = reviewer.Id,
            ActorName = "Согласующий",
            ApprovalParticipant = eventType == TaskCompletionNotificationPolicy.ApprovalEventType ? participant : null,
            CreatedAt = DateTime.UtcNow
        };
        db.TaskStatusNotificationOutboxes.Add(entry);
        await db.SaveChangesAsync();
        if (eventType == TaskCompletionNotificationPolicy.ApprovalEventType)
        {
            db.TaskJournalComments.Add(new TaskJournalComment
            {
                TaskJournalItemId = task.Id,
                AuthorUserId = reviewer.Id,
                AuthorName = "Согласующий",
                Kind = "system",
                Text = "Согласовано и завершено",
                MetadataJson = JsonSerializer.Serialize(new
                {
                    kind = "approvalDecision",
                    notificationEventKey = entry.EventKey,
                    actorUserId = reviewer.Id,
                    participantId = participant.Id,
                    approverUserId = reviewer.Id,
                    approverName = participant.DisplayName,
                    decision = "approved",
                    resolution = participant.Resolution,
                    taskResult = task.Result,
                    resultingStatus = "done",
                    respondedAt = participant.RespondedAt
                })
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        return new CompletionFixture(entry, author, assignee, reviewer, observer);
    }

    private static User CompletionUser(int roleId, string email)
        => new() { RoleId = roleId, Email = email, FullName = email, PasswordHash = "test", IsActive = true, IsApproved = true };

    private sealed record CompletionFixture(TaskStatusNotificationOutbox Entry, User Author, User Assignee, User Reviewer, User Observer);

    private sealed class CompletionRetryPublisher(int failOnAttempt) : IRealtimeNotificationPublisher
    {
        private int attempts;
        public List<(string Type, int Recipient, bool Toast)> Delivered { get; } = [];
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            attempts++;
            if (attempts == failOnAttempt) throw new IOException("Synthetic completion transport failure.");
            Delivered.Add((notification.Type, notification.RecipientUserId, toastEnabled));
            return Task.CompletedTask;
        }
    }

    private static async Task PersistUnknownCompletionDeliveryAsync(PaymentContext db, string eventKey, int? recipientId, string channel)
    {
        Assert.Equal(3, await db.UserNotifications.CountAsync(item => item.Type == UserNotificationTypes.TaskApproved));
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = eventKey,
            RecipientUserId = recipientId,
            RecipientAddress = "synthetic.local",
            Channel = channel,
            Status = NotificationDeliveryStatus.Pending
        });
        await db.SaveChangesAsync();
        throw new IOException("Synthetic interruption after persisting an unknown remote attempt.");
    }

    private sealed class CompletionPendingPush(PaymentContext db) : IWebPushNotificationService
    {
        public int Attempts { get; private set; }
        public WebPushPublicKeyStatus GetPublicKeyStatus() => new(true, true, "local-key", true, true, true);
        public async Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default)
        {
            Attempts++;
            await PersistUnknownCompletionDeliveryAsync(db, notification.EventKey, notification.RecipientUserId, NotificationChannels.WebPush);
        }
        public Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<NotificationDelivery?>(null);
    }

    private sealed class CompletionPendingMax(PaymentContext db) : IMaxOutboxService
    {
        public int Attempts { get; private set; }
        public async Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
        {
            var eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.EventKey)));
            var existing = await db.MaxOutboxMessages.SingleOrDefaultAsync(
                item => item.EventKey == eventKey && item.RecipientUserId == request.RecipientUserId, ct);
            if (existing is not null) return existing;
            Attempts++;
            Assert.Equal(3, await db.UserNotifications.CountAsync(item => item.Type == UserNotificationTypes.TaskApproved, ct));
            var linkId = await db.MaxRecipientLinks.Where(item => item.UserId == request.RecipientUserId).Select(item => item.Id).SingleAsync(ct);
            var queued = new MaxOutboxMessage
            {
                EventKey = eventKey,
                RecipientUserId = request.RecipientUserId,
                RecipientLinkId = linkId,
                Scenario = request.Scenario,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Status = Attempts == 1 ? MaxOutboxStatuses.Processing : MaxOutboxStatuses.Queued
            };
            db.MaxOutboxMessages.Add(queued);
            await db.SaveChangesAsync(ct);
            if (Attempts == 1) throw new IOException("Synthetic interruption while the first MAX attempt is processing.");
            return queued;
        }
        public Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct) => Task.FromResult(false);
    }
}

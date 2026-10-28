using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Hubs;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class NotificationServiceTests
{
    [Fact]
    public async Task StatusDispatch_RealSignalRFailureKeepsEventUntilRecovered()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "real-hub-author@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = author.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "done", ChannelsJson = "[\"inApp\"]" };
        var entry = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:durable:real-hub", EventType = "completion", StatusTransitionToken = task.StatusTransitionToken, ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.Add(entry);
        await db.SaveChangesAsync();
        var id = entry.Id;
        db.ChangeTracker.Clear();
        var hub = new FailingHubContext();
        var realtime = new SignalRRealtimeNotificationPublisher(hub, NullLogger<SignalRRealtimeNotificationPublisher>.Instance);
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:durable:real-hub").ToListAsync());
        Assert.Null((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == id)).CompletedAt);
        db.ChangeTracker.Clear();
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        hub.ClientsRecorder.Proxy.Fail = false;
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:durable:real-hub").ToListAsync());
        Assert.Equal(1, hub.ClientsRecorder.Proxy.Sent);
    }

    private sealed class FailingHubContext : IHubContext<NotificationsHub>
    {
        public FailingHubClients ClientsRecorder { get; } = new();
        public IHubClients Clients => ClientsRecorder;
        public IGroupManager Groups { get; } = new NoopGroups();
    }
    private sealed class FailingHubClients : IHubClients
    {
        public FailingHubProxy Proxy { get; } = new();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }
    private sealed class FailingHubProxy : IClientProxy
    {
        public bool Fail { get; set; } = true;
        public int Sent { get; private set; }
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Synthetic SignalR transport failure.");
            Sent++;
            return Task.CompletedTask;
        }
    }
    private sealed class NoopGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailOnceRealtimePublisher : IRealtimeNotificationPublisher
    {
        public int Attempts { get; private set; }
        public int Delivered { get; private set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new IOException("Synthetic realtime failure before delivery.");
            Delivered++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StatusDispatch_RecoversApprovalForAllRecipientsAfterPartialFailure()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "approval-retry-author@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = author.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var assignee = new User { Email = "approval-retry-assignee@example.test", FullName = "Исполнитель", PasswordHash = "test", RoleId = author.RoleId, IsActive = true, IsApproved = true };
        var approver = new User { Email = "approval-retry-reviewer@example.test", FullName = "Согласующий", PasswordHash = "test", RoleId = author.RoleId, IsActive = true, IsApproved = true };
        db.Users.AddRange(assignee, approver);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, AssigneeUserId = assignee.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "onApproval", ChannelsJson = "[\"inApp\"]", Participants = [new TaskJournalParticipant { UserId = approver.Id, Role = "approver", ApprovalStatus = "pending" }] };
        var entry = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:durable:approval-realtime", EventType = "approval", StatusTransitionToken = task.StatusTransitionToken, ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.Add(entry);
        await db.SaveChangesAsync();
        var id = entry.Id;
        db.ChangeTracker.Clear();
        var realtime = new FailOnceRealtimePublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:durable:approval-realtime").ToListAsync());
        db.ChangeTracker.Clear();
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(2, await db.UserNotifications.CountAsync(n => n.EventKey == "task:durable:approval-realtime"));
        Assert.Equal(2, realtime.Delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusDispatch_RecoversRealtimeAfterInboxWasSaved(bool readBeforeRetry)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "durable-realtime-author@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = author.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "done", ChannelsJson = "[\"inApp\"]" };
        var entry = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:durable:realtime", EventType = "completion", StatusTransitionToken = task.StatusTransitionToken, ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.Add(entry);
        await db.SaveChangesAsync();
        var id = entry.Id;
        db.ChangeTracker.Clear();
        var realtime = new FailOnceRealtimePublisher();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService(), realtime);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, await db.UserNotifications.CountAsync(n => n.EventKey == "task:durable:realtime"));
        db.ChangeTracker.Clear();
        if (readBeforeRetry)
        {
            var saved = await db.UserNotifications.SingleAsync(n => n.EventKey == "task:durable:realtime");
            saved.IsRead = true;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, await db.UserNotifications.CountAsync(n => n.EventKey == "task:durable:realtime"));
        Assert.Equal(readBeforeRetry ? 0 : 1, realtime.Delivered);
    }

    private sealed class StatusDeliveryProbe : IDurableTaskStatusNotificationService
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default) => Deliver();
        public Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default) => Deliver();
        private Task<IReadOnlyList<NotificationDelivery>> Deliver()
        {
            Calls++;
            IReadOnlyList<NotificationDelivery> result = [new NotificationDelivery { Status = Fail ? NotificationDeliveryStatus.Failed : NotificationDeliveryStatus.Sent }];
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task StatusDispatch_RetainsFailedDeliveryAndRetriesAfterLease()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "dispatch-author@example.test");
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "done" };
        var item = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:dispatch:retry", EventType = "completion", StatusTransitionToken = task.StatusTransitionToken, ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.Add(item);
        await db.SaveChangesAsync();
        var id = item.Id;
        db.ChangeTracker.Clear();
        var probe = new StatusDeliveryProbe { Fail = true };
        var processor = new TaskStatusNotificationDispatchProcessor(db, probe);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
        await db.TaskStatusNotificationOutboxes.Where(entry => entry.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.LeaseUntil, (DateTime?)DateTime.UtcNow.AddSeconds(-1)));
        probe.Fail = false;
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(2, probe.Calls);
        Assert.NotNull((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(entry => entry.Id == id)).CompletedAt);
    }

    [Fact]
    public async Task StatusDispatch_DiscardsSupersededOrInvalidStateWithoutSending()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "dispatch-stale@example.test");
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "done" };
        var first = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:dispatch:stale", EventType = "approval", ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        var second = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:dispatch:done", EventType = "completion", StatusTransitionToken = task.StatusTransitionToken, ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.AddRange(first, second);
        await db.SaveChangesAsync();
        var firstId = first.Id;
        var secondId = second.Id;
        db.ChangeTracker.Clear();
        var probe = new StatusDeliveryProbe();
        var processor = new TaskStatusNotificationDispatchProcessor(db, probe);
        Assert.True(await processor.ProcessAsync(firstId, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
        Assert.True(await processor.ProcessAsync(secondId, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task TaskStatusOutbox_RollsBackWithTaskState()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "outbox-owner@example.test");
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "inProgress" };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            task.Status = "done";
            db.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
            {
                TaskJournalItemId = task.Id, EventKey = "task:outbox:rollback", EventType = "completion",
                ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal("inProgress", (await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id)).Status);
        Assert.Empty(await db.TaskStatusNotificationOutboxes.ToListAsync());
    }

    [Fact]
    public async Task TaskStatusOutbox_RejectsDuplicateEventKeys()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "outbox-unique@example.test");
        var task = new TaskJournalItem { ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1), Status = "done" };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        TaskStatusNotificationOutbox MakeEntry() => new()
        {
            TaskJournalItemId = task.Id, EventKey = "task:outbox:unique", EventType = "completion",
            ActorUserId = author.Id, ActorName = "Автор", CreatedAt = DateTime.UtcNow,
        };
        db.TaskStatusNotificationOutboxes.Add(MakeEntry());
        await db.SaveChangesAsync();
        db.TaskStatusNotificationOutboxes.Add(MakeEntry());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DurableApproval_RequiresCurrentTaskRoleAndMembership()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "durable-owner@example.test");
        var role = new Role { Name = "durable-reviewer", Description = "reviewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var permission = new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Tasks, CanView = false, ViewScope = DataVisibilityScope.Own };
        var reviewer = new User { Email = "durable-reviewer@example.test", FullName = "Согласующий", PasswordHash = "test", RoleId = role.Id, IsActive = true, IsApproved = true };
        db.RolePermissions.Add(permission);
        db.Users.Add(reviewer);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "Дело", Title = "Задача", AuthorUserId = author.Id, DueAt = DateTime.UtcNow.AddHours(1),
            ChannelsJson = "[\"inApp\"]", Status = "onApproval",
            Participants = [new TaskJournalParticipant { UserId = reviewer.Id, Role = "approver", ApprovalStatus = "pending" }],
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        await service.NotifyApprovalForEventAsync(task, "Автор", author.Id, "task:role-denied");
        Assert.Empty(await db.UserNotifications.Where(item => item.EventKey == "task:role-denied").ToListAsync());
        permission.CanView = true;
        await db.SaveChangesAsync();
        await service.NotifyApprovalForEventAsync(task, "Автор", author.Id, "task:role-allowed");
        Assert.Single(await db.UserNotifications.Where(item => item.EventKey == "task:role-allowed" && item.RecipientUserId == reviewer.Id).ToListAsync());
        await db.TaskJournalParticipants.Where(item => item.UserId == reviewer.Id && item.TaskJournalItemId == task.Id).ExecuteDeleteAsync();
        await service.NotifyApprovalForEventAsync(task, "Автор", author.Id, "task:removed-reviewer");
        Assert.Empty(await db.UserNotifications.Where(item => item.EventKey == "task:removed-reviewer").ToListAsync());
    }

    [Fact]
    public async Task DurableTaskEvents_DoNotNotifyInactiveOrUnapprovedRecipients()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "durable-denied-author@example.test");
        var assignee = new User { Email = "durable-denied-assignee@example.test", FullName = "Исполнитель", PasswordHash = "test", RoleId = author.RoleId, IsActive = false, IsApproved = true };
        var approver = new User { Email = "durable-denied-approver@example.test", FullName = "Согласующий", PasswordHash = "test", RoleId = author.RoleId, IsActive = true, IsApproved = false };
        author.IsApproved = false;
        db.Users.AddRange(assignee, approver);
        await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "Синтетическое дело", Title = "Синтетическая задача", AuthorUserId = author.Id,
            AssigneeUserId = assignee.Id, DueAt = DateTime.UtcNow.AddHours(1), ChannelsJson = "[\"inApp\"]",
            Status = "onApproval", Participants = [new TaskJournalParticipant { UserId = approver.Id, Role = "approver", ApprovalStatus = "pending" }],
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        await service.NotifyApprovalForEventAsync(task, "Автор", 999999, "task:denied:approval");
        task.Status = "done";
        await db.SaveChangesAsync();
        await service.NotifyCompletionForEventAsync(task, "task:denied:completion");
        Assert.Empty(await db.UserNotifications.Where(item => item.EventKey.StartsWith("task:denied:")).ToListAsync());
    }

    [Fact]
    public async Task DurableTaskEvents_DeduplicateRetriesAndKeepDistinctCycles()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "durable-event-author@example.test");
        db.RolePermissions.Add(new RolePermission { RoleId = author.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
        var assignee = new User { Email = "durable-event-assignee@example.test", FullName = "Исполнитель", PasswordHash = "test", RoleId = author.RoleId, IsActive = true, IsApproved = true };
        var approver = new User { Email = "durable-event-approver@example.test", FullName = "Согласующий", PasswordHash = "test", RoleId = author.RoleId, IsActive = true, IsApproved = true };
        db.Users.AddRange(assignee, approver); await db.SaveChangesAsync();
        var task = new TaskJournalItem
        {
            ObjectName = "Синтетическое дело", Title = "Синтетическая задача", AuthorUserId = author.Id,
            AssigneeUserId = assignee.Id, DueAt = DateTime.UtcNow.AddHours(1), ChannelsJson = "[\"inApp\"]",
            Status = "onApproval", Participants = [new TaskJournalParticipant { UserId = approver.Id, Role = "approver", ApprovalStatus = "pending" }],
        };
        db.TaskJournalItems.Add(task); await db.SaveChangesAsync();
        var service = CreateService(db, new RecordingEmailService(), new RecordingTelegramService());
        const string first = "task:synthetic:approval:first";
        const string second = "task:synthetic:approval:second";
        await service.NotifyApprovalForEventAsync(task, author.FullName, author.Id, first);
        await service.NotifyApprovalForEventAsync(task, author.FullName, author.Id, first);
        Assert.Equal(2, await db.UserNotifications.CountAsync(n => n.EventKey == first));
        await service.NotifyApprovalForEventAsync(task, author.FullName, author.Id, second);
        Assert.Equal(2, await db.UserNotifications.CountAsync(n => n.EventKey == second));
        task.Status = "done"; await db.SaveChangesAsync();
        const string completion = "task:synthetic:completion:first";
        await service.NotifyCompletionForEventAsync(task, completion);
        await service.NotifyCompletionForEventAsync(task, completion);
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == completion).ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.NotifyApprovalForEventAsync(task, author.FullName, author.Id, " "));
    }
}

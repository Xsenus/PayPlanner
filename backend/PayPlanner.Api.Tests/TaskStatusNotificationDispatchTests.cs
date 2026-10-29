using Microsoft.Data.Sqlite;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskStatusNotificationDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedTaskEvents_DoNotHideLiveEventsBehindTheBatchLimit(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var firstId = await harness.SeedAsync();
        long liveId;
        int deletedTaskId;
        await using (var setup = harness.Context())
        {
            var first = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == firstId);
            var deletedTask = first.TaskJournalItem;
            deletedTaskId = deletedTask.Id;
            for (var index = 1; index < 50; index++)
                setup.TaskStatusNotificationOutboxes.Add(new TaskStatusNotificationOutbox
                {
                    TaskJournalItemId = deletedTask.Id,
                    EventKey = $"task:deleted:{index}",
                    EventType = "completion",
                    StatusTransitionToken = deletedTask.StatusTransitionToken,
                    ActorName = "Автор",
                    CreatedAt = DateTime.UtcNow,
                });
            await setup.SaveChangesAsync();
            var liveTask = new TaskJournalItem { Title = "Действующая задача", ObjectName = "Дело", AuthorUserId = deletedTask.AuthorUserId, DueAt = DateTime.UtcNow.AddHours(1), Status = "done" };
            var live = new TaskStatusNotificationOutbox
            {
                TaskJournalItem = liveTask,
                EventKey = "task:live:after-deleted",
                EventType = "completion",
                StatusTransitionToken = liveTask.StatusTransitionToken,
                ActorName = "Автор",
                CreatedAt = DateTime.UtcNow,
            };
            setup.TaskStatusNotificationOutboxes.Add(live);
            setup.ChangeTracker.CascadeDeleteTiming = Microsoft.EntityFrameworkCore.ChangeTracking.CascadeTiming.OnSaveChanges;
            setup.TaskJournalItems.Remove(deletedTask);
            await setup.SaveChangesAsync();
            liveId = live.Id;
        }
        await using var db = harness.Context();
        Assert.True(await db.TaskJournalItems.IgnoreQueryFilters().Where(task => task.Id == deletedTaskId).Select(task => EF.Property<bool>(task, "IsDeleted")).SingleAsync());
        Assert.Equal(51, await db.TaskStatusNotificationOutboxes.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await db.TaskStatusNotificationOutboxes.CountAsync());
        Assert.Equal(1, await db.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).CountAsync());
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.Equal(1, await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(1, probe.Calls);
        Assert.NotNull((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == liveId)).CompletedAt);
        Assert.Equal(50, await db.TaskStatusNotificationOutboxes.IgnoreQueryFilters().CountAsync(item => item.TaskJournalItemId == deletedTaskId && item.Attempts == 0 && item.LeaseToken == null && item.CompletedAt == null));
        Assert.Equal(1, await db.TaskJournalItems.IgnoreQueryFilters().Where(task => task.Id == deletedTaskId).ExecuteUpdateAsync(setters => setters.SetProperty(task => EF.Property<bool>(task, "IsDeleted"), false)));
        Assert.Equal(51, await db.TaskStatusNotificationOutboxes.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApprovalEventWithoutBoundParticipant_IsSafelyRetired(bool postgres, bool deleteParticipant)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int participantId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            var participant = new TaskJournalParticipant { UserId = task.AuthorUserId, Role = "approver", DisplayName = "Автор", ApprovalStatus = "pending" };
            task.Participants.Add(participant);
            await setup.SaveChangesAsync();
            entry.EventType = "approval";
            entry.StatusTransitionToken = task.StatusTransitionToken;
            if (deleteParticipant) entry.ApprovalParticipant = participant;
            await setup.SaveChangesAsync();
            participantId = participant.Id;
        }
        if (deleteParticipant)
        {
            await using var change = harness.Context();
            Assert.Equal(1, await change.TaskJournalParticipants.Where(item => item.Id == participantId).ExecuteDeleteAsync());
        }
        await using var db = harness.Context();
        var stored = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Null(stored.ApprovalParticipantId);
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(id, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
        Assert.NotNull((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == id)).CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingApprovalEvent_DoesNotMoveToDifferentApproverWithoutNewEvent(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int taskId;
        int firstId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var admin = await setup.Roles.SingleAsync(role => role.Name == "admin");
            var first = new User { Email = "stage-event-first@example.test", FullName = "Первый", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var second = new User { Email = "stage-event-second@example.test", FullName = "Второй", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            task.Participants.Add(new TaskJournalParticipant { User = first, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
            task.Participants.Add(new TaskJournalParticipant { User = second, Role = "approver", DisplayName = second.FullName, Email = second.Email, ApprovalStatus = "pending", SortOrder = 2 });
            await setup.SaveChangesAsync();
            var approvalEvent = new TaskStatusNotificationOutbox { TaskJournalItem = task, ApprovalParticipant = task.Participants.Single(item => item.UserId == first.Id), EventKey = "task:bound-stage", EventType = "approval", StatusTransitionToken = task.StatusTransitionToken, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
            setup.TaskStatusNotificationOutboxes.Add(approvalEvent);
            await setup.SaveChangesAsync();
            id = approvalEvent.Id;
            taskId = task.Id;
            firstId = first.Id;
        }
        await using (var change = harness.Context())
            Assert.Equal(1, await change.TaskJournalParticipants.Where(item => item.TaskJournalItemId == taskId && item.UserId == firstId && item.Role == "approver").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ApprovalStatus, "approved")));
        await using var db = harness.Context();
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(id, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalStageChangeAfterNotificationCommit_PreventsRealtimeDelivery(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var admin = await setup.Roles.SingleAsync(role => role.Name == "admin");
            var first = new User { Email = "commit-stage-first@example.test", FullName = "Первый", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var next = new User { Email = "commit-stage-next@example.test", FullName = "Второй", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            task.ChannelsJson = "[\"inApp\"]";
            task.Participants.Add(new TaskJournalParticipant { User = first, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
            task.Participants.Add(new TaskJournalParticipant { User = next, Role = "approver", DisplayName = next.FullName, Email = next.Email, ApprovalStatus = "pending", SortOrder = 2 });
            await setup.SaveChangesAsync();
            entry.EventType = "approval";
            entry.ApprovalParticipant = task.Participants.Single(item => item.UserId == first.Id);
            entry.StatusTransitionToken = task.StatusTransitionToken;
            entry.ActorUserId = task.AuthorUserId!.Value;
            await setup.SaveChangesAsync();
        }
        var interceptor = new AdvanceApproverAfterNotificationCommit(harness);
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        Assert.Equal(1, interceptor.Affected);
        Assert.Single(await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(0, publisher.Delivered);
    }

    private sealed class AdvanceApproverAfterNotificationCommit(Harness harness) : DbTransactionInterceptor
    {
        public bool Triggered { get; private set; }
        public int Affected { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var entry = eventData.Context!.ChangeTracker.Entries<UserNotification>().FirstOrDefault(item => item.Entity.EventKey == "task:dispatch:concurrency");
            if (Triggered || entry is null) return;
            Triggered = true;
            await using var separate = harness.Context();
            Affected = await separate.TaskJournalParticipants.Where(item => item.TaskJournalItemId == entry.Entity.TaskJournalItemId && item.UserId == entry.Entity.RecipientUserId && item.Role == "approver")
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ApprovalStatus, "approved"), cancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleApprovalSnapshot_DoesNotNotifyPreviousApprover(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int firstId;
        int taskId;
        int actorId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var admin = await setup.Roles.SingleAsync(role => role.Name == "admin");
            var first = new User { Email = "stale-first@example.test", FullName = "Первый", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var next = new User { Email = "stale-next@example.test", FullName = "Второй", PasswordHash = "test", Role = admin, IsActive = true, IsApproved = true };
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            task.ChannelsJson = "[\"inApp\"]";
            task.Participants.Add(new TaskJournalParticipant { User = first, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
            task.Participants.Add(new TaskJournalParticipant { User = next, Role = "approver", DisplayName = next.FullName, Email = next.Email, ApprovalStatus = "pending", SortOrder = 2 });
            await setup.SaveChangesAsync();
            firstId = first.Id;
            taskId = task.Id;
            actorId = task.AuthorUserId!.Value;
        }
        await using var db = harness.Context();
        var staleTask = await db.TaskJournalItems.AsNoTracking().Include(task => task.Participants).ThenInclude(participant => participant.User).SingleAsync(task => task.Id == taskId);
        await using (var changed = harness.Context())
            Assert.Equal(1, await changed.TaskJournalParticipants.Where(item => item.TaskJournalItemId == taskId && item.UserId == firstId && item.Role == "approver").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ApprovalStatus, "approved")));
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await service.NotifyApprovalForEventAsync(staleTask, "Автор", actorId, "task:stale-approval", CancellationToken.None);
        Assert.Empty(await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:stale-approval").ToListAsync());
        Assert.Equal(0, publisher.Delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalEvent_NotifiesCurrentApproverAndNotPreviousApprover(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int previousId;
        int currentId;
        int authorId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var adminRole = await setup.Roles.SingleAsync(role => role.Name == "admin");
            var first = new User { Email = "old-reviewer@example.test", FullName = "Первый", PasswordHash = "test", Role = adminRole, IsActive = true, IsApproved = true };
            var next = new User { Email = "next-reviewer@example.test", FullName = "Второй", PasswordHash = "test", Role = first.Role, IsActive = true, IsApproved = true };
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            task.ChannelsJson = "[\"inApp\"]";
            task.Participants.Add(new TaskJournalParticipant { User = first, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "approved", SortOrder = 1 });
            task.Participants.Add(new TaskJournalParticipant { User = next, Role = "approver", DisplayName = next.FullName, Email = next.Email, ApprovalStatus = "pending", SortOrder = 2 });
            await setup.SaveChangesAsync();
            entry.EventType = "approval";
            entry.ApprovalParticipant = task.Participants.Single(item => item.UserId == next.Id);
            entry.StatusTransitionToken = task.StatusTransitionToken;
            entry.ActorUserId = first.Id;
            await setup.SaveChangesAsync();
            previousId = first.Id;
            currentId = next.Id;
            authorId = task.AuthorUserId!.Value;
        }
        await using var db = harness.Context();
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None));
        var recipients = await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:dispatch:concurrency").Select(item => item.RecipientUserId).ToListAsync();
        Assert.Equal(new[] { authorId, currentId }.Order(), recipients.Order());
        Assert.DoesNotContain(previousId, recipients);
        Assert.Equal(2, publisher.Delivered);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequestAccessRevocation_IsRespectedBeforeOrSerializedDuringNotificationSave(bool postgres, bool revokeBefore)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int requestId;
        int otherId;
        int recipientId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var recipient = await setup.Users.Include(user => user.Role).SingleAsync(user => user.Id == entry.TaskJournalItem.AuthorUserId);
            recipient.Role!.Name = "limited-request-reader";
            setup.RolePermissions.AddRange(
                new RolePermission { RoleId = recipient.RoleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own },
                new RolePermission { RoleId = recipient.RoleId, Section = RolePermissionCatalog.Requests, CanView = true, ViewScope = DataVisibilityScope.Own });
            var other = new User { Email = "request-owner@example.test", FullName = "Другой", PasswordHash = "test", Role = new Role { Name = "request-owner" }, IsActive = true, IsApproved = true };
            var status = new SalesRequestStatus { Key = "status-privacy", Title = "Тест" };
            setup.AddRange(other, status);
            await setup.SaveChangesAsync();
            var request = new SalesRequest { Summary = "Закрытая заявка", StatusId = status.Id, ResponsibleUserId = recipient.Id, CreatedByUserId = other.Id };
            setup.SalesRequests.Add(request);
            await setup.SaveChangesAsync();
            entry.TaskJournalItem.SalesRequestId = request.Id;
            entry.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
            await setup.SaveChangesAsync();
            requestId = request.Id;
            otherId = other.Id;
            recipientId = recipient.Id;
        }
        if (revokeBefore)
        {
            await using var revoke = harness.Context();
            Assert.Equal(1, await revoke.SalesRequests.Where(item => item.Id == requestId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ResponsibleUserId, otherId)));
        }
        var interceptor = new RevokeAtNotificationSave(harness, "request");
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        var notifications = await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:dispatch:concurrency").ToListAsync();
        if (revokeBefore)
        {
            Assert.Empty(notifications);
            Assert.Equal(0, publisher.Delivered);
            Assert.False(interceptor.Triggered);
        }
        else
        {
            Assert.True(interceptor.Triggered);
            Assert.True(interceptor.Blocked, $"transaction={interceptor.HadTransaction}, affected={interceptor.Affected}, error={interceptor.UnexpectedError}");
            Assert.Equal(recipientId, Assert.Single(notifications).RecipientUserId);
            Assert.Equal(1, publisher.Delivered);
            await using var after = harness.Context();
            Assert.Equal(1, await after.SalesRequests.Where(item => item.Id == requestId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ResponsibleUserId, otherId)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationAfterNotificationCommit_PreventsRealtimeDelivery(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            entry.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
            await setup.SaveChangesAsync();
        }
        var interceptor = new DeactivateAfterNotificationCommit(harness);
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        Assert.Equal(1, interceptor.Affected);
        Assert.Single(await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(0, publisher.Delivered);
    }

    private sealed class DeactivateAfterNotificationCommit(Harness harness) : DbTransactionInterceptor
    {
        public bool Triggered { get; private set; }
        public int Affected { get; private set; }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            var entry = eventData.Context!.ChangeTracker.Entries<UserNotification>().FirstOrDefault(item => item.Entity.EventKey == "task:dispatch:concurrency");
            if (Triggered || entry is null) return;
            Triggered = true;
            await using var separate = harness.Context();
            Affected = await separate.Users.Where(user => user.Id == entry.Entity.RecipientUserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false), cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApproverRemoval_IsRespectedBeforeOrSerializedDuringNotificationSave(bool postgres, bool removeBefore)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int taskId;
        int approverId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            var role = new Role { Name = "limited-approver" };
            var approver = new User { Email = "status-approver@example.test", FullName = "Согласующий", PasswordHash = "test", Role = role, IsActive = true, IsApproved = true };
            setup.RolePermissions.Add(new RolePermission { Role = role, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
            var task = entry.TaskJournalItem;
            task.Status = "onApproval";
            task.ChannelsJson = "[\"inApp\"]";
            task.Participants.Add(new TaskJournalParticipant { User = approver, Role = "approver", DisplayName = approver.FullName, Email = approver.Email, ApprovalStatus = "pending" });
            await setup.SaveChangesAsync();
            entry.EventType = "approval";
            entry.ApprovalParticipant = task.Participants.Single(item => item.UserId == approver.Id);
            entry.StatusTransitionToken = task.StatusTransitionToken;
            entry.ActorUserId = task.AuthorUserId!.Value;
            await setup.SaveChangesAsync();
            taskId = task.Id;
            approverId = approver.Id;
        }
        if (removeBefore)
        {
            await using var remove = harness.Context();
            Assert.Equal(1, await remove.TaskJournalParticipants.Where(item => item.TaskJournalItemId == taskId && item.UserId == approverId).ExecuteDeleteAsync());
        }
        var interceptor = new RevokeAtNotificationSave(harness, "participant");
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        var notifications = await db.UserNotifications.AsNoTracking().Where(item => item.EventKey == "task:dispatch:concurrency").ToListAsync();
        if (removeBefore)
        {
            Assert.Empty(notifications);
            Assert.Equal(0, publisher.Delivered);
            Assert.False(interceptor.Triggered);
        }
        else
        {
            Assert.True(interceptor.Triggered);
            Assert.True(interceptor.Blocked, $"transaction={interceptor.HadTransaction}, affected={interceptor.Affected}, error={interceptor.UnexpectedError}");
            Assert.Equal(approverId, Assert.Single(notifications).RecipientUserId);
            Assert.Equal(1, publisher.Delivered);
            await using var after = harness.Context();
            Assert.Equal(1, await after.TaskJournalParticipants.Where(item => item.TaskJournalItemId == taskId && item.UserId == approverId).ExecuteDeleteAsync());
        }
    }

    [Theory]
    [InlineData(false, "role")]
    [InlineData(false, "task")]
    [InlineData(true, "role")]
    [InlineData(true, "task")]
    public async Task RoleOrStatusChangeDuringNotificationSave_IsSerialized(bool postgres, string operation)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        int taskId;
        int roleId;
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            entry.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
            taskId = entry.TaskJournalItemId;
            var user = await setup.Users.Include(item => item.Role).SingleAsync(item => item.Id == entry.TaskJournalItem.AuthorUserId);
            roleId = user.RoleId;
            user.Role!.Name = "task-reader";
            setup.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = RolePermissionCatalog.Tasks, CanView = true, ViewScope = DataVisibilityScope.Own });
            await setup.SaveChangesAsync();
        }
        var interceptor = new RevokeAtNotificationSave(harness, operation);
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        Assert.True(interceptor.Blocked, $"transaction={interceptor.HadTransaction}, affected={interceptor.Affected}, error={interceptor.UnexpectedError}");
        Assert.Single(await db.UserNotifications.AsNoTracking().Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(1, publisher.Delivered);
        await using var after = harness.Context();
        if (operation == "role")
            Assert.Equal(1, await after.RolePermissions.Where(item => item.RoleId == roleId && item.Section == RolePermissionCatalog.Tasks).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, false)));
        else
            Assert.Equal(1, await after.TaskJournalItems.Where(item => item.Id == taskId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "revision")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationDuringNotificationSave_WaitsForCommittedNotification(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using (var setup = harness.Context())
        {
            var entry = await setup.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
            entry.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
            await setup.SaveChangesAsync();
        }
        var interceptor = new RevokeAtNotificationSave(harness);
        await using var db = harness.Context(interceptor);
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        Assert.True(interceptor.Triggered);
        Assert.True(interceptor.Blocked, $"transaction={interceptor.HadTransaction}, affected={interceptor.Affected}, error={interceptor.UnexpectedError}");
        Assert.Single(await db.UserNotifications.AsNoTracking().Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(1, publisher.Delivered);
        await using var after = harness.Context();
        Assert.Equal(1, await after.Users.Where(user => user.Id == interceptor.UserId).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false)));
    }

    private sealed class RevokeAtNotificationSave(Harness harness, string operation = "user") : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }
        public bool Blocked { get; private set; }
        public bool HadTransaction { get; private set; }
        public int Affected { get; private set; } = -1;
        public string? UnexpectedError { get; private set; }
        public int UserId { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var notification = eventData.Context!.ChangeTracker.Entries<UserNotification>().FirstOrDefault(entry => entry.State == EntityState.Added);
            if (!Triggered && notification is not null)
            {
                Triggered = true;
                HadTransaction = eventData.Context.Database.CurrentTransaction is not null;
                UserId = notification.Entity.RecipientUserId;
                await using var separate = harness.Context();
                await separate.Database.OpenConnectionAsync(cancellationToken);
                if (separate.Database.GetDbConnection() is SqliteConnection sqlite) sqlite.DefaultTimeout = 1;
                if (separate.Database.IsNpgsql()) await separate.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms'", cancellationToken);
                try
                {
                    Affected = operation switch
                    {
                        "user" => await separate.Users.Where(user => user.Id == notification.Entity.RecipientUserId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false), cancellationToken),
                        "role" => await separate.RolePermissions.Where(permission => permission.Section == RolePermissionCatalog.Tasks
                                && separate.Users.Any(user => user.Id == notification.Entity.RecipientUserId && user.RoleId == permission.RoleId))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(permission => permission.CanView, false), cancellationToken),
                        "task" => await separate.TaskJournalItems.Where(task => task.Id == notification.Entity.TaskJournalItemId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, "revision"), cancellationToken),
                        "participant" => await separate.TaskJournalParticipants.Where(item => item.TaskJournalItemId == notification.Entity.TaskJournalItemId && item.UserId == notification.Entity.RecipientUserId && item.Role == "approver")
                            .ExecuteDeleteAsync(cancellationToken),
                        "request" => await separate.SalesRequests.Where(item => separate.TaskJournalItems.Any(task => task.Id == notification.Entity.TaskJournalItemId && task.SalesRequestId == item.Id))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ResponsibleUserId, item => item.CreatedByUserId), cancellationToken),
                        _ => throw new InvalidOperationException("Unexpected synthetic operation."),
                    };
                }
                catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteErrorCode is 5 or 6) { Blocked = true; }
                catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable) { Blocked = true; }
                catch (InvalidOperationException error) when (error.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.LockNotAvailable }) { Blocked = true; }
                catch (Exception error) { UnexpectedError = error.ToString(); }
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationBeforeNotificationSave_DoesNotDeliver(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var entry = await db.TaskStatusNotificationOutboxes.Include(item => item.TaskJournalItem).SingleAsync(item => item.Id == id);
        var userId = entry.TaskJournalItem.AuthorUserId!.Value;
        entry.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsActive, false));
        var publisher = new CountingRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        await new TaskStatusNotificationDispatchProcessor(db, service).ProcessAsync(id, CancellationToken.None);
        Assert.Empty(await db.UserNotifications.AsNoTracking().Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(0, publisher.Delivered);
    }

    private sealed class CountingRealtime : IRealtimeNotificationPublisher
    {
        public int Delivered { get; private set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default) { Delivered++; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealNotificationService_RetryRespectsDisabledToast(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var item = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        item.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        var authorId = item.TaskJournalItem.AuthorUserId!.Value;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new FailingOnceRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        db.ChangeTracker.Clear();
        db.UserSettings.Add(new UserSetting
        {
            UserId = authorId,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>> { [UserNotificationTypes.TaskCompleted] = [NotificationChannels.Toast] }))
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(entry => entry.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.LeaseUntil, (DateTime?)expired));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(1, publisher.Delivered);
        Assert.False(publisher.LastToast);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealNotificationService_RetryRespectsDisabledType(bool postgres, bool disableBeforeRetry)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var item = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        item.TaskJournalItem.ChannelsJson = "[\"inApp\"]";
        var authorId = item.TaskJournalItem.AuthorUserId!.Value;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var publisher = new FailingOnceRealtime();
        var service = new NotificationService(db, new ConfigurationBuilder().Build(), new ForbiddenEmail(), new ForbiddenTelegram(), NullLogger<NotificationService>.Instance, publisher);
        var processor = new TaskStatusNotificationDispatchProcessor(db, service);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        db.ChangeTracker.Clear();
        if (disableBeforeRetry)
        {
            db.UserSettings.Add(new UserSetting
            {
                UserId = authorId,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([UserNotificationTypes.TaskCompleted], new Dictionary<string, IReadOnlyList<string>>()))
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(entry => entry.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.LeaseUntil, (DateTime?)expired));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Single(await db.UserNotifications.Where(n => n.EventKey == "task:dispatch:concurrency").ToListAsync());
        Assert.Equal(disableBeforeRetry ? 0 : 1, publisher.Delivered);
    }

    private sealed class FailingOnceRealtime : IRealtimeNotificationPublisher
    {
        private int attempts;
        public int Delivered { get; private set; }
        public bool LastToast { get; private set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            if (++attempts == 1) throw new IOException("Synthetic realtime failure.");
            Delivered++;
            LastToast = toastEnabled;
            return Task.CompletedTask;
        }
    }

    private sealed class ForbiddenEmail : IEmailService
    {
        public Task SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false, CancellationToken cancellationToken = default, EmailSendSettings? settings = null) => throw new InvalidOperationException("External email forbidden in test.");
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException();
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => throw new InvalidOperationException();
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => throw new InvalidOperationException();
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => throw new InvalidOperationException();
    }

    private sealed class ForbiddenTelegram : ITelegramNotificationService
    {
        public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default) => throw new InvalidOperationException("External Telegram forbidden in test.");
        public Task<TelegramSendResult> SendMessageWithTokenAsync(long chatId, string text, string botToken, CancellationToken cancellationToken = default, TelegramBotConnectionSettings? connection = null) => throw new InvalidOperationException("External Telegram forbidden in test.");
        public Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApprovalBecomingOverdue_PreservesCurrentDeliveryCycle(bool postgres, bool reverse)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var item = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        item.TaskJournalItem.Status = reverse ? "overdueApproval" : "onApproval";
        var approver = new TaskJournalParticipant { UserId = item.TaskJournalItem.AuthorUserId, Role = "approver", DisplayName = "Автор", ApprovalStatus = "pending" };
        item.TaskJournalItem.Participants.Add(approver);
        await db.SaveChangesAsync();
        item.EventType = "approval";
        item.ApprovalParticipant = approver;
        item.StatusTransitionToken = item.TaskJournalItem.StatusTransitionToken;
        await db.SaveChangesAsync();
        item.TaskJournalItem.Status = reverse ? "onApproval" : "overdueApproval";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryTaskEdit_PreservesCurrentDeliveryCycle(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var item = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        var token = item.TaskJournalItem.StatusTransitionToken;
        item.TaskJournalItem.Title = "Исправленное название";
        await db.SaveChangesAsync();
        Assert.Equal(token, item.TaskJournalItem.StatusTransitionToken);
        db.ChangeTracker.Clear();
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewEvent_SnapshotsChangedStatusInSameSave(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var previous = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        var task = previous.TaskJournalItem;
        var oldToken = task.StatusTransitionToken;
        task.Status = "onApproval";
        var next = new TaskStatusNotificationOutbox { TaskJournalItemId = task.Id, EventKey = "task:dispatch:new-cycle", EventType = "approval", ActorName = "Автор", CreatedAt = DateTime.UtcNow };
        db.TaskStatusNotificationOutboxes.Add(next);
        await db.SaveChangesAsync();
        Assert.NotEqual(oldToken, task.StatusTransitionToken);
        Assert.Equal(task.StatusTransitionToken, next.StatusTransitionToken);
        db.ChangeTracker.Clear();
        var stored = await db.TaskStatusNotificationOutboxes.AsNoTracking().Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == next.Id);
        Assert.Equal(stored.TaskJournalItem.StatusTransitionToken, stored.StatusTransitionToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldApprovalCycle_DoesNotNotifyAfterReopenThroughAnotherRoute(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        var item = await db.TaskStatusNotificationOutboxes.Include(entry => entry.TaskJournalItem).SingleAsync(entry => entry.Id == id);
        item.EventType = "approval";
        item.TaskJournalItem.Status = "onApproval";
        await db.SaveChangesAsync();
        item.StatusTransitionToken = item.TaskJournalItem.StatusTransitionToken;
        await db.SaveChangesAsync();
        item.TaskJournalItem.Status = "revision";
        await db.SaveChangesAsync();
        item.TaskJournalItem.Status = "onApproval";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(id, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownExternalDeliveryOutcome_DoesNotSendAgain(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var db = harness.Context();
        db.NotificationDeliveries.Add(new NotificationDelivery { EventKey = "task:dispatch:concurrency", Channel = "email", Status = NotificationDeliveryStatus.Pending, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var probe = new BlockingNotifications();
        probe.Release.TrySetResult();
        var processor = new TaskStatusNotificationDispatchProcessor(db, probe);
        Assert.False(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(0, probe.Calls);
        Assert.Null((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == id)).CompletedAt);
        await db.NotificationDeliveries.Where(item => item.EventKey == "task:dispatch:concurrency").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, NotificationDeliveryStatus.Sent));
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        Assert.True(await processor.ProcessAsync(id, CancellationToken.None));
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingWorkers_OnlyOneClaimsEvent(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var id = await harness.SeedAsync();
        await using var firstDb = harness.Context();
        await using var secondDb = harness.Context();
        var probe = new BlockingNotifications();
        var first = new TaskStatusNotificationDispatchProcessor(firstDb, probe).ProcessAsync(id, CancellationToken.None);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try { Assert.False(await new TaskStatusNotificationDispatchProcessor(secondDb, probe).ProcessAsync(id, CancellationToken.None)); }
        finally { probe.Release.TrySetResult(); }
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, probe.Calls);
        Assert.NotNull((await secondDb.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == id)).CompletedAt);
    }

    private sealed class BlockingNotifications : IDurableTaskStatusNotificationService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default) => Deliver();
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default) => Deliver();
        private async Task<IReadOnlyList<NotificationDelivery>> Deliver()
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task;
            return [];
        }
    }

    private sealed class Harness(bool postgres, PostgreSqlModelTemplate? template, PostgreSqlModelTemplate.DatabaseLease? lease, SqliteConnection keeper, string connection) : IAsyncDisposable
    {
        public static async Task<Harness> CreateAsync(bool postgres)
        {
            var template = postgres ? new PostgreSqlModelTemplate() : null;
            if (template is not null) await template.InitializeAsync();
            var lease = template is not null ? await template.CreateDatabaseAsync() : null;
            var sqlite = new SqliteConnectionStringBuilder { DataSource = "pp_status_dispatch_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false };
            var keeper = new SqliteConnection(sqlite.ConnectionString);
            if (!postgres) await keeper.OpenAsync();
            var harness = new Harness(postgres, template, lease, keeper, postgres ? lease!.ConnectionString : sqlite.ConnectionString);
            if (!postgres) { await using var db = harness.Context(); await db.Database.EnsureCreatedAsync(); }
            return harness;
        }

        public PaymentContext Context(IInterceptor? interceptor = null)
        {
            if (postgres)
            {
                var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
                if (interceptor is not null) builder.AddInterceptors(interceptor);
                return new PostgreSqlPaymentContext(builder.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.FullName));
            if (interceptor is not null) sqlite.AddInterceptors(interceptor);
            return new PaymentContext(sqlite.Options);
        }

        public async Task<long> SeedAsync()
        {
            await using var db = Context();
            var role = new Role { Name = "admin" };
            var author = new User { Email = "status-dispatch@example.test", FullName = "Автор", PasswordHash = "test", Role = role, IsActive = true, IsApproved = true };
            var task = new TaskJournalItem { Title = "Задача", ObjectName = "Дело", AuthorUser = author, DueAt = DateTime.UtcNow.AddHours(1), Status = "done" };
            var item = new TaskStatusNotificationOutbox { TaskJournalItem = task, EventKey = "task:dispatch:concurrency", EventType = "completion", StatusTransitionToken = task.StatusTransitionToken, ActorName = "Автор", CreatedAt = DateTime.UtcNow };
            db.TaskStatusNotificationOutboxes.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await keeper.DisposeAsync();
            if (lease is not null) await lease.DisposeAsync();
            if (template is not null) await template.DisposeAsync();
        }
    }
}

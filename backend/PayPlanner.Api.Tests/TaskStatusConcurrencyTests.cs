using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalDecision_NextApproverEventSurvivesDeliveryFailure(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "next-approval-author@example.test", "Автор");
        var first = await CreateAdminUserAsync(db, "next-approval-first@example.test", "Первый");
        var second = await CreateAdminUserAsync(db, "next-approval-second@example.test", "Второй");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = first.Id, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
        task.Participants.Add(new TaskJournalParticipant { UserId = second.Id, Role = "approver", DisplayName = second.FullName, Email = second.Email, ApprovalStatus = "pending", SortOrder = 2 });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, first);
        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest { Decision = "approved" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("approval", pending.EventType);
        Assert.Null(pending.CompletedAt);
        Assert.Equal(task.StatusTransitionToken, pending.StatusTransitionToken);
        var participants = await db.TaskJournalParticipants.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id && item.Role == "approver").ToListAsync();
        Assert.Equal("approved", participants.Single(item => item.UserId == first.Id).ApprovalStatus);
        Assert.Equal("pending", participants.Single(item => item.UserId == second.Id).ApprovalStatus);
        Assert.Equal(participants.Single(item => item.UserId == second.Id).Id, pending.ApprovalParticipantId);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(new[] { task.Id }, notifications.ApprovalRequestedTaskIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkStatus_HiddenAndInvalidTasksStayUnchangedAndHaveNoEvent(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "bulk-visible-editor@example.test", "Редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "bulk-hidden-editor@example.test", "Другой", canEdit: true);
        var replacement = await CreateAdminUserAsync(db, "bulk-new-assignee@example.test", "Новый исполнитель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var initialDue = DateTime.UtcNow.Date.AddDays(1).AddHours(12);
        var visible = await CreateTaskAsync(db, current.Id, client.Id, current.Id, status: "inProgress", result: "Результат", dueAt: initialDue);
        var invalid = await CreateTaskAsync(db, current.Id, client.Id, current.Id, status: "inProgress", result: null, dueAt: initialDue);
        var hidden = await CreateTaskAsync(db, other.Id, client.Id, other.Id, status: "inProgress", result: "Результат", dueAt: initialDue);
        var originalInvalidDue = invalid.DueAt;
        var originalHiddenDue = hidden.DueAt;
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, current);
        var newDue = DateTime.UtcNow.Date.AddDays(5).AddHours(12);
        var response = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest { Ids = [visible.Id, invalid.Id, hidden.Id], Status = "done", Priority = "urgent", AssigneeUserId = replacement.Id, DueAt = newDue }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(2, GetAnonymousInt(ok.Value, "skipped"));
        db.ChangeTracker.Clear();
        var tasks = await db.TaskJournalItems.AsNoTracking().Where(item => item.Id == visible.Id || item.Id == invalid.Id || item.Id == hidden.Id).ToDictionaryAsync(item => item.Id);
        Assert.Equal("done", tasks[visible.Id].Status);
        Assert.Equal("urgent", tasks[visible.Id].Priority);
        Assert.Equal(replacement.Id, tasks[visible.Id].AssigneeUserId);
        Assert.Equal(newDue, tasks[visible.Id].DueAt);
        Assert.Equal("inProgress", tasks[invalid.Id].Status);
        Assert.Equal("normal", tasks[invalid.Id].Priority);
        Assert.Equal(current.Id, tasks[invalid.Id].AssigneeUserId);
        Assert.Equal(originalInvalidDue, tasks[invalid.Id].DueAt);
        Assert.Equal("inProgress", tasks[hidden.Id].Status);
        Assert.Equal("normal", tasks[hidden.Id].Priority);
        Assert.Equal(other.Id, tasks[hidden.Id].AssigneeUserId);
        Assert.Equal(originalHiddenDue, tasks[hidden.Id].DueAt);
        Assert.DoesNotContain(await db.TaskJournalParticipants.AsNoTracking().Where(item => item.TaskJournalItemId == invalid.Id || item.TaskJournalItemId == hidden.Id).ToListAsync(), item => item.UserId == replacement.Id && item.Role == "assignee");
        var pending = Assert.Single(await db.TaskStatusNotificationOutboxes.AsNoTracking().ToListAsync());
        Assert.Equal(visible.Id, pending.TaskJournalItemId);
    }

    [Fact]
    public async Task CreateOnApproval_FailureBeforeDispatchIsRecoveredFromCommittedOutbox()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "create-before-dispatch-author@example.test", "Автор");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var notifications = new RecordingNotificationService();
        var publisher = new RecordingRealtimeNotificationPublisher { FailTaskJournalChange = true };
        var controller = CreateController(db, notifications, author, realtimePublisher: publisher);
        await Assert.ThrowsAsync<IOException>(() => controller.CreateTask(new UpsertTaskJournalItemRequest { ClientId = client.Id, Title = "Задача на согласовании", DueAt = DateTime.UtcNow.AddDays(1), Status = "onApproval", Result = "Результат", Participants = [] }, CancellationToken.None));
        db.ChangeTracker.Clear();
        var task = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Title == "Задача на согласовании");
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(task.StatusTransitionToken, pending.StatusTransitionToken);
        Assert.Equal((await db.TaskJournalParticipants.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id && item.Role == "approver")).Id, pending.ApprovalParticipantId);
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(new[] { task.Id }, notifications.ApprovalRequestedTaskIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateOnApproval_DeliveryFailureLeavesRecoverableStatusEvent(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "create-queue-author@example.test", "Автор");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, author);
        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest { ClientId = client.Id, Title = "Задача на согласовании", DueAt = DateTime.UtcNow.AddDays(1), Status = "onApproval", Result = "Результат", Participants = [] }, CancellationToken.None);
        var dto = Assert.IsType<TaskJournalItemDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == dto.Id);
        Assert.Equal("approval", pending.EventType);
        Assert.Null(pending.CompletedAt);
        Assert.Equal((await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == dto.Id)).StatusTransitionToken, pending.StatusTransitionToken);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(new[] { dto.Id }, notifications.ApprovalRequestedTaskIds);
    }

    [Fact]
    public async Task ApprovalCompletion_FailureBeforeDispatchIsRecoveredFromCommittedOutbox()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "approval-before-dispatch-author@example.test", "Автор");
        var approver = await CreateAdminUserAsync(db, "approval-before-dispatch-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = approver.Id, Role = "approver", DisplayName = approver.FullName, Email = approver.Email, ApprovalStatus = "pending" });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var publisher = new RecordingRealtimeNotificationPublisher { FailTaskJournalChange = true };
        var controller = CreateController(db, notifications, approver, realtimePublisher: publisher);
        await Assert.ThrowsAsync<IOException>(() => controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest { Decision = "approved", FinalAction = "complete" }, CancellationToken.None));
        db.ChangeTracker.Clear();
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal("done", (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).Status);
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(new[] { task.Id }, notifications.CompletedTaskIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalCompletion_DeliveryFailureLeavesRecoverableStatusEvent(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "approval-completion-author@example.test", "Автор");
        var approver = await CreateAdminUserAsync(db, "approval-completion-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = approver.Id, Role = "approver", DisplayName = approver.FullName, Email = approver.Email, ApprovalStatus = "pending" });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, approver);
        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest { Decision = "approved", FinalAction = "complete" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Null(pending.CompletedAt);
        Assert.Equal(TaskCompletionNotificationPolicy.ApprovalEventType, pending.EventType);
        Assert.Equal((await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).StatusTransitionToken, pending.StatusTransitionToken);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(new[] { task.Id }, notifications.CompletedTaskIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkStatus_MultipleEventsRetainTheirOwnTransitionAndRecover(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-multiple-author@example.test", "Автор");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var first = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "inProgress", result: "Результат");
        var second = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "inProgress", result: "Результат");
        var ids = new[] { first.Id, second.Id };
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, author);
        Assert.IsType<OkObjectResult>((await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest { Ids = [.. ids], Status = "done", Result = "Результат" }, CancellationToken.None)).Result);

        var events = await db.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => ids.Contains(item.TaskJournalItemId)).ToListAsync();
        var tasks = await db.TaskJournalItems.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(item => item.StatusTransitionToken).Distinct().Count());
        Assert.All(events, item =>
        {
            Assert.Null(item.CompletedAt);
            Assert.Equal(tasks[item.TaskJournalItemId].StatusTransitionToken, item.StatusTransitionToken);
            Assert.False(string.IsNullOrEmpty(item.StatusTransitionToken));
        });
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => ids.Contains(item.TaskJournalItemId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        var processor = new TaskStatusNotificationDispatchProcessor(db, notifications);
        foreach (var item in events)
            Assert.True(await processor.ProcessAsync(item.Id, CancellationToken.None));
        Assert.Equal(ids.OrderBy(id => id), notifications.CompletedTaskIds.OrderBy(id => id));
        Assert.Equal(2, await db.TaskStatusNotificationOutboxes.CountAsync(item => ids.Contains(item.TaskJournalItemId) && item.CompletedAt != null));
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(false, "onApproval")]
    [InlineData(true, "done")]
    [InlineData(true, "onApproval")]
    public async Task BulkStatus_DeliveryFailureQueuesOnlyChangedVisibleTasks(bool postgres, string target)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-queue-author@example.test", "Автор");
        var reviewer = await CreateAdminUserAsync(db, "bulk-queue-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var changed = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "inProgress", result: "Результат");
        var unchanged = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: target, result: "Результат");
        if (target == "onApproval")
        {
            foreach (var item in new[] { changed, unchanged })
                item.Participants.Add(new TaskJournalParticipant { UserId = reviewer.Id, Role = "approver", DisplayName = reviewer.FullName, Email = reviewer.Email, ApprovalStatus = "pending" });
            await db.SaveChangesAsync();
        }
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, author);
        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest { Ids = [changed.Id, unchanged.Id, 999999], Status = target, Result = "Результат" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var queued = await db.TaskStatusNotificationOutboxes.AsNoTracking().ToListAsync();
        var pending = Assert.Single(queued);
        Assert.Equal(changed.Id, pending.TaskJournalItemId);
        Assert.Null(pending.CompletedAt);
        var saved = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == changed.Id);
        Assert.Equal(saved.StatusTransitionToken, pending.StatusTransitionToken);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(1, target == "done" ? notifications.CompletedTaskIds.Count : notifications.ApprovalRequestedTaskIds.Count);
    }

    private sealed class FailStatusOutboxSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<TaskStatusNotificationOutbox>().Any(entry => entry.State == EntityState.Added))
                throw new IOException("Synthetic outbox write failure.");
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalDecision_NextApproverEventFailureRollsBackDecision(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        var interceptor = new FailStatusOutboxSave();
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(interceptor).Options)
            : await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "next-rollback-author@example.test", "Автор");
        var first = await CreateAdminUserAsync(db, "next-rollback-first@example.test", "Первый");
        var second = await CreateAdminUserAsync(db, "next-rollback-second@example.test", "Второй");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = first.Id, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
        task.Participants.Add(new TaskJournalParticipant { UserId = second.Id, Role = "approver", DisplayName = second.FullName, Email = second.Email, ApprovalStatus = "pending", SortOrder = 2 });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), first);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest { Decision = "approved" }, CancellationToken.None));
        db.ChangeTracker.Clear();
        var saved = await db.TaskJournalItems.AsNoTracking().Include(item => item.Participants).SingleAsync(item => item.Id == task.Id);
        Assert.Equal("onApproval", saved.Status);
        Assert.All(saved.Participants.Where(item => item.Role == "approver"), item => Assert.Equal("pending", item.ApprovalStatus));
        Assert.Empty(await db.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateOnApproval_OutboxFailureDoesNotCreateTask(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        var interceptor = new FailStatusOutboxSave();
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(interceptor).Options)
            : await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "create-rollback-author@example.test", "Автор");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var controller = CreateController(db, new RecordingNotificationService(), author);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => controller.CreateTask(new UpsertTaskJournalItemRequest { ClientId = client.Id, Title = "Задача на согласовании", DueAt = DateTime.UtcNow.AddDays(1), Status = "onApproval", Result = "Результат", Participants = [] }, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.TaskJournalItems.AsNoTracking().Where(item => item.Title == "Задача на согласовании").ToListAsync());
        Assert.Empty(await db.TaskStatusNotificationOutboxes.AsNoTracking().ToListAsync());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalCompletion_OutboxFailureRollsBackDecision(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        var interceptor = new FailStatusOutboxSave();
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(interceptor).Options)
            : await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "approval-rollback-author@example.test", "Автор");
        var approver = await CreateAdminUserAsync(db, "approval-rollback-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = approver.Id, Role = "approver", DisplayName = approver.FullName, Email = approver.Email, ApprovalStatus = "pending" });
        await db.SaveChangesAsync();
        var token = task.StatusTransitionToken;
        var controller = CreateController(db, new RecordingNotificationService(), approver);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest { Decision = "approved", FinalAction = "complete" }, CancellationToken.None));
        db.ChangeTracker.Clear();
        var saved = await db.TaskJournalItems.AsNoTracking().Include(item => item.Participants).SingleAsync(item => item.Id == task.Id);
        Assert.Equal("onApproval", saved.Status);
        Assert.Equal(token, saved.StatusTransitionToken);
        Assert.Equal("pending", Assert.Single(saved.Participants, item => item.UserId == approver.Id).ApprovalStatus);
        Assert.Empty(await db.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }


    [Theory]
    [InlineData(false, "onApproval")]
    [InlineData(true, "done")]
    public async Task BulkStatus_OutboxFailureRollsBackEntireBatch(bool postgres, string target)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        var interceptor = new FailStatusOutboxSave();
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(interceptor).Options)
            : await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "bulk-rollback-author@example.test", "Автор");
        var reviewer = await CreateAdminUserAsync(db, "bulk-rollback-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var first = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "inProgress", result: "Результат");
        var second = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "inProgress", result: "Результат");
        if (target == "onApproval")
        {
            foreach (var item in new[] { first, second })
                item.Participants.Add(new TaskJournalParticipant { UserId = reviewer.Id, Role = "approver", DisplayName = reviewer.FullName, Email = reviewer.Email, ApprovalStatus = "pending" });
            await db.SaveChangesAsync();
        }
        var ids = new[] { first.Id, second.Id };
        var controller = CreateController(db, new RecordingNotificationService(), author);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest { Ids = [.. ids], Status = target, Result = "Результат" }, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.All(await db.TaskJournalItems.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(), item => Assert.Equal("inProgress", item.Status));
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item => ids.Contains(item.Id)));
        Assert.Empty(await db.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => ids.Contains(item.TaskJournalItemId)).ToListAsync());
    }

    [Theory]
    [InlineData(false, "onApproval")]
    [InlineData(true, "done")]
    public async Task FullUpdate_OutboxWriteFailureRollsBackTask(bool postgres, string target)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        var interceptor = new FailStatusOutboxSave();
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(interceptor).Options)
            : await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "full-rollback-author@example.test", "Автор");
        var assignee = await CreateAdminUserAsync(db, "full-rollback-assignee@example.test", "Исполнитель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, status: "inProgress");
        var originalToken = task.StatusTransitionToken;
        var request = new UpsertTaskJournalItemRequest { ClientId = client.Id, Title = task.Title, AssigneeUserId = assignee.Id, DueAt = task.DueAt, Status = target, Result = "Результат", Participants = [] };
        var controller = CreateController(db, new RecordingNotificationService(), author);
        interceptor.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => controller.UpdateTask(task.Id, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        var saved = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal("inProgress", saved.Status);
        Assert.Equal(originalToken, saved.StatusTransitionToken);
        Assert.Empty(await db.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }

    [Theory]
    [InlineData(false, "done")]
    [InlineData(false, "onApproval")]
    [InlineData(true, "done")]
    [InlineData(true, "onApproval")]
    public async Task FullUpdate_DeliveryFailureLeavesRecoverableStatusEvent(bool postgres, string target)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is not null ? await template.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "full-queue-author@example.test", "Автор");
        var assignee = await CreateAdminUserAsync(db, "full-queue-assignee@example.test", "Исполнитель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, status: "inProgress");
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var request = new UpsertTaskJournalItemRequest { ClientId = client.Id, Title = task.Title, AssigneeUserId = assignee.Id, DueAt = task.DueAt, Status = target, Result = "Результат", Participants = [] };
        var controller = CreateController(db, notifications, author);
        Assert.IsType<OkObjectResult>((await controller.UpdateTask(task.Id, request, CancellationToken.None)).Result);
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Null(pending.CompletedAt);
        Assert.Equal(target == "done" ? TaskCompletionNotificationPolicy.OrdinaryEventType : "approval", pending.EventType);
        Assert.Equal((await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).StatusTransitionToken, pending.StatusTransitionToken);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(1, target == "done" ? notifications.CompletedTaskIds.Count : notifications.ApprovalRequestedTaskIds.Count);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("onApproval")]
    public async Task TaskStatus_FailureBeforeImmediateDispatch_IsRecoveredFromCommittedOutbox(string target)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "committed-author@example.test", "Автор");
        var executor = await CreateTaskUserAsync(db, "committed-executor@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, executor.Id);
        db.SystemSettings.Add(new SystemSetting { Key = "tasks.executorCompletionAllowedUserIds", Value = $"[{executor.Id}]" });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var publisher = new RecordingRealtimeNotificationPublisher { FailTaskJournalChange = true };
        var controller = CreateController(db, notifications, executor, realtimePublisher: publisher);
        var request = new UpdateTaskJournalStatusRequest { Status = target, Result = "Результат" };
        await Assert.ThrowsAsync<IOException>(() => controller.UpdateStatus(task.Id, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.CompletedAt);
        Assert.Equal(target, (await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).Status);
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.Equal(1, target == "done" ? notifications.CompletedTaskIds.Count : notifications.ApprovalRequestedTaskIds.Count);
        var retryController = CreateController(db, notifications, executor);
        Assert.IsType<OkObjectResult>((await retryController.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        Assert.Equal(1, await db.TaskStatusNotificationOutboxes.CountAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("onApproval")]
    public async Task TaskStatus_DeliveryFailureIsRecoveredWithoutDuplicate(string target)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "queued-author@example.test", "Автор");
        var executor = await CreateTaskUserAsync(db, "queued-executor@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, executor.Id);
        db.SystemSettings.Add(new SystemSetting { Key = "tasks.executorCompletionAllowedUserIds", Value = $"[{executor.Id}]" });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var request = new UpdateTaskJournalStatusRequest { Status = target, Result = "Результат" };
        var controller = CreateController(db, notifications, executor);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        var pending = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Null(pending.CompletedAt);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal((await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id)).StatusTransitionToken, pending.StatusTransitionToken);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        Assert.Equal(1, await db.TaskStatusNotificationOutboxes.CountAsync(item => item.TaskJournalItemId == task.Id));
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == pending.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        notifications.FailDurableDelivery = false;
        db.ChangeTracker.Clear();
        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, notifications).ProcessAsync(pending.Id, CancellationToken.None));
        Assert.NotNull((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync(item => item.Id == pending.Id)).CompletedAt);
        Assert.Equal(1, target == "done" ? notifications.CompletedTaskIds.Count : notifications.ApprovalRequestedTaskIds.Count);
    }

    private sealed class PauseTaskStatusSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!paused && eventData.Context!.ChangeTracker.Entries<TaskJournalItem>().Any(entry => entry.State == EntityState.Modified && entry.Property(t => t.Status).IsModified))
            {
                paused = true;
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class PauseApprovalDecisionSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!paused && eventData.Context!.ChangeTracker.Entries<TaskJournalParticipant>().Any(entry => entry.State == EntityState.Modified && entry.Property(item => item.ApprovalStatus).IsModified))
            {
                paused = true;
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task ApprovalDecision_ConcurrentDifferentApproversPreserveQueue_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext Context(SaveChangesInterceptor? gate = null)
        {
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString);
            if (gate is not null) options.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(options.Options);
        }
        await using var seed = Context();
        var author = await CreateAdminUserAsync(seed, "decision-order-author@example.test", "Автор");
        var first = await CreateAdminUserAsync(seed, "decision-order-first@example.test", "Первый");
        var second = await CreateAdminUserAsync(seed, "decision-order-second@example.test", "Второй");
        var client = await CreateClientAsync(seed, "Синтетический клиент");
        var task = await CreateTaskAsync(seed, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = first.Id, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
        task.Participants.Add(new TaskJournalParticipant { UserId = second.Id, Role = "approver", DisplayName = second.FullName, Email = second.Email, ApprovalStatus = "pending", SortOrder = 2 });
        await seed.SaveChangesAsync();
        var gate = new PauseApprovalDecisionSave();
        await using var firstDb = Context(gate);
        await using var secondDb = Context();
        var firstDecision = new UpdateTaskJournalApprovalRequest { Decision = "approved" };
        var secondDecision = new UpdateTaskJournalApprovalRequest { Decision = "revisionRequested", Resolution = "Уточнить результат" };
        var slow = CreateController(firstDb, new RecordingNotificationService(), first).UpdateApproval(task.Id, firstDecision, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ActionResult? during;
        try { during = (await CreateController(secondDb, new RecordingNotificationService(), second).UpdateApproval(task.Id, secondDecision, CancellationToken.None)).Result; }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await slow.WaitAsync(TimeSpan.FromSeconds(30))).Result);
        Assert.Equal(423, Assert.IsType<ObjectResult>(during).StatusCode);
        secondDb.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>((await CreateController(secondDb, new RecordingNotificationService(), second).UpdateApproval(task.Id, secondDecision, CancellationToken.None)).Result);
        var saved = await seed.TaskJournalItems.AsNoTracking().Include(item => item.Participants).SingleAsync(item => item.Id == task.Id);
        Assert.Equal("revision", saved.Status);
        Assert.Equal("approved", saved.Participants.Single(item => item.UserId == first.Id && item.Role == "approver").ApprovalStatus);
        Assert.Equal("revisionRequested", saved.Participants.Single(item => item.UserId == second.Id && item.Role == "approver").ApprovalStatus);
        Assert.Equal(2, await seed.TaskJournalComments.AsNoTracking().CountAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Equal(1, await seed.TaskStatusNotificationOutboxes.AsNoTracking().CountAsync(item => item.TaskJournalItemId == task.Id));
    }


    [Fact]
    public async Task ApprovalDecision_RevokedTaskPermissionCannotCommit_Postgres()
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext Context() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Context();
        var author = await CreateAdminUserAsync(seed, "decision-access-author@example.test", "Автор");
        var reviewer = await CreateTaskUserAsync(seed, "decision-access-reviewer@example.test", "Согласующий", canEdit: false);
        var client = await CreateClientAsync(seed, "Синтетический клиент");
        var task = await CreateTaskAsync(seed, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = reviewer.Id, Role = "approver", DisplayName = reviewer.FullName, Email = reviewer.Email, ApprovalStatus = "pending" });
        await seed.SaveChangesAsync();
        var permission = await seed.RolePermissions.SingleAsync(item => item.RoleId == reviewer.RoleId && item.Section == RolePermissionCatalog.Tasks);
        await using var requestDb = Context();
        var controller = CreateController(requestDb, new RecordingNotificationService(), reviewer);
        var decision = new UpdateTaskJournalApprovalRequest { Decision = "approved" };
        await using (var transaction = await seed.Database.BeginTransactionAsync())
        {
            permission.CanView = false;
            await seed.SaveChangesAsync();
            var during = await controller.UpdateApproval(task.Id, decision, CancellationToken.None);
            Assert.Equal(423, Assert.IsType<ObjectResult>(during.Result).StatusCode);
            await transaction.CommitAsync();
        }
        requestDb.ChangeTracker.Clear();
        Assert.IsType<ForbidResult>((await controller.UpdateApproval(task.Id, decision, CancellationToken.None)).Result);
        var participant = await requestDb.TaskJournalParticipants.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id && item.UserId == reviewer.Id && item.Role == "approver");
        Assert.Equal("pending", participant.ApprovalStatus);
        Assert.Empty(await requestDb.TaskStatusNotificationOutboxes.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
        Assert.Empty(await requestDb.TaskJournalComments.AsNoTracking().Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalDecision_ConcurrentDuplicatesDoNotCreateMultipleEvents(bool postgres)
    {
        await using var template = postgres ? new PostgreSqlModelTemplate() : null;
        if (template is not null) await template.InitializeAsync();
        await using var database = template is null ? null : await template.CreateDatabaseAsync();
        var sqliteString = new SqliteConnectionStringBuilder { DataSource = "approval_race_" + Guid.NewGuid().ToString("N"), Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, Pooling = false }.ConnectionString;
        await using var keeper = new SqliteConnection(sqliteString);
        if (!postgres) await keeper.OpenAsync();
        PaymentContext Context(SaveChangesInterceptor? gate = null)
        {
            if (postgres)
            {
                var pg = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString);
                if (gate is not null) pg.AddInterceptors(gate);
                return new PostgreSqlPaymentContext(pg.Options);
            }
            var sqlite = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(sqliteString);
            if (gate is not null) sqlite.AddInterceptors(gate);
            return new PaymentContext(sqlite.Options);
        }
        await using var seed = Context();
        if (!postgres) await seed.Database.EnsureCreatedAsync();
        var author = await CreateAdminUserAsync(seed, "decision-race-author@example.test", "Автор");
        var first = await CreateAdminUserAsync(seed, "decision-race-first@example.test", "Первый");
        var next = await CreateAdminUserAsync(seed, "decision-race-next@example.test", "Второй");
        var client = await CreateClientAsync(seed, "Синтетический клиент");
        var task = await CreateTaskAsync(seed, author.Id, client.Id, author.Id, status: "onApproval", result: "Результат");
        task.Participants.Add(new TaskJournalParticipant { UserId = first.Id, Role = "approver", DisplayName = first.FullName, Email = first.Email, ApprovalStatus = "pending", SortOrder = 1 });
        task.Participants.Add(new TaskJournalParticipant { UserId = next.Id, Role = "approver", DisplayName = next.FullName, Email = next.Email, ApprovalStatus = "pending", SortOrder = 2 });
        await seed.SaveChangesAsync();
        var gate = new PauseApprovalDecisionSave();
        await using var slowDb = Context(gate);
        await using var fastDb = Context();
        var request = new UpdateTaskJournalApprovalRequest { Decision = "approved" };
        var slow = CreateController(slowDb, new RecordingNotificationService(), first).UpdateApproval(task.Id, request, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ActionResult? fast;
        try { fast = (await CreateController(fastDb, new RecordingNotificationService(), first).UpdateApproval(task.Id, request, CancellationToken.None)).Result; }
        finally { gate.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await slow.WaitAsync(TimeSpan.FromSeconds(30))).Result);
        Assert.Equal(423, Assert.IsType<ObjectResult>(fast).StatusCode);
        fastDb.ChangeTracker.Clear();
        var retry = await CreateController(fastDb, new RecordingNotificationService(), first).UpdateApproval(task.Id, request, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(retry.Result);
        Assert.Equal(1, await seed.TaskStatusNotificationOutboxes.AsNoTracking().CountAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Equal(1, await seed.TaskJournalComments.AsNoTracking().CountAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("onApproval")]
    public async Task TaskStatus_TwoConcurrentFirstSubmissionsDoNotDuplicateEffects_Postgres(string target)
    {
        await using var template = new PostgreSqlModelTemplate();
        await template.InitializeAsync();
        await using var database = await template.CreateDatabaseAsync();
        PostgreSqlPaymentContext Context(SaveChangesInterceptor? gate = null)
        {
            var builder = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString);
            if (gate is not null) builder.AddInterceptors(gate);
            return new PostgreSqlPaymentContext(builder.Options);
        }
        await using var seed = Context();
        var author = await CreateAdminUserAsync(seed, "race-author@example.test", "Автор");
        var executor = await CreateTaskUserAsync(seed, "race-executor@example.test", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(seed, "Синтетический клиент");
        var task = await CreateTaskAsync(seed, author.Id, client.Id, executor.Id);
        seed.SystemSettings.Add(new SystemSetting { Key = "tasks.executorCompletionAllowedUserIds", Value = $"[{executor.Id}]" });
        await seed.SaveChangesAsync();
        var gate = new PauseTaskStatusSave();
        await using var slowDb = Context(gate);
        await using var fastDb = Context();
        var slowNotifications = new RecordingNotificationService();
        var fastNotifications = new RecordingNotificationService();
        var request = new UpdateTaskJournalStatusRequest { Status = target, Result = "Результат" };
        var slow = CreateController(slowDb, slowNotifications, executor).UpdateStatus(task.Id, request, CancellationToken.None);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ActionResult? fastResult;
        try { fastResult = (await CreateController(fastDb, fastNotifications, executor).UpdateStatus(task.Id, request, CancellationToken.None)).Result; }
        finally { gate.Release.TrySetResult(); }
        var slowResult = (await slow.WaitAsync(TimeSpan.FromSeconds(30))).Result;
        Assert.IsType<OkObjectResult>(slowResult);
        Assert.Equal(423, Assert.IsType<ObjectResult>(fastResult).StatusCode);
        var retry = await CreateController(fastDb, fastNotifications, executor).UpdateStatus(task.Id, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(retry.Result);
        var notices = target == "done" ? slowNotifications.CompletedTaskIds.Count + fastNotifications.CompletedTaskIds.Count
            : slowNotifications.ApprovalRequestedTaskIds.Count + fastNotifications.ApprovalRequestedTaskIds.Count;
        Assert.Equal(1, notices);
        Assert.Equal(1, await seed.TaskJournalComments.CountAsync(c => c.TaskJournalItemId == task.Id));
        Assert.Equal(target, (await seed.TaskJournalItems.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class TasksControllerTests
{
    [Theory]
    [InlineData("type")]
    [InlineData("inApp")]
    public async Task TaskCoalescing_SelfAuthorDoesNotGainActorExcludedChangeFallback(string disabled)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "self-completion-author@example.test", "Постановщик");
        var observer = await CreateAdminUserAsync(db, "self-completion-observer@example.test", "Наблюдатель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        task.Participants.Add(new TaskJournalParticipant { UserId = observer.Id, DisplayName = observer.FullName, Role = "observer" });
        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = disabled == "type"
                ? """{"disabledTypes":["taskCompleted"]}"""
                : """{"disabledChannels":{"taskCompleted":["inApp"]}}"""
        });
        await db.SaveChangesAsync();
        var notifications = NotificationServiceTests.CreateService(db,
            new NotificationServiceTests.RecordingEmailService(), new NotificationServiceTests.RecordingTelegramService());

        Assert.IsType<OkObjectResult>((await CreateController(db, notifications, author).UpdateStatus(task.Id,
            new UpdateTaskJournalStatusRequest { Status = "done", Result = "Документы готовы" }, CancellationToken.None)).Result);

        var row = Assert.Single(await db.UserNotifications.AsNoTracking().ToListAsync());
        Assert.Equal(observer.Id, row.RecipientUserId);
        Assert.Equal(UserNotificationTypes.TaskChanged, row.Type);
        var operation = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(TaskCompletionNotificationPolicy.OrdinaryEventType, operation.EventType);
        Assert.NotNull(operation.CompletedAt);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("bulk")]
    public async Task TaskCoalescing_NormalFullAndBulkEntriesCreateOneAuthorEvent(string entryPoint)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "normal-completion-author@example.test", "Постановщик");
        var executor = await CreateAdminUserAsync(db, "normal-completion-executor@example.test", "Исполнитель");
        var observer = await CreateAdminUserAsync(db, "normal-completion-observer@example.test", "Наблюдатель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var notifications = NotificationServiceTests.CreateService(db,
            new NotificationServiceTests.RecordingEmailService(), new NotificationServiceTests.RecordingTelegramService());
        var request = new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            Title = "Подготовить документы",
            AssigneeUserId = executor.Id,
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 60,
            Reminders = ["atDue"],
            Channels = ["inApp"],
            Participants = [new TaskJournalParticipantRequest { UserId = observer.Id, Role = "observer" }]
        };
        Assert.IsType<CreatedResult>((await CreateController(db, notifications, author)
            .CreateTask(request, CancellationToken.None)).Result);
        var taskId = (await db.TaskJournalItems.AsNoTracking().SingleAsync()).Id;
        var lastNotificationId = await db.UserNotifications.MaxAsync(row => (int?)row.Id) ?? 0;
        var controller = CreateController(db, notifications, executor);
        request.Status = "done";
        request.Result = "Документы готовы";

        if (entryPoint == "full")
        {
            Assert.IsType<OkObjectResult>((await controller.UpdateTask(taskId, request, CancellationToken.None)).Result);
        }
        else
        {
            Assert.IsType<OkObjectResult>((await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
            {
                Ids = [taskId],
                Status = request.Status,
                Result = request.Result
            }, CancellationToken.None)).Result);
        }

        var rows = await db.UserNotifications.AsNoTracking().Where(row => row.Id > lastNotificationId).ToListAsync();
        Assert.Equal(UserNotificationTypes.TaskCompleted, Assert.Single(rows, row => row.RecipientUserId == author.Id).Type);
        Assert.Equal(UserNotificationTypes.TaskChanged, Assert.Single(rows, row => row.RecipientUserId == observer.Id).Type);
        Assert.Equal(2, rows.Count);
        var operation = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(TaskCompletionNotificationPolicy.CoalescedEventType, operation.EventType);
        Assert.NotNull(operation.CompletedAt);
    }

    [Fact]
    public async Task TaskCoalescing_DeadlineChangeDuringCompletionKeepsSeparateChangeNotification()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "material-completion-author@example.test", "Постановщик");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var notifications = new RecordingNotificationService();

        Assert.IsType<OkObjectResult>((await CreateController(db, notifications, author).UpdateStatus(task.Id,
            new UpdateTaskJournalStatusRequest { Status = "done", Result = "Готово", DueAt = task.DueAt.AddHours(1) }, CancellationToken.None)).Result);

        Assert.Equal(TaskCompletionNotificationPolicy.OrdinaryEventType, (await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).EventType);
        Assert.Contains(Assert.Single(notifications.ChangedTasks).ChangeLabels, change => change.StartsWith("Срок:", StringComparison.Ordinal));
        Assert.Single(notifications.CompletedTaskIds);
    }

    [Fact]
    public async Task TaskCoalescing_CompletionWithCommentKeepsIndependentCommentContext()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "comment-completion-author@example.test", "Постановщик");
        var executor = await CreateAdminUserAsync(db, "comment-completion-executor@example.test", "Исполнитель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var notifications = NotificationServiceTests.CreateService(db,
            new NotificationServiceTests.RecordingEmailService(), new NotificationServiceTests.RecordingTelegramService());
        var request = new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            Title = "Подготовить документы",
            AssigneeUserId = executor.Id,
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 60,
            Reminders = ["atDue"],
            Channels = ["inApp"],
            Participants = [new TaskJournalParticipantRequest { UserId = executor.Id, Role = "assignee" }]
        };
        Assert.IsType<CreatedResult>((await CreateController(db, notifications, author)
            .CreateTask(request, CancellationToken.None)).Result);
        var taskId = (await db.TaskJournalItems.AsNoTracking().SingleAsync()).Id;
        request.Status = "done";
        request.Result = "Готово";
        request.ChangeComment = "Передано представителю";

        Assert.IsType<OkObjectResult>((await CreateController(db, notifications, executor)
            .UpdateTask(taskId, request, CancellationToken.None)).Result);

        Assert.Equal(TaskCompletionNotificationPolicy.OrdinaryEventType, (await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).EventType);
        var authorRows = await db.UserNotifications.Where(row => row.TaskJournalItemId == taskId && row.RecipientUserId == author.Id).ToListAsync();
        Assert.Equal(2, authorRows.Count);
        Assert.Contains("Передано представителю", Assert.Single(authorRows, row => row.Type == UserNotificationTypes.TaskChanged).BodyPreview);
        Assert.Single(authorRows, row => row.Type == UserNotificationTypes.TaskCompleted);
    }

    [Fact]
    public async Task TaskCoalescing_StatusEntryPointCreatesOneAuthorEventAndPreservesObserverChange()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "coalesced-author@example.test", "Постановщик");
        var executor = await CreateAdminUserAsync(db, "coalesced-executor@example.test", "Исполнитель");
        var observer = await CreateAdminUserAsync(db, "coalesced-observer@example.test", "Наблюдатель");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, executor.Id);
        task.Participants.Add(new TaskJournalParticipant { UserId = observer.Id, DisplayName = observer.FullName, Role = "observer" });
        await db.SaveChangesAsync();
        var notifications = NotificationServiceTests.CreateService(db,
            new NotificationServiceTests.RecordingEmailService(), new NotificationServiceTests.RecordingTelegramService());
        var controller = CreateController(db, notifications, executor);
        var request = new UpdateTaskJournalStatusRequest { Status = "done", Result = "Документы готовы" };

        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);

        var rows = await db.UserNotifications.AsNoTracking().Where(row => row.TaskJournalItemId == task.Id).ToListAsync();
        Assert.Equal(UserNotificationTypes.TaskCompleted, Assert.Single(rows, row => row.RecipientUserId == author.Id).Type);
        Assert.Equal(UserNotificationTypes.TaskChanged, Assert.Single(rows, row => row.RecipientUserId == observer.Id).Type);
        Assert.Equal(2, rows.Count);
        var operation = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(TaskCompletionNotificationPolicy.CoalescedEventType, operation.EventType);
        Assert.NotNull(operation.CompletedAt);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, request, CancellationToken.None)).Result);
        Assert.Equal(2, await db.UserNotifications.CountAsync(row => row.TaskJournalItemId == task.Id));
    }

    [Theory]
    [InlineData("full")]
    [InlineData("status")]
    [InlineData("bulk")]
    public async Task TaskCompletion_AllOrdinaryEntryPointsPersistExplicitOrigin(string entryPoint)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "completion-entry-author@example.test", "Постановщик");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "new");
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        var controller = CreateController(db, notifications, author);
        if (entryPoint == "full")
        {
            Assert.IsType<OkObjectResult>((await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
            {
                ClientId = client.Id,
                Title = task.Title,
                AssigneeUserId = author.Id,
                DueAt = task.DueAt,
                Status = "done",
                Result = "Результат",
                PlannedDurationMinutes = task.PlannedDurationMinutes,
                Reminders = ["atDue"],
                Channels = ["inApp"],
                Participants = []
            }, CancellationToken.None)).Result);
        }
        else if (entryPoint == "status")
        {
            Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id,
                new UpdateTaskJournalStatusRequest { Status = "done", Result = "Результат" }, CancellationToken.None)).Result);
        }
        else
        {
            Assert.IsType<OkObjectResult>((await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
            {
                Ids = [task.Id],
                Status = "done",
                Result = "Результат"
            }, CancellationToken.None)).Result);
        }

        var entry = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(TaskCompletionNotificationPolicy.OrdinaryOrigin, TaskCompletionNotificationPolicy.OriginForEventType(entry.EventType));
        Assert.Equal(TaskCompletionNotificationPolicy.OrdinaryEventType, entry.EventType);
        Assert.Null(entry.CompletedAt);
        Assert.Equal((await db.TaskJournalItems.AsNoTracking().SingleAsync()).StatusTransitionToken, entry.StatusTransitionToken);
    }

    [Fact]
    public async Task TaskCompletion_FinalApprovalRecoversSnapshotAfterFullSaveReplacesParticipants()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "completion-snapshot-author@example.test", "Постановщик");
        var reviewer = await CreateAdminUserAsync(db, "completion-snapshot-reviewer@example.test", "Согласующий");
        var client = await CreateClientAsync(db, "Синтетический клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", result: "Первоначальный результат");
        var participant = new TaskJournalParticipant { UserId = reviewer.Id, DisplayName = reviewer.FullName, Email = reviewer.Email, Role = "approver", ApprovalStatus = "pending" };
        task.Participants.Add(participant);
        await db.SaveChangesAsync();
        var participantId = participant.Id;
        var notifications = new RecordingNotificationService { FailDurableDelivery = true };
        Assert.IsType<OkObjectResult>((await CreateController(db, notifications, reviewer).UpdateApproval(task.Id,
            new UpdateTaskJournalApprovalRequest { Decision = "approved", FinalAction = "complete", Resolution = "Первоначальная рецензия" }, CancellationToken.None)).Result);
        var entry = await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal(TaskCompletionNotificationPolicy.ApprovalEventType, entry.EventType);
        Assert.Equal(participantId, entry.ApprovalParticipantId);
        Assert.Empty(notifications.ApprovalDecisionTaskIds);
        Assert.IsType<OkObjectResult>((await CreateController(db, notifications, author).UpdateTask(task.Id,
            new UpsertTaskJournalItemRequest
            {
                ClientId = client.Id,
                Title = task.Title,
                AssigneeUserId = author.Id,
                DueAt = task.DueAt,
                Status = "done",
                Result = "Позднее уточнение результата",
                Participants = []
            }, CancellationToken.None)).Result);
        Assert.False(await db.TaskJournalParticipants.AnyAsync(item => item.Id == participantId));
        Assert.Null((await db.TaskStatusNotificationOutboxes.AsNoTracking().SingleAsync()).ApprovalParticipantId);
        Assert.Equal(entry.StatusTransitionToken, (await db.TaskJournalItems.AsNoTracking().SingleAsync()).StatusTransitionToken);
        db.ChangeTracker.Clear();
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await db.TaskStatusNotificationOutboxes.Where(item => item.Id == entry.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil, (DateTime?)expired));
        var probe = new CompletionSnapshotProbe();

        Assert.True(await new TaskStatusNotificationDispatchProcessor(db, probe).ProcessAsync(entry.Id, CancellationToken.None));

        Assert.Equal(participantId, probe.Approver?.Id);
        Assert.Equal(reviewer.FullName, probe.Approver?.DisplayName);
        Assert.Equal("Первоначальная рецензия", probe.Approver?.Resolution);
        Assert.Equal("Первоначальный результат", probe.Result);
        Assert.Equal(entry.EventKey, probe.EventKey);
        Assert.Equal("Позднее уточнение результата", (await db.TaskJournalItems.AsNoTracking().SingleAsync()).Result);
    }

    private sealed class CompletionSnapshotProbe : IDurableTaskStatusNotificationService
    {
        public TaskJournalParticipant? Approver { get; private set; }
        public string? Result { get; private set; }
        public string? EventKey { get; private set; }
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCompletionForEventAsync(TaskJournalItem task, string eventKey, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Expected the approval completion snapshot.");
        public Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalForEventAsync(TaskJournalItem task, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Expected a completed approval.");
        public Task<IReadOnlyList<NotificationDelivery>> NotifyApprovalCompletionForEventAsync(TaskJournalItem task, TaskJournalParticipant approver, string actorName, int actorUserId, string eventKey, CancellationToken cancellationToken = default)
        {
            Approver = approver;
            Result = task.Result;
            EventKey = eventKey;
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>([]);
        }
    }
}

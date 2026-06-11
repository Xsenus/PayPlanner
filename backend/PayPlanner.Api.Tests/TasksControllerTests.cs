using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class TasksControllerTests
{
    [Fact]
    public async Task CreateTask_RequiresClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            Title = "Подготовить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("клиента", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAssignees_ReturnsProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-assignees@example.com", "Администратор");
        var assignee = await CreateAdminUserAsync(db, "kamchatka-assignee@example.com", "Исполнитель Камчатка");
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = "profile.timeZoneId",
            Value = "Asia/Kamchatka",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetAssignees(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalAssigneeDto>>(ok.Value);
        var dto = Assert.Single(items, item => item.Id == assignee.Id);
        Assert.Equal("Asia/Kamchatka", dto.TimeZoneId);
    }

    [Fact]
    public async Task UpdateSettings_PersistsCaseInactivityAutomationSettings()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-settings@example.com", "Администратор");
        var fallbackAssignee = await CreateAdminUserAsync(db, "fallback-settings@example.com", "Резервный исполнитель");
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.UpdateSettings(new TaskJournalSettingsRequest
        {
            RestrictExecutorEditing = false,
            AllowExecutorDeadlineChangeRequests = false,
            RequireCompletionResult = false,
            CaseInactivityAutomationEnabled = false,
            CaseInactivityDays = 400,
            CaseInactivityLimit = 0,
            CaseInactivityDefaultAssigneeUserId = fallbackAssignee.Id,
            CaseInactivityEscalationDays = 7,
            CaseInactivityEscalationUserId = fallbackAssignee.Id,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalSettingsDto>(ok.Value);

        Assert.False(dto.RestrictExecutorEditing);
        Assert.False(dto.AllowExecutorDeadlineChangeRequests);
        Assert.False(dto.RequireCompletionResult);
        Assert.False(dto.CaseInactivityAutomationEnabled);
        Assert.Equal(365, dto.CaseInactivityDays);
        Assert.Equal(1, dto.CaseInactivityLimit);
        Assert.Equal(fallbackAssignee.Id, dto.CaseInactivityDefaultAssigneeUserId);
        Assert.Equal(365, dto.CaseInactivityEscalationDays);
        Assert.Equal(fallbackAssignee.Id, dto.CaseInactivityEscalationUserId);

        var stored = await db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key.StartsWith("tasks.automation.caseControl."))
            .ToDictionaryAsync(item => item.Key, item => item.Value);

        Assert.Equal("false", stored["tasks.automation.caseControl.enabled"]);
        Assert.Equal("365", stored["tasks.automation.caseControl.inactiveDays"]);
        Assert.Equal("1", stored["tasks.automation.caseControl.limit"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.automation.caseControl.defaultAssigneeUserId"]);
        Assert.Equal("365", stored["tasks.automation.caseControl.escalationDays"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.automation.caseControl.escalationUserId"]);

        var requireCompletionResult = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.requireCompletionResult");
        Assert.Equal("false", requireCompletionResult.Value);
    }

    [Fact]
    public async Task GetCaseControlAutomationDiagnostics_ReturnsCurrentAutomationState()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-diagnostics@example.com", "Администратор");
        var fallbackAssignee = await CreateAdminUserAsync(db, "fallback-diagnostics@example.com", "Резервный исполнитель");
        var escalationUser = await CreateAdminUserAsync(db, "escalation-diagnostics@example.com", "Руководитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-45);
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "tasks.automation.caseControl.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.inactiveDays", Value = "14", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.limit", Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.defaultAssigneeUserId", Value = fallbackAssignee.Id.ToString(), CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.escalationDays", Value = "30", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.escalationUserId", Value = escalationUser.Id.ToString(), CreatedAt = now, UpdatedAt = now });
        db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "client-case",
            ObjectId = clientCase.Id,
            Action = "Automation",
            Description = "Создана контрольная задача по делу БАНКРОТСТВО.",
            MetadataJson = "{\"event\":\"caseInactivityControlTaskCreated\"}",
            CreatedAt = now.AddDays(-1),
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetCaseControlAutomationDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationDiagnosticsDto>(ok.Value);

        Assert.True(dto.Enabled);
        Assert.Equal(14, dto.InactivityDays);
        Assert.Equal(30, dto.EscalationDays);
        Assert.Equal(10, dto.Limit);
        Assert.Equal(fallbackAssignee.Id, dto.DefaultAssigneeUserId);
        Assert.Equal("Резервный исполнитель", dto.DefaultAssigneeName);
        Assert.Equal(escalationUser.Id, dto.EscalationUserId);
        Assert.Equal("Руководитель", dto.EscalationUserName);
        Assert.Equal(1, dto.VisibleCandidateCount);
        Assert.Equal(1, dto.VisibleEscalationCandidateCount);
        Assert.Equal(1, dto.CreatedTaskCountLast7Days);
        Assert.NotNull(dto.LastCreatedAt);
        Assert.Equal("Создана контрольная задача по делу БАНКРОТСТВО.", dto.LastCreatedDescription);
    }

    [Fact]
    public async Task CreateTask_AllowsInternalTaskWithoutClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            Title = "Проверить внутренний процесс",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.True(dto.IsInternal);
        Assert.Null(dto.ClientId);
        Assert.Null(dto.LegalEntityId);
        Assert.Null(dto.ClientCaseId);
        Assert.Equal("Внутренняя задача", dto.ObjectName);
    }

    [Fact]
    public async Task CreateTask_BindsClientAndClientCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Title = "Подготовить заявление",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("Иванов Иван Иванович", dto.ClientName);
        Assert.Equal("БАНКРОТСТВО", dto.ClientCaseTitle);
        Assert.Equal("Иванов Иван Иванович - БАНКРОТСТВО", dto.ObjectName);
    }

    [Fact]
    public async Task CreateTask_PersistsApprovalDueAt()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var approvalDueAt = DateTime.UtcNow.AddHours(8);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Подготовить заявление",
            DueAt = DateTime.UtcNow.AddHours(2),
            ApprovalDueAt = approvalDueAt,
            Status = "onApproval",
            Participants =
            [
                new TaskJournalParticipantRequest
                {
                    UserId = approver.Id,
                    Role = "approver",
                    Name = approver.FullName,
                },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.NotNull(dto.ApprovalDueAt);
        Assert.Equal(approvalDueAt, await db.TaskJournalItems
            .Where(item => item.Id == dto.Id)
            .Select(item => item.ApprovalDueAt)
            .SingleAsync());
    }

    [Fact]
    public async Task CreateTask_BindsLegalEntityAndLegalEntityCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var legalEntity = await CreateLegalEntityAsync(db, "ООО Ромашка");
        var clientCase = await CreateLegalEntityCaseAsync(db, legalEntity.Id, "КОРПОРАТИВНЫЙ СПОР");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            LegalEntityId = legalEntity.Id,
            ClientCaseId = clientCase.Id,
            Title = "Проверить документы юрлица",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Null(dto.ClientId);
        Assert.Equal(legalEntity.Id, dto.LegalEntityId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("ООО Ромашка", dto.LegalEntityName);
        Assert.Equal("КОРПОРАТИВНЫЙ СПОР", dto.ClientCaseTitle);
        Assert.Equal("ООО Ромашка - КОРПОРАТИВНЫЙ СПОР", dto.ObjectName);
    }

    [Fact]
    public async Task CreateTask_NormalizesPlannedDuration()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Подготовить заявление",
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 47,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(45, dto.PlannedDurationMinutes);
        Assert.Equal(45, await db.TaskJournalItems
            .Where(item => item.Id == dto.Id)
            .Select(item => item.PlannedDurationMinutes)
            .SingleAsync());
    }

    [Fact]
    public async Task CreateTask_RejectsClientCaseFromAnotherClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var otherClient = await CreateClientAsync(db, "Петров Петр Петрович");
        var otherClientCase = await CreateCaseAsync(db, otherClient.Id, "СЕМЕЙНЫЙ СПОР");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = otherClientCase.Id,
            Title = "Подготовить заявление",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("не относится", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatus_DoneWithoutResultRequiresResult()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Петров Петр Петрович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, result: null);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("результат", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task UpdateTask_DoneWithoutResultRequiresResult()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Петров Петр Петрович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, result: null);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Закрыть задачу",
            DueAt = DateTime.UtcNow.AddHours(2),
            Status = "done",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("результат", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task UpdateStatus_DoneWithResult_NotifiesAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Сидоров Семен Семенович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: null);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, assignee);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Документы подготовлены и переданы клиенту.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Equal("done", dto.Status);
        Assert.Equal("Документы подготовлены и переданы клиенту.", dto.Result);
        Assert.Single(notificationService.CompletedTaskIds, task.Id);
    }

    [Fact]
    public async Task UpdateTask_WithChangeComment_AddsCommentAndNotification()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Сидоров Семен Семенович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Проверить определения",
            DueAt = task.DueAt.AddHours(2),
            Status = "inProgress",
            ChangeComment = "Поменяли срок и передали в работу.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<TaskJournalItemDto>(ok.Value);

        var comments = await db.TaskJournalComments
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == task.Id)
            .ToListAsync();
        Assert.Contains(comments, item => item.Kind == "comment" && item.Text == "Поменяли срок и передали в работу.");
        var notification = Assert.Single(notificationService.ChangedTasks);
        Assert.Equal(task.Id, notification.TaskId);
        Assert.Equal("Поменяли срок и передали в работу.", notification.Comment);
        Assert.Contains(notification.ChangeLabels, item => item.Contains("Наименование", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notification.ChangeLabels, item => item.Contains("Срок:", StringComparison.OrdinalIgnoreCase) && item.Contains("->", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notification.ChangeLabels, item => item.Contains("Статус:", StringComparison.OrdinalIgnoreCase) && item.Contains("В работе", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateTask_RestrictsExecutorFullEditWhenSettingEnabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "assignee@example.com", "Исполнитель", canEdit: true);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Изменить задачу",
            DueAt = originalDueAt.AddDays(1),
            Status = "new",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(task.Title, stored.Title);
        Assert.Equal(originalDueAt, stored.DueAt);
    }

    [Fact]
    public async Task UpdateTask_ForbidsObserverWithOwnScopeEvenWhenRoleCanEdit()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Author");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Assignee");
        var observer = await CreateTaskUserAsync(db, "observer@example.com", "Observer", canEdit: true);
        var client = await CreateClientAsync(db, "Client");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = observer.Id,
            Role = "observer",
            DisplayName = observer.FullName,
            Email = observer.Email,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), observer);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Changed by observer",
            DueAt = task.DueAt.AddDays(1),
            Status = "new",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(task.Title, stored.Title);
        Assert.Equal(task.DueAt, stored.DueAt);
    }

    [Fact]
    public async Task UpdateStatus_ForbidsObserverWithOwnScopeEvenWhenRoleCanEdit()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Author");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Assignee");
        var observer = await CreateTaskUserAsync(db, "observer@example.com", "Observer", canEdit: true);
        var client = await CreateClientAsync(db, "Client");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = observer.Id,
            Role = "observer",
            DisplayName = observer.FullName,
            Email = observer.Email,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), observer);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Observer result",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task UpdateStatus_AllowsExecutorToSaveResultWithoutEditPermission()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: null);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Документы переданы постановщику.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Equal("done", dto.Status);
        Assert.Equal("Документы переданы постановщику.", dto.Result);
    }

    [Fact]
    public async Task CreateDeadlineChangeRequest_AddsHistoryAndKeepsDueAt()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var requestedDueAt = originalDueAt.AddDays(2);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = requestedDueAt,
            Reason = "Жду документы от клиента.",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var comment = Assert.IsType<TaskJournalCommentDto>(created.Value);

        Assert.Equal("system", comment.Kind);
        Assert.Contains("запросил перенос срока", comment.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Жду документы от клиента", comment.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalDueAt, await db.TaskJournalItems
            .Where(item => item.Id == task.Id)
            .Select(item => item.DueAt)
            .SingleAsync());
    }

    [Fact]
    public async Task GetTasks_SearchesByClientAndClientCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "bankruptcy");
        var task = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: clientCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: "bankruptcy",
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(task.Id, dto.Id);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
    }

    [Fact]
    public async Task GetTasks_SearchByClientNameDoesNotMatchHiddenTaskFields()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-search@example.com", "Автор");
        var egorova = await CreateClientAsync(db, "Егорова Татьяна Евгеньевна");
        var egorovaCase = await CreateCaseAsync(db, egorova.Id, "ЕГОРОВА - дом и земля");
        var expectedTask = await CreateTaskAsync(db, author.Id, egorova.Id, clientCaseId: egorovaCase.Id);
        expectedTask.Title = "ЕГОРОВА - счет 94";

        var voyakina = await CreateClientAsync(db, "Воякина Елена Михайловна");
        var voyakinaCase = await CreateCaseAsync(db, voyakina.Id, "ВОЯКИНА - оформление дома");
        var hiddenMatchTask = await CreateTaskAsync(db, author.Id, voyakina.Id, clientCaseId: voyakinaCase.Id, result: "Егорова упомянута во внутреннем результате");
        hiddenMatchTask.Title = "ВОЯКИНА - решить вопрос по заключению договора";
        hiddenMatchTask.AssigneeName = "Егорова Татьяна";
        hiddenMatchTask.AuthorName = "Егорова Татьяна";
        hiddenMatchTask.TagsJson = "[\"Егорова\"]";
        hiddenMatchTask.Participants.Add(new TaskJournalParticipant
        {
            Role = "observer",
            DisplayName = "Егорова Татьяна",
            Email = "egorova@example.com",
            ApprovalStatus = "pending",
            Resolution = "Егорова в резолюции",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: "ЕГОРОВА",
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(expectedTask.Id, dto.Id);
        Assert.Equal(egorova.Id, dto.ClientId);
        Assert.Equal(egorovaCase.Id, dto.ClientCaseId);
    }

    [Fact]
    public async Task GetTasks_FiltersByClientCaseId()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var firstCase = await CreateCaseAsync(db, client.Id, "Первое дело");
        var secondCase = await CreateCaseAsync(db, client.Id, "Второе дело");
        var expectedTask = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: firstCase.Id);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: secondCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            clientCaseId: firstCase.Id,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(expectedTask.Id, dto.Id);
        Assert.Equal(firstCase.Id, dto.ClientCaseId);
    }

    [Fact]
    public async Task UpdateControl_MarksTaskAndAddsManagerAsObserver()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "control-author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "control-assignee@example.com", "Исполнитель");
        var manager = await CreateAdminUserAsync(db, "control-manager@example.com", "Руководитель");
        var client = await CreateClientAsync(db, "Клиент на контроле");
        var clientCase = await CreateCaseAsync(db, client.Id, "Дело на контроле");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, clientCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), manager);

        var result = await controller.UpdateControl(task.Id, new UpdateTaskJournalControlRequest
        {
            IsUnderControl = true,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.True(dto.IsUnderControl);
        Assert.Equal(manager.Id, dto.ControlMarkedByUserId);
        Assert.Equal("Руководитель", dto.ControlMarkedByUserName);

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.Id == task.Id);
        Assert.True(stored.IsUnderControl);
        Assert.Contains(stored.Participants, participant => participant.UserId == manager.Id && participant.Role == "observer");

        var filteredResult = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            priority: null,
            underControl: true,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var filteredOk = Assert.IsType<OkObjectResult>(filteredResult.Result);
        var filteredPage = Assert.IsType<PagedResponse<TaskJournalItemDto>>(filteredOk.Value);
        Assert.Equal(task.Id, Assert.Single(filteredPage.Items).Id);
    }

    [Fact]
    public async Task GetTasks_SearchesByLegalEntityAndLegalEntityCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var legalEntity = await CreateLegalEntityAsync(db, "ООО Ромашка");
        legalEntity.Inn = "5400000000";
        var clientCase = await CreateLegalEntityCaseAsync(db, legalEntity.Id, "corporate dispute");
        var task = await CreateTaskAsync(
            db,
            author.Id,
            clientId: null,
            clientCaseId: clientCase.Id,
            legalEntityId: legalEntity.Id);
        var otherClient = await CreateClientAsync(db, "Иванов Иван Иванович");
        await CreateTaskAsync(db, author.Id, otherClient.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        foreach (var searchTerm in new[] { "5400000000", "corporate" })
        {
            var result = await controller.GetTasks(
                search: searchTerm,
                status: null,
                type: null,
                assigneeUserId: null,
                from: null,
                to: null,
                page: 1,
                pageSize: 50,
                ct: CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
            var dto = Assert.Single(page.Items);

            Assert.Equal(task.Id, dto.Id);
            Assert.Null(dto.ClientId);
            Assert.Equal(legalEntity.Id, dto.LegalEntityId);
            Assert.Equal(clientCase.Id, dto.ClientCaseId);
        }
    }

    [Fact]
    public async Task GetTasks_MapsLegacyApprovedApprovalQueueToApprovedStatus()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: "Согласовано.");
        task.Status = "inProgress";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "approved",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(task.Id, dto.Id);
        Assert.Equal("approved", dto.Status);
    }

    [Fact]
    public async Task GetTasks_MapsExpiredApprovalQueueToOverdueApprovalStatus()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval");
        task.DueAt = DateTime.UtcNow.AddHours(-2);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(task.Id, dto.Id);
        Assert.Equal("overdueApproval", dto.Status);
    }

    [Fact]
    public async Task GetTasks_UsesApprovalDueAtForApprovalOverdueStatus()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var approvalDueAt = DateTime.UtcNow.AddHours(2);
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval", approvalDueAt: approvalDueAt);
        task.DueAt = DateTime.UtcNow.AddHours(-2);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items);

        Assert.Equal(task.Id, dto.Id);
        Assert.Equal("onApproval", dto.Status);
        Assert.NotNull(dto.ApprovalDueAt);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_ReturnsOpenCaseWithoutActiveTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var completedTask = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: clientCase.Id,
            result: "Готово",
            status: "done");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE TaskJournalItems SET CreatedAt = {DateTime.UtcNow.AddDays(-20)}, UpdatedAt = NULL WHERE Id = {completedTask.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ClientCases SET CreatedAt = {DateTime.UtcNow.AddDays(-21)} WHERE Id = {clientCase.Id}");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(inactiveDays: 14, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        var candidate = Assert.Single(candidates);

        Assert.Equal(client.Id, candidate.ClientId);
        Assert.Equal(clientCase.Id, candidate.ClientCaseId);
        Assert.Equal("Проверить движение по делу", candidate.SuggestedTitle);
        Assert.Equal(0, candidate.ActiveTaskCount);
        Assert.Equal(1, candidate.CompletedTaskCount);
        Assert.True(candidate.DaysWithoutActiveTask >= 14);
        Assert.Contains("Нет активной задачи", candidate.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_AppliesCaseOwnerVisibility()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "author@example.com", "Автор", canEdit: false);
        var otherResponsible = await CreateAdminUserAsync(db, "other@example.com", "Другой ответственный");
        var visibleClient = await CreateClientAsync(db, "Видимый клиент");
        visibleClient.ResponsibleUserId = author.Id;
        var hiddenClient = await CreateClientAsync(db, "Скрытый клиент");
        hiddenClient.ResponsibleUserId = otherResponsible.Id;
        var visibleCase = await CreateCaseAsync(db, visibleClient.Id, "ВИДИМОЕ ДЕЛО");
        var hiddenCase = await CreateCaseAsync(db, hiddenClient.Id, "СКРЫТОЕ ДЕЛО");
        visibleCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        hiddenCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(inactiveDays: 14, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        var candidate = Assert.Single(candidates);

        Assert.Equal(visibleCase.Id, candidate.ClientCaseId);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == hiddenCase.Id);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_SkipsCasesWithActiveTaskAndFinishedCases()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        var closedCase = await CreateCaseAsync(db, client.Id, "ЗАКРЫТОЕ ДЕЛО", ClientCaseStatus.Closed);
        var lostCase = await CreateCaseAsync(db, client.Id, "ПРОИГРАННОЕ ДЕЛО", ClientCaseStatus.Lost);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: activeCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(inactiveDays: 14, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);

        Assert.DoesNotContain(candidates, item => item.ClientCaseId == activeCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == closedCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == lostCase.Id);
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_CreatesControlTaskWithCaseBinding()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("control", dto.Type);
        Assert.Equal("Проверить движение по делу", dto.Title);
        Assert.Equal(author.Id, dto.AssigneeUserId);
        Assert.Contains("автоконтроль", dto.Tags);

        var comments = await db.TaskJournalComments
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == dto.Id)
            .ToListAsync();
        Assert.Contains(comments, item => item.Kind == "system" && item.Text.Contains("автоконтроля дела", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_AssignsResponsibleUserOfCaseOwner()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var responsible = await CreateAdminUserAsync(db, "responsible@example.com", "Ответственный");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = responsible.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(responsible.Id, dto.AssigneeUserId);
        Assert.Equal(responsible.FullName, dto.AssigneeName);
        Assert.Equal(author.Id, dto.AuthorUserId);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == responsible.Id);
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_PrefersCaseResponsibleOverOwnerAndFallback()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var ownerResponsible = await CreateAdminUserAsync(db, "owner@example.com", "Ответственный клиента");
        var caseResponsible = await CreateAdminUserAsync(db, "case@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = ownerResponsible.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.ResponsibleUserId = caseResponsible.Id;
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(caseResponsible.Id, dto.AssigneeUserId);
        Assert.Equal(caseResponsible.FullName, dto.AssigneeName);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == caseResponsible.Id);
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_ReturnsNotFoundForHiddenCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "author@example.com", "Автор", canEdit: false, canCreate: true);
        var otherResponsible = await CreateAdminUserAsync(db, "other@example.com", "Другой ответственный");
        var hiddenClient = await CreateClientAsync(db, "Скрытый клиент");
        hiddenClient.ResponsibleUserId = otherResponsible.Id;
        var hiddenCase = await CreateCaseAsync(db, hiddenClient.Id, "СКРЫТОЕ ДЕЛО");
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(hiddenCase.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(await db.TaskJournalItems.ToListAsync());
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_DoesNotCreateDuplicateWhenActiveTaskExists()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: clientCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task CreateTask_AllowsManualCaseTaskWhenAutomationControlIsNotRequired()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var automationResult = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(automationResult.Result);

        var manualResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Title = "Manual case task",
            DueAt = DateTime.UtcNow.AddDays(1),
            AssigneeUserId = author.Id,
            PlannedDurationMinutes = 60,
            Type = "task",
            Priority = "normal",
            Status = "new",
            Reminders = ["atDue"],
            Channels = ["inApp"],
            Tags = ["case"],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(manualResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("task", dto.Type);
        Assert.Equal("Manual case task", dto.Title);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Theory]
    [InlineData("courtHearing")]
    [InlineData("meeting")]
    public async Task CreateTask_AllowsAdditionalTaskTypes(string taskType)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-types@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = author.Id,
            Title = "Новая задача с типом",
            DueAt = DateTime.UtcNow.AddHours(2),
            Type = taskType,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        var task = await db.TaskJournalItems.SingleAsync(item => item.Id == dto.Id);

        Assert.Equal(taskType, dto.Type);
        Assert.Equal(taskType, task.Type);
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_CreatesTasksForCasesWithoutActiveTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var firstCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var secondCase = await CreateCaseAsync(db, client.Id, "СЕМЕЙНЫЙ СПОР");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        firstCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        secondCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: activeCase.Id);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(ok.Value);

        Assert.Equal(2, dto.CreatedTasks.Count);
        Assert.Contains(dto.CreatedTasks, item => item.ClientCaseId == firstCase.Id);
        Assert.Contains(dto.CreatedTasks, item => item.ClientCaseId == secondCase.Id);
        Assert.DoesNotContain(dto.CreatedTasks, item => item.ClientCaseId == activeCase.Id);
        Assert.Equal(3, await db.TaskJournalItems.CountAsync());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_CreatesControlTaskForInactiveCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });
        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        var task = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(client.Id, task.ClientId);
        Assert.Equal("control", task.Type);
        Assert.Equal("Проверить движение по делу", task.Title);
        Assert.Equal(author.Id, task.AssigneeUserId);
        Assert.Contains(task.Participants, item => item.Role == "assignee" && item.UserId == author.Id);
        Assert.Contains(task.Comments, item => item.Kind == "system" && item.Text.Contains("фоновым автоконтролем", StringComparison.OrdinalIgnoreCase));

        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item =>
            item.ObjectType == "client-case" &&
            item.ObjectId == clientCase.Id &&
            item.Action == "Automation");
        Assert.Contains("Автоконтроль создал задачу", auditEvent.Description ?? string.Empty);
        Assert.Contains(task.Id.ToString(), auditEvent.MetadataJson ?? string.Empty);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_EscalatesLongInactiveCase()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "case-responsible@example.com", "Ответственный дела");
        var escalationUser = await CreateAdminUserAsync(db, "escalation@example.com", "Руководитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-45);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:EscalationUserId"] = escalationUser.Id.ToString(),
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        var task = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("urgent", task.Priority);
        Assert.Equal(assignee.Id, task.AssigneeUserId);
        Assert.Contains(task.Participants, item => item.Role == "observer" && item.UserId == escalationUser.Id);

        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item =>
            item.ObjectType == "client-case" &&
            item.ObjectId == clientCase.Id &&
            item.Action == "Automation");
        Assert.Contains("\"isEscalated\":true", auditEvent.MetadataJson ?? string.Empty);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_RepeatsEscalationForOpenControlTask()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "repeat-escalation-assignee@example.com", "Ответственный дела");
        var escalationUser = await CreateAdminUserAsync(db, "repeat-escalation-manager@example.com", "Руководитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-40);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            assigneeUserId: assignee.Id,
            clientCaseId: clientCase.Id,
            status: "new",
            createdAt: now.AddDays(-3));
        task.Type = "control";
        task.Title = "Проверить движение по делу";
        task.Priority = "normal";
        task.TagsJson = "[\"автоконтроль\"]";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = assignee.Id,
            Role = "assignee",
            DisplayName = assignee.FullName,
            Email = assignee.Email,
            ApprovalStatus = "pending",
            CreatedAt = now.AddDays(-3),
            UpdatedAt = now.AddDays(-3),
        });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:EscalationUserId"] = escalationUser.Id.ToString(),
            ["TaskJournal:Automation:Limit"] = "10",
        }, notifications);

        using (provider)
        {
            var firstRun = await scheduler.RunOnceAsync(CancellationToken.None);
            var secondRun = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, firstRun.CreatedCount);
            Assert.Equal(1, firstRun.EscalatedCount);
            Assert.Equal(0, secondRun.CreatedCount);
            Assert.Equal(0, secondRun.EscalatedCount);
            Assert.Null(firstRun.Error);
            Assert.Null(secondRun.Error);
        }

        var updatedTask = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal("urgent", updatedTask.Priority);
        Assert.Contains(updatedTask.Participants, item => item.Role == "observer" && item.UserId == escalationUser.Id);
        Assert.Single(updatedTask.Comments, item =>
            item.Kind == "system" &&
            item.MetadataJson.Contains("caseInactivityControlEscalation") &&
            item.MetadataJson.Contains("\"offsetDays\":2"));
        var changedTask = Assert.Single(notifications.ChangedTasks);
        Assert.Equal(task.Id, changedTask.TaskId);
        Assert.Contains("Повторная эскалация автоконтроля", changedTask.ChangeLabels.Single());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsPausedAndApprovedTaskCases()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "paused-approved-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var pausedCase = await CreateCaseAsync(db, client.Id, "ПРИОСТАНОВЛЕННОЕ ДЕЛО", ClientCaseStatus.OnHold);
        var approvedTaskCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С СОГЛАСОВАННОЙ ЗАДАЧЕЙ");
        pausedCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        approvedTaskCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: approvedTaskCase.Id, status: "approved", createdAt: DateTime.UtcNow.AddDays(-5));
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
        }

        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == pausedCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == approvedTaskCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_TreatsDocumentsCourtEventsAndCommentsAsCaseActivity()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "activity-signals-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var documentCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С ДОКУМЕНТОМ");
        var courtEventCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С ЗАСЕДАНИЕМ");
        var commentCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С КОММЕНТАРИЕМ");
        var staleCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ДВИЖЕНИЯ");
        var now = DateTime.UtcNow;
        documentCase.CreatedAt = now.AddDays(-30);
        courtEventCase.CreatedAt = now.AddDays(-30);
        commentCase.CreatedAt = now.AddDays(-30);
        staleCase.CreatedAt = now.AddDays(-30);
        db.ClientCaseDocuments.Add(new ClientCaseDocument
        {
            ClientCaseId = documentCase.Id,
            DocumentKey = "doc-activity",
            Version = 1,
            DocumentType = "other",
            Status = "draft",
            OriginalFileName = "activity.pdf",
            StoredFileName = "activity.pdf",
            StoragePath = "activity.pdf",
            ContentType = "application/pdf",
            SizeBytes = 12,
            UploadedByUserId = author.Id,
            UploadedByName = author.FullName,
            UploadedAt = now.AddDays(-1),
        });
        db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = courtEventCase.Id,
            EventType = "hearing",
            Title = "Судебное заседание",
            ScheduledAt = now.AddDays(7),
            CourtInstance = "first",
            MovementStatus = "scheduled",
            SourceType = "manual",
            CreatedAt = now.AddDays(-1),
            CreatedByUserId = author.Id,
            CreatedByUserName = author.FullName,
        });
        var completedTask = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: commentCase.Id,
            status: "done",
            createdAt: now.AddDays(-20));
        db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = completedTask.Id,
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            Kind = "comment",
            Text = "Уточнили позицию по делу.",
            MetadataJson = "{}",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1),
        });
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == documentCase.Id));
        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == courtEventCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == commentCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == staleCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_NotifiesAssigneeAboutInactiveCaseControlTask()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "inactive-notify@example.com", "Ответственный");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        }, notifications);

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CreatedCount);
            Assert.Null(result.Error);
        }

        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        var notification = Assert.Single(notifications.CaseInactivityControls);
        Assert.Equal(task.Id, notification.TaskId);
        Assert.True(notification.DaysWithoutActiveTask >= 14);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_PrefersCaseResponsibleOverDefaultAssignee()
    {
        await using var db = await CreateContextAsync();
        var defaultAssignee = await CreateAdminUserAsync(db, "gordik@example.com", "Гордик Сергей");
        var ownerResponsible = await CreateAdminUserAsync(db, "owner-responsible@example.com", "Ответственный клиента");
        var responsible = await CreateAdminUserAsync(db, "responsible@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = ownerResponsible.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.ResponsibleUserId = responsible.Id;
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
            ["TaskJournal:Automation:DefaultAssigneeUserId"] = defaultAssignee.Id.ToString(),
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CreatedCount);
            Assert.Null(result.Error);
        }

        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(responsible.Id, task.AssigneeUserId);
        Assert.Equal(responsible.FullName, task.AssigneeName);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_UsesAssigneeTimeZoneForDueAt()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "samara-responsible@example.com", "Ответственный Самара");
        var client = await CreateClientAsync(db, "Клиент Самара");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "Контроль времени");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CreatedCount);
            Assert.Null(result.Error);
        }

        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        var assigneeTimeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var createdLocal = CalendarTimeZoneHelper.ToUserLocal(task.CreatedAt, assigneeTimeZone);
        var dueLocal = CalendarTimeZoneHelper.ToUserLocal(task.DueAt, assigneeTimeZone);
        Assert.Equal(createdLocal.Date.AddDays(1).AddHours(9), dueLocal);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_DoesNotRecreateRecentlyDeletedControlTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var task = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: clientCase.Id, createdAt: DateTime.UtcNow.AddDays(-20));
        task.TagsJson = "[\"автоконтроль\"]";
        db.TaskJournalItems.Remove(task);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
        }

        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.IgnoreQueryFilters().CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsRecentlyViewedCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        clientCase.LastViewedAt = DateTime.UtcNow.AddHours(-2);
        clientCase.LastViewedByUserId = author.Id;
        clientCase.LastViewedByUserName = author.FullName;
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
        }

        Assert.Empty(await db.TaskJournalItems.Where(item => item.ClientCaseId == clientCase.Id).ToListAsync());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsFreshAndAlreadyControlledCases()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        client.ResponsibleUserId = author.Id;
        var oldCase = await CreateCaseAsync(db, client.Id, "СТАРОЕ ДЕЛО");
        var freshCase = await CreateCaseAsync(db, client.Id, "НОВОЕ ДЕЛО");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        var lostCase = await CreateCaseAsync(db, client.Id, "ПРОИГРАННОЕ ДЕЛО", ClientCaseStatus.Lost);
        oldCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        activeCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        lostCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: activeCase.Id);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var firstRun = await scheduler.RunOnceAsync(CancellationToken.None);
            var secondRun = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, firstRun.CreatedCount);
            Assert.Equal(0, secondRun.CreatedCount);
            Assert.Null(firstRun.Error);
            Assert.Null(secondRun.Error);
        }

        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == oldCase.Id));
        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == freshCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == activeCase.Id));
        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == lostCase.Id));
    }

    [Fact]
    public async Task ExportCalendar_ReturnsIcsWithTaskDuration()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id);
        task.Title = "Проверить документы";
        task.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Unspecified);
        task.PlannedDurationMinutes = 90;
        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientId: null,
            legalEntityId: null,
            reminderMinutes: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Equal("text/calendar; charset=utf-8", file.ContentType);
        Assert.Contains("BEGIN:VCALENDAR", content);
        Assert.Contains("SUMMARY:Проверить документы", content);
        Assert.Contains("DTSTART:20260510T090000Z", content);
        Assert.Contains("DTEND:20260510T103000Z", content);
        Assert.Contains("BEGIN:VALARM", content);
        Assert.Contains("DESCRIPTION:Напоминание: Проверить документы", content);
        Assert.Contains("TRIGGER:-PT1H", content);
        Assert.Contains("TRIGGER:-PT15M", content);

        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == CalendarExportAudit.ObjectType);
        Assert.Equal(CalendarExportAudit.ExportedAction, auditEvent.Action);
        Assert.Equal(author.Id, auditEvent.ActorUserId);
        Assert.Contains("ICS-календарь задач", auditEvent.Description);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        Assert.Equal("tasks", metadata.RootElement.GetProperty("calendarKind").GetString());
        Assert.Equal(1, metadata.RootElement.GetProperty("exportedEventCount").GetInt32());
        Assert.Equal(60, metadata.RootElement.GetProperty("reminderMinutes")[0].GetInt32());
        Assert.Equal(15, metadata.RootElement.GetProperty("reminderMinutes")[1].GetInt32());
    }

    [Fact]
    public async Task ExportCalendar_FiltersTasksByClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-client-ics@example.com", "Автор");
        var firstClient = await CreateClientAsync(db, "Клиент календаря");
        var secondClient = await CreateClientAsync(db, "Другой клиент календаря");
        var firstTask = await CreateTaskAsync(db, author.Id, firstClient.Id);
        firstTask.Title = "Задача выбранного клиента";
        firstTask.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var secondTask = await CreateTaskAsync(db, author.Id, secondClient.Id);
        secondTask.Title = "Задача другого клиента";
        secondTask.DueAt = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientId: firstClient.Id,
            legalEntityId: null,
            reminderMinutes: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("SUMMARY:Задача выбранного клиента", content);
        Assert.DoesNotContain("SUMMARY:Задача другого клиента", content);

        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == CalendarExportAudit.ObjectType);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        Assert.Equal(1, metadata.RootElement.GetProperty("exportedEventCount").GetInt32());
    }

    [Fact]
    public async Task ExportCalendar_UsesCustomReminderMinutes()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-custom-ics@example.com", "Автор");
        var client = await CreateClientAsync(db, "Календарный клиент");
        var task = await CreateTaskAsync(db, author.Id, client.Id);
        task.Title = "Настроить календарь";
        task.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientId: null,
            legalEntityId: null,
            reminderMinutes: "120,30,0,abc,120",
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("TRIGGER:-PT2H", content);
        Assert.Contains("TRIGGER:-PT30M", content);
        Assert.DoesNotContain("TRIGGER:-PT1H", content);
        Assert.DoesNotContain("TRIGGER:-PT15M", content);
    }

    [Fact]
    public async Task ExportCalendar_ReturnsForbidWhenTaskExportIsDenied()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateTaskUserAsync(
            db,
            "task-export-denied@example.com",
            "Пользователь без календаря",
            canEdit: false,
            canExport: false);
        var controller = CreateController(db, new RecordingNotificationService(), user);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientId: null,
            legalEntityId: null,
            reminderMinutes: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task ExportPlanningReport_ReturnsHtmlGroupedByClientAndCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Кирпита Наталья Андреевна");
        var clientCase = await CreateCaseAsync(db, client.Id, "Взыскание долга");
        var task = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: clientCase.Id, result: "На контроль");
        task.Title = "Проверить движение по делу";
        task.Description = "Сверить карточку суда и зафиксировать результат.";
        task.DueAt = DateTime.UtcNow.AddDays(-1);
        task.Status = "waiting";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportPlanningReport(
            format: "html",
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Equal("text/html; charset=utf-8", file.ContentType);
        Assert.Contains("ПЛАНИНГ ПО КЛИЕНТАМ", content);
        Assert.Contains("КЛИЕНТ | Кирпита Наталья Андреевна", content);
        Assert.Contains("ДЕЛО | Взыскание долга", content);
        Assert.Contains("Проверить движение по делу", content);
        Assert.Contains("Ожидает", content);
        Assert.Contains("Просрочена", content);
        Assert.Contains("На контроль", content);
    }

    [Fact]
    public async Task ExportPlanningReport_UsesProfileTimeZoneForDatesAndFileName()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "planning-timezone@example.com", "Автор");
        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
        });
        var client = await CreateClientAsync(db, "Самарский клиент");
        var dueAtUtc = DateTime.SpecifyKind(new DateTime(2026, 6, 4, 22, 30, 0), DateTimeKind.Utc);
        var task = await CreateTaskAsync(db, author.Id, client.Id);
        task.Title = "Проверить срок по часовому поясу";
        task.DueAt = dueAtUtc;
        await db.SaveChangesAsync();

        var controller = CreateController(db, new RecordingNotificationService(), author);
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone("Europe/Samara");
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        var expectedDueAt = CalendarTimeZoneHelper.ToUserLocal(dueAtUtc, timeZone).ToString("dd.MM.yyyy HH:mm", culture);
        var beforeGeneratedAt = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).ToString("dd.MM.yyyy HH:mm", culture);
        var beforeFileStamp = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).ToString("yyyyMMdd-HHmm");

        var result = await controller.ExportPlanningReport(
            format: "html",
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            priority: null,
            ct: CancellationToken.None);

        var afterGeneratedAt = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).ToString("dd.MM.yyyy HH:mm", culture);
        var afterFileStamp = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).ToString("yyyyMMdd-HHmm");
        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains(expectedDueAt, content);
        Assert.True(
            content.Contains(beforeGeneratedAt, StringComparison.Ordinal) ||
            content.Contains(afterGeneratedAt, StringComparison.Ordinal),
            $"Planning report must contain generated time in Europe/Samara. Expected {beforeGeneratedAt} or {afterGeneratedAt}. Content: {content}");
        Assert.True(
            file.FileDownloadName == $"task-planning-report-{beforeFileStamp}.html" ||
            file.FileDownloadName == $"task-planning-report-{afterFileStamp}.html",
            $"Planning report file name must use Europe/Samara local time. Expected {beforeFileStamp} or {afterFileStamp}. Actual: {file.FileDownloadName}");
    }

    [Fact]
    public async Task ExportPlanningReport_ReturnsWordFile()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        await CreateTaskAsync(db, author.Id, client.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportPlanningReport(
            format: "word",
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);

        Assert.Equal("application/msword", file.ContentType);
        Assert.EndsWith(".doc", file.FileDownloadName);
        var preamble = Encoding.UTF8.GetPreamble();
        Assert.True(file.FileContents.Take(preamble.Length).SequenceEqual(preamble));
    }

    [Fact]
    public async Task ExportPlanningReport_ReturnsPdfThroughPdfService()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        await CreateTaskAsync(db, author.Id, client.Id);
        var pdfService = new FakeInvoiceDocumentPdfService([1, 2, 3]);
        var controller = CreateController(db, new RecordingNotificationService(), author, pdfService);

        var result = await controller.ExportPlanningReport(
            format: "pdf",
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);

        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal([1, 2, 3], file.FileContents);
        Assert.Contains("ПЛАНИНГ ПО КЛИЕНТАМ", pdfService.LastHtml);
    }

    [Fact]
    public async Task ExportPlanningReport_ReturnsForbidWhenTaskExportIsDenied()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateTaskUserAsync(
            db,
            "task-report-denied@example.com",
            "Пользователь без экспорта",
            canEdit: false,
            canExport: false);
        var controller = CreateController(db, new RecordingNotificationService(), user);

        var result = await controller.ExportPlanningReport(
            format: "html",
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetNotifications_IncludesCompletedTaskForAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Сидоров Семен Семенович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: "Клиент уведомлен.");
        task.Status = "done";
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetNotifications(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var notifications = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalNotificationDto>>(ok.Value);
        Assert.Contains(notifications, notification =>
            notification.TaskId == task.Id
            && notification.Title == "Задача выполнена"
            && notification.Tone == "blue");
    }

    [Fact]
    public async Task CreateTask_CreatesParticipantsAndPrimaryAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var observer = await CreateAdminUserAsync(db, "observer@example.com", "Наблюдатель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Подготовить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
            Participants =
            [
                new() { UserId = approver.Id, Role = "approver", SortOrder = 0 },
                new() { UserId = observer.Id, Role = "observer", SortOrder = 0 },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(assignee.Id, dto.AssigneeUserId);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == assignee.Id);
        Assert.Contains(dto.Participants, item => item.Role == "approver" && item.UserId == approver.Id);
        Assert.Contains(dto.Participants, item => item.Role == "observer" && item.UserId == observer.Id);
        Assert.Equal([dto.Id], notifications.AssignedTaskIds);
    }

    [Fact]
    public async Task CreateTask_WithContractorPayment_CreatesPendingExpensePayment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "contractor@example.com", "Привлеченный исполнитель", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var dueDate = new DateTime(2026, 6, 1, 14, 30, 0, DateTimeKind.Utc);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            AssigneeUserId = assignee.Id,
            Title = "Подготовить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 12_500.555m,
            ContractorPaymentDueDate = dueDate,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.True(dto.ContractorPaymentEnabled);
        Assert.Equal(12_500.56m, dto.ContractorPaymentAmount);
        Assert.NotNull(dto.ContractorPaymentId);
        Assert.Equal(PaymentStatus.Pending.ToString(), dto.ContractorPaymentStatus);

        var task = await db.TaskJournalItems
            .Include(item => item.ContractorPayment!)
                .ThenInclude(payment => payment.Responsibles)
            .SingleAsync(item => item.Id == dto.Id);
        var payment = Assert.IsType<Payment>(task.ContractorPayment);

        Assert.Equal(dto.ContractorPaymentId, payment.Id);
        Assert.Equal(PaymentType.Expense, payment.Type);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(12_500.56m, payment.Amount);
        Assert.Equal(dueDate.Date, payment.Date);
        Assert.Equal(dueDate.Date, payment.PlannedDate);
        Assert.Equal(client.Id, payment.ExpenseClientId);
        Assert.Equal(clientCase.Id, payment.ClientCaseId);
        Assert.Equal("Привлеченный исполнитель", payment.CounterpartyName);
        Assert.Contains($"TaskContractorPayment]:{dto.Id}", payment.SystemNotes, StringComparison.Ordinal);
        Assert.Contains(payment.Responsibles, responsible => responsible.UserId == assignee.Id);
    }

    [Fact]
    public async Task CreateTask_RejectsContractorPaymentForRegularAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Обычный исполнитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Подготовить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 5_000m,
            ContractorPaymentDueDate = DateTime.UtcNow.AddDays(3),
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("привлеченного исполнителя", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.Payments.ToListAsync());
    }

    [Fact]
    public async Task UpdateStatus_DoneCreatesContractorPaymentWhenPaymentOnResultEnabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "contractor-result@example.com", "Исполнитель по результату", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var createResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Выполнить поручение",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 7_000m,
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Null(createdDto.ContractorPaymentId);
        Assert.Empty(await db.Payments.ToListAsync());

        var statusResult = await controller.UpdateStatus(createdDto.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Поручение выполнено.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(statusResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("done", dto.Status);
        Assert.True(dto.ContractorPaymentOnResult);
        Assert.NotNull(dto.ContractorPaymentId);

        var payment = await db.Payments
            .Include(item => item.Responsibles)
            .SingleAsync();
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(7_000m, payment.Amount);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(5), payment.Date);
        Assert.Contains(payment.Responsibles, responsible => responsible.UserId == assignee.Id);
    }

    [Fact]
    public async Task UpdateStatus_DoneUsesProfileTimeZoneForContractorPaymentResultDate()
    {
        await using var db = await CreateContextAsync();
        var selectedZone = ResolveTimeZoneWithDifferentLocalDate();
        var author = await CreateAdminUserAsync(db, "author-timezone@example.com", "Автор с часовым поясом");
        var assignee = await CreateAdminUserAsync(db, "contractor-timezone@example.com", "Исполнитель с оплатой", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Клиент с задачей");
        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = selectedZone.TimeZoneId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, new RecordingNotificationService(), author);

        var createResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Выполнить работу с оплатой",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 9_000m,
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<TaskJournalItemDto>(created.Value);

        var statusResult = await controller.UpdateStatus(createdDto.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Работа выполнена.",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(statusResult.Result);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(selectedZone.ProfileToday.AddDays(5), payment.Date);
        Assert.Equal(selectedZone.ProfileToday.AddDays(5), payment.PlannedDate);
    }

    [Fact]
    public async Task CreateTask_RejectsContractorPaymentWhenExactDateAndResultDelayBothSet()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "contractor-conflict@example.com", "Привлеченный исполнитель", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Выполнить поручение",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 7_000m,
            ContractorPaymentDueDate = DateTime.UtcNow.AddDays(3),
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("либо точную дату оплаты", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.Payments.ToListAsync());
    }

    [Fact]
    public async Task UpdateStatus_InProgressDoesNotCreateContractorPaymentFromResultDelay()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "contractor-progress@example.com", "Исполнитель по результату", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var createResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Выполнить поручение",
            DueAt = DateTime.UtcNow.AddHours(2),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 6_500m,
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(createResult.Result);
        var createdDto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Null(createdDto.ContractorPaymentId);
        Assert.Empty(await db.Payments.ToListAsync());

        var statusResult = await controller.UpdateStatus(createdDto.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "inProgress",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(statusResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("inProgress", dto.Status);
        Assert.True(dto.ContractorPaymentOnResult);
        Assert.Equal(5, dto.ContractorPaymentResultDelayDays);
        Assert.Null(dto.ContractorPaymentId);
        Assert.Empty(await db.Payments.ToListAsync());

        var doneResult = await controller.UpdateStatus(createdDto.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Работа принята.",
        }, CancellationToken.None);

        var doneOk = Assert.IsType<OkObjectResult>(doneResult.Result);
        var doneDto = Assert.IsType<TaskJournalItemDto>(doneOk.Value);
        Assert.Equal("done", doneDto.Status);
        Assert.NotNull(doneDto.ContractorPaymentId);

        var payment = await db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(6_500m, payment.Amount);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(5), payment.Date);
    }

    [Fact]
    public async Task UpdateStatus_DoneKeepsExistingContractorPaymentResponsible()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Author");
        var assignee = await CreateAdminUserAsync(db, "contractor-existing-payment@example.com", "Contractor", isExternalContractor: true);
        var client = await CreateClientAsync(db, "Client");
        var payment = new Payment
        {
            Type = PaymentType.Expense,
            Status = PaymentStatus.Cancelled,
            Amount = 500m,
            PaidAmount = 0m,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Description = "Contractor payment",
            CounterpartyName = assignee.FullName,
            SystemNotes = "existing contractor payment",
            DocumentKind = DocumentKind.Payment,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            IsPaid = false,
        };
        payment.Responsibles.Add(new PaymentResponsible { UserId = assignee.Id });
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        var task = new TaskJournalItem
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            AssigneeName = assignee.FullName,
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            ObjectName = client.Name,
            Title = "Accept work",
            DueAt = DateTime.UtcNow.AddHours(2),
            Status = "approved",
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 500m,
            ContractorPaymentOnResult = true,
            ContractorPaymentResultDelayDays = 5,
            ContractorPaymentId = payment.Id,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = CreateController(db, new RecordingNotificationService(), author);

        var doneResult = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Work accepted.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(doneResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("done", dto.Status);
        Assert.Equal(payment.Id, dto.ContractorPaymentId);

        var responsibles = await db.PaymentResponsibles
            .Where(item => item.PaymentId == payment.Id)
            .ToListAsync();
        var responsible = Assert.Single(responsibles);
        Assert.Equal(assignee.Id, responsible.UserId);

        var updatedPayment = await db.Payments.SingleAsync(item => item.Id == payment.Id);
        Assert.Equal(PaymentStatus.Pending, updatedPayment.Status);
        Assert.Equal(DateTime.UtcNow.Date.AddDays(5), updatedPayment.Date);
    }

    [Fact]
    public async Task CreateTask_NotifiesWhenScheduleConflictsWithActiveTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var existingTask = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            AssigneeUserId = assignee.Id,
            Title = "Проверить пересечение",
            DueAt = existingTask.DueAt.AddMinutes(30),
            PlannedDurationMinutes = 60,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        var conflict = Assert.Single(notifications.ScheduleConflictTasks);
        Assert.Equal(dto.Id, conflict.TaskId);
        Assert.Contains(existingTask.Id, conflict.ConflictIds);
    }

    [Fact]
    public async Task CreateTask_AddsSystemHistoryEntry()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Подготовить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        var commentsResult = await controller.GetComments(dto.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(commentsResult.Result);
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCommentDto>>(ok.Value);
        var history = Assert.Single(comments);

        Assert.Equal("system", history.Kind);
        Assert.Equal("Задача создана.", history.Text);
    }

    [Fact]
    public async Task CreateComment_AddsUserComment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.CreateComment(task.Id, new CreateTaskJournalCommentRequest
        {
            Text = "Клиент прислал документы.",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var comment = Assert.IsType<TaskJournalCommentDto>(created.Value);

        Assert.Equal("comment", comment.Kind);
        Assert.Equal("Клиент прислал документы.", comment.Text);
        Assert.Equal(author.Id, comment.AuthorUserId);
        Assert.Single(notificationService.CommentTaskIds, task.Id);
    }

    [Fact]
    public async Task UploadFile_CreatesDocumentVersionsAndHistory()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "Документы по задаче");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, clientCaseId: clientCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var firstResult = await controller.UploadFile(
            task.Id,
            CreateFormFile("первая версия", "Документ.txt"),
            null,
            CancellationToken.None);
        var firstCreated = Assert.IsType<CreatedResult>(firstResult.Result);
        var first = Assert.IsType<TaskJournalFileDto>(firstCreated.Value);

        var secondResult = await controller.UploadFile(
            task.Id,
            CreateFormFile("вторая версия", "Документ.txt"),
            first.DocumentKey,
            CancellationToken.None);
        var secondCreated = Assert.IsType<CreatedResult>(secondResult.Result);
        var second = Assert.IsType<TaskJournalFileDto>(secondCreated.Value);

        Assert.Equal(first.DocumentKey, second.DocumentKey);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.True(second.IsLatestVersion);
        Assert.NotNull(first.ClientCaseDocumentId);
        Assert.NotNull(second.ClientCaseDocumentId);

        var filesResult = await controller.GetFiles(task.Id, CancellationToken.None);
        var filesOk = Assert.IsType<OkObjectResult>(filesResult.Result);
        var files = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalFileDto>>(filesOk.Value);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, item => item.Id == first.Id && !item.IsLatestVersion && item.ClientCaseDocumentId == first.ClientCaseDocumentId);
        Assert.Contains(files, item => item.Id == second.Id && item.IsLatestVersion && item.ClientCaseDocumentId == second.ClientCaseDocumentId);
        Assert.Equal(2, await db.ClientCaseDocuments.CountAsync(item => item.ClientCaseId == clientCase.Id && item.DocumentKey.StartsWith($"task-{task.Id}-")));

        var download = await controller.DownloadFile(task.Id, second.Id, CancellationToken.None);
        var physicalFile = Assert.IsType<PhysicalFileResult>(download);
        Assert.Equal("Документ.txt", physicalFile.FileDownloadName);

        var commentsResult = await controller.GetComments(task.Id, CancellationToken.None);
        var commentsOk = Assert.IsType<OkObjectResult>(commentsResult.Result);
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCommentDto>>(commentsOk.Value);
        Assert.Contains(comments, item => item.Kind == "system" && item.Text.Contains("новая версия", StringComparison.OrdinalIgnoreCase));

        Assert.IsType<NoContentResult>(await controller.DeleteFile(task.Id, first.Id, CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.DeleteFile(task.Id, second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFile_RejectsEmptyFile()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-empty@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFileWithDeclaredLength([], "empty.txt", 0),
            null,
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UploadFile_RejectsTooLargeFileByDeclaredLength()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-large@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFileWithDeclaredLength([1], "large.txt", 25L * 1024 * 1024 + 1),
            null,
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData("..\\..\\evil?.txt")]
    [InlineData("../outside/evil?.txt")]
    [InlineData("..%2foutside%2fevil?.txt")]
    public async Task UploadFile_SanitizesTraversalFileNameAndKeepsStoragePathUnderTask(string fileName)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-path@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFile("safe content", fileName),
            null,
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalFileDto>(created.Value);
        var stored = await db.TaskJournalFiles.SingleAsync(item => item.Id == dto.Id);

        Assert.DoesNotContain("..", dto.OriginalFileName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", dto.OriginalFileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", dto.OriginalFileName, StringComparison.Ordinal);
        Assert.StartsWith($"{task.Id}{Path.DirectorySeparatorChar}", stored.StoragePath, StringComparison.Ordinal);
        Assert.DoesNotContain("..", stored.StoragePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadFile_ReturnsNotFoundForMissingPhysicalFile()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-missing-file@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);
        db.TaskJournalFiles.Add(new TaskJournalFile
        {
            TaskJournalItemId = task.Id,
            DocumentKey = Guid.NewGuid().ToString("N"),
            Version = 1,
            OriginalFileName = "missing.txt",
            StoredFileName = "missing.txt",
            StoragePath = Path.Combine(task.Id.ToString(), "missing.txt"),
            ContentType = "text/plain",
            SizeBytes = 10,
            UploadedByUserId = author.Id,
            UploadedByName = author.FullName,
            UploadedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var file = await db.TaskJournalFiles.SingleAsync();

        var result = await controller.DownloadFile(task.Id, file.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DownloadFile_ReturnsNotFoundForFileFromAnotherTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-other-task-file@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var firstTask = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var secondTask = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        secondTask.Title = "Вторая задача";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);
        var upload = await controller.UploadFile(
            firstTask.Id,
            CreateFormFile("task file", "document.txt"),
            null,
            CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(upload.Result);
        var file = Assert.IsType<TaskJournalFileDto>(created.Value);

        var result = await controller.DownloadFile(secondTask.Id, file.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateStatus_DoneRequiresApprovedApprovers()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Готово",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_DoneRequiresResultByDefault()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-result-default@example.com", "РђРІС‚РѕСЂ");
        var client = await CreateClientAsync(db, "РРІР°РЅРѕРІ РРІР°РЅ РРІР°РЅРѕРІРёС‡");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = null,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateStatus_DoneAllowsEmptyResultWhenSettingDisabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-result-disabled@example.com", "РђРІС‚РѕСЂ");
        var client = await CreateClientAsync(db, "РРІР°РЅРѕРІ РРІР°РЅ РРІР°РЅРѕРІРёС‡");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.requireCompletionResult",
            Value = "false",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = null,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("done", dto.Status);
        Assert.Equal(string.Empty, dto.Result);
    }

    [Fact]
    public async Task UpdateApproval_RevisionReturnsTaskToRevision()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        task.Status = "onApproval";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), approver);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "revisionRequested",
            Resolution = "Нужно исправить дату.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Equal("revision", dto.Status);
        Assert.Contains(dto.Participants, item =>
            item.Role == "approver"
            && item.UserId == approver.Id
            && item.ApprovalStatus == "revisionRequested"
            && item.Resolution == "Нужно исправить дату.");

        var commentsResult = await controller.GetComments(task.Id, CancellationToken.None);
        var commentsOk = Assert.IsType<OkObjectResult>(commentsResult.Result);
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCommentDto>>(commentsOk.Value);
        var history = Assert.Single(comments, item => item.Kind == "system" && item.Text.Contains("Возвращено на доработку", StringComparison.OrdinalIgnoreCase));
        using var metadata = JsonDocument.Parse(history.MetadataJson);
        Assert.Equal("approvalDecision", metadata.RootElement.GetProperty("kind").GetString());
        Assert.Equal("revisionRequested", metadata.RootElement.GetProperty("decision").GetString());
        Assert.Equal("revision", metadata.RootElement.GetProperty("resultingStatus").GetString());
        Assert.Equal(approver.Id, metadata.RootElement.GetProperty("approverUserId").GetInt32());
    }

    [Fact]
    public async Task UpdateApproval_AllApproversApprovedAllowsCompletion()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var firstApprover = await CreateAdminUserAsync(db, "first@example.com", "Первый согласующий");
        var secondApprover = await CreateAdminUserAsync(db, "second@example.com", "Второй согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        task.Status = "onApproval";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            Email = firstApprover.Email,
            ApprovalStatus = "approved",
            SortOrder = 0,
        });
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = secondApprover.Id,
            Role = "approver",
            DisplayName = secondApprover.FullName,
            Email = secondApprover.Email,
            ApprovalStatus = "pending",
            SortOrder = 1,
        });
        await db.SaveChangesAsync();
        var notificationService = new RecordingNotificationService();
        var approverController = CreateController(db, notificationService, secondApprover);

        var approvalResult = await approverController.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
            Resolution = "Согласовано.",
        }, CancellationToken.None);

        var approvalOk = Assert.IsType<OkObjectResult>(approvalResult.Result);
        var approvalDto = Assert.IsType<TaskJournalItemDto>(approvalOk.Value);
        Assert.Equal("approved", approvalDto.Status);

        var authorController = CreateController(db, notificationService, author);
        var doneResult = await authorController.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Задача выполнена.",
        }, CancellationToken.None);

        var doneOk = Assert.IsType<OkObjectResult>(doneResult.Result);
        var doneDto = Assert.IsType<TaskJournalItemDto>(doneOk.Value);
        Assert.Equal("done", doneDto.Status);
        Assert.Single(notificationService.CompletedTaskIds, task.Id);
    }

    [Fact]
    public async Task UpdateStatus_ApprovedRequiresAllApproversApproved()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        task.Status = "onApproval";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "approved",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BulkUpdateTasks_UpdatesAssigneePriorityDueAndHistory()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var oldAssignee = await CreateAdminUserAsync(db, "old@example.com", "Старый исполнитель");
        var newAssignee = await CreateAdminUserAsync(db, "new@example.com", "Новый исполнитель");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var first = await CreateTaskAsync(db, author.Id, client.Id, oldAssignee.Id);
        var second = await CreateTaskAsync(db, author.Id, client.Id, oldAssignee.Id);
        first.Participants.Add(new TaskJournalParticipant
        {
            UserId = oldAssignee.Id,
            Role = "assignee",
            DisplayName = oldAssignee.FullName,
            Email = oldAssignee.Email,
        });
        second.Participants.Add(new TaskJournalParticipant
        {
            UserId = oldAssignee.Id,
            Role = "assignee",
            DisplayName = oldAssignee.FullName,
            Email = oldAssignee.Email,
        });
        await db.SaveChangesAsync();
        var dueAt = DateTime.UtcNow.AddDays(3);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [first.Id, second.Id],
            AssigneeUserId = newAssignee.Id,
            Priority = "urgent",
            DueAt = dueAt,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(2, GetAnonymousInt(ok.Value, "requested"));
        Assert.Equal(0, GetAnonymousInt(ok.Value, "skipped"));

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .Where(item => item.Id == first.Id || item.Id == second.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.All(stored, task =>
        {
            Assert.Equal(newAssignee.Id, task.AssigneeUserId);
            Assert.Equal("Новый исполнитель", task.AssigneeName);
            Assert.Equal("urgent", task.Priority);
            Assert.Equal(dueAt, task.DueAt);
            var participant = Assert.Single(task.Participants, item => item.Role == "assignee");
            Assert.Equal(newAssignee.Id, participant.UserId);
            Assert.Contains(task.Comments, item => item.Kind == "system" && item.Text.Contains("Массовое изменение", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(2, notificationService.ChangedTasks.Count);
    }

    [Fact]
    public async Task BulkUpdateTasks_DoneWithoutResultSkipsTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [task.Id],
            Status = "done",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(0, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "skipped"));

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
        Assert.Empty(notificationService.CompletedTaskIds);
    }

    [Fact]
    public async Task BulkDeleteTasks_RemovesSelectedTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var first = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var second = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.BulkDeleteTasks(new BulkDeleteTaskJournalItemsRequest
        {
            Ids = [first.Id],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "requested"));
        Assert.Equal(0, GetAnonymousInt(ok.Value, "skipped"));

        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.Id == first.Id));
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.Id == second.Id));
        var deletedTaskIsDeleted = await db.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(item => item.Id == first.Id)
            .Select(item => EF.Property<bool>(item, "IsDeleted"))
            .SingleAsync();
        Assert.True(deletedTaskIsDeleted);
    }

    [Fact]
    public async Task BulkUpdateTasks_NotifiesApprovalWhenSentToApproval()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: "Проверьте результат.");
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
            SortOrder = 0,
        });
        await db.SaveChangesAsync();
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [task.Id],
            Status = "onApproval",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "updated"));
        Assert.Contains(task.Id, notificationService.ApprovalRequestedTaskIds);
    }

    [Fact]
    public async Task BulkUpdateTasks_RejectsMassApproval()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, status: "onApproval");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [task.Id],
            Status = "approved",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("Массовое согласование", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatus_OnApprovalAfterRevisionRestartsApprovalQueue()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var firstApprover = await CreateAdminUserAsync(db, "first@example.com", "Первый согласующий");
        var secondApprover = await CreateAdminUserAsync(db, "second@example.com", "Второй согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: "Исправлено.", status: "revision");
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            Email = firstApprover.Email,
            ApprovalStatus = "approved",
            Resolution = "Первый шаг согласован.",
            RespondedAt = DateTime.UtcNow.AddHours(-2),
            SortOrder = 0,
        });
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = secondApprover.Id,
            Role = "approver",
            DisplayName = secondApprover.FullName,
            Email = secondApprover.Email,
            ApprovalStatus = "revisionRequested",
            Resolution = "Нужно исправить дату.",
            RespondedAt = DateTime.UtcNow.AddHours(-1),
            SortOrder = 1,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "onApproval",
            Result = "Исправлено и отправлено повторно.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Equal("onApproval", dto.Status);
        Assert.All(dto.Participants.Where(item => item.Role == "approver"), participant =>
        {
            Assert.Equal("pending", participant.ApprovalStatus);
            Assert.Empty(participant.Resolution);
            Assert.Null(participant.RespondedAt);
        });
    }

    [Fact]
    public async Task UpdateApproval_RejectsDecisionWhenTaskIsNotOnApproval()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null, status: "inProgress");
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), approver);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
            Resolution = "Согласовано.",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("не находится на согласовании", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var participant = await db.TaskJournalParticipants.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("pending", participant.ApprovalStatus);
        Assert.Null(participant.Resolution);
        Assert.Null(participant.RespondedAt);
    }

    [Fact]
    public async Task UpdateApproval_RequiresApprovalQueueOrder()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var firstApprover = await CreateAdminUserAsync(db, "first@example.com", "Первый согласующий");
        var secondApprover = await CreateAdminUserAsync(db, "second@example.com", "Второй согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        task.Status = "onApproval";
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            Email = firstApprover.Email,
            ApprovalStatus = "pending",
            SortOrder = 0,
        });
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = secondApprover.Id,
            Role = "approver",
            DisplayName = secondApprover.FullName,
            Email = secondApprover.Email,
            ApprovalStatus = "pending",
            SortOrder = 1,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), secondApprover);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
            Resolution = "Согласовано.",
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static TasksController CreateController(
        PaymentContext db,
        INotificationService notificationService,
        User user,
        IInvoiceDocumentPdfService? pdfService = null)
        => new(db, notificationService, pdfService: pdfService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                        new Claim(ClaimTypes.Name, user.FullName),
                        new Claim(ClaimTypes.Email, user.Email),
                    ], "TestAuth")),
                },
            },
        };

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static (string TimeZoneId, DateTime ProfileToday) ResolveTimeZoneWithDifferentLocalDate()
    {
        var nowUtc = DateTime.UtcNow;
        var utcToday = nowUtc.Date;
        foreach (var timeZoneId in new[]
        {
            "Pacific/Kiritimati",
            "Line Islands Standard Time",
            "Pacific/Pago_Pago",
            "Samoa Standard Time",
        })
        {
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            var profileToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date;
            if (profileToday != utcToday)
            {
                return (timeZone.Id, profileToday);
            }
        }

        throw new InvalidOperationException("No test timezone with a local date different from UTC was found.");
    }

    private static (TaskJournalAutomationSchedulerService Scheduler, ServiceProvider Provider) CreateAutomationScheduler(
        PaymentContext db,
        IReadOnlyDictionary<string, string?> values,
        INotificationService? notificationService = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        if (notificationService is not null)
        {
            services.AddSingleton(notificationService);
        }
        var provider = services.BuildServiceProvider();

        return (
            new TaskJournalAutomationSchedulerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<TaskJournalAutomationSchedulerService>.Instance),
            provider);
    }

    private static async Task<User> CreateAdminUserAsync(PaymentContext db, string email, string fullName, bool isExternalContractor = false)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "admin")
            ?? new Role { Name = "admin", Description = "Администратор" };
        var user = new User
        {
            Email = email,
            FullName = fullName,
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            IsExternalContractor = isExternalContractor,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateTaskUserAsync(
        PaymentContext db,
        string email,
        string fullName,
        bool canEdit,
        bool canCreate = false,
        bool canExport = true)
    {
        var role = new Role
        {
            Name = $"tasks-{Guid.NewGuid():N}",
            Description = "Пользователь задач",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Tasks,
                    CanView = true,
                    CanCreate = canCreate,
                    CanEdit = canEdit,
                    CanDelete = canEdit,
                    CanExport = canExport,
                    ViewScope = DataVisibilityScope.Own,
                },
            ],
        };

        var user = new User
        {
            Email = email,
            FullName = fullName,
            PasswordHash = "test",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Client> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client { Name = name, IsActive = true };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<LegalEntity> CreateLegalEntityAsync(PaymentContext db, string shortName)
    {
        var legalEntity = new LegalEntity { ShortName = shortName };
        db.LegalEntities.Add(legalEntity);
        await db.SaveChangesAsync();
        return legalEntity;
    }

    private static async Task<ClientCase> CreateCaseAsync(
        PaymentContext db,
        int clientId,
        string title,
        ClientCaseStatus status = ClientCaseStatus.Open)
    {
        var clientCase = new ClientCase
        {
            ClientId = clientId,
            Title = title,
            Status = status,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase;
    }

    private static async Task<ClientCase> CreateLegalEntityCaseAsync(
        PaymentContext db,
        int legalEntityId,
        string title,
        ClientCaseStatus status = ClientCaseStatus.Open)
    {
        var clientCase = new ClientCase
        {
            LegalEntityId = legalEntityId,
            Title = title,
            Status = status,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase;
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(
        PaymentContext db,
        int authorUserId,
        int? clientId,
        int? assigneeUserId = null,
        int? clientCaseId = null,
        string? result = null,
        string status = "new",
        DateTime? createdAt = null,
        int? legalEntityId = null,
        DateTime? approvalDueAt = null)
    {
        var task = new TaskJournalItem
        {
            ClientId = clientId,
            LegalEntityId = legalEntityId,
            ClientCaseId = clientCaseId,
            ObjectName = legalEntityId.HasValue ? "Тестовое юрлицо" : "Тестовый клиент",
            Title = "Тестовая задача",
            AuthorUserId = authorUserId,
            AuthorName = "Автор",
            AssigneeUserId = assigneeUserId,
            AssigneeName = "Исполнитель",
            DueAt = DateTime.UtcNow.AddHours(1),
            ApprovalDueAt = approvalDueAt,
            PlannedDurationMinutes = 60,
            Status = status,
            Result = result,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = "[]",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private static IFormFile CreateFormFile(string content, string fileName, string contentType = "text/plain")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static IFormFile CreateFormFileWithDeclaredLength(byte[] content, string fileName, long declaredLength, string contentType = "text/plain")
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, declaredLength, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static int GetAnonymousInt(object? value, string propertyName)
    {
        var property = value?.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property.GetValue(value));
    }

    private sealed class FakeInvoiceDocumentPdfService : IInvoiceDocumentPdfService
    {
        private readonly byte[] _bytes;

        public FakeInvoiceDocumentPdfService(byte[] bytes)
        {
            _bytes = bytes;
        }

        public string LastHtml { get; private set; } = string.Empty;

        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
        {
            LastHtml = html;
            return Task.FromResult(_bytes);
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<int> CompletedTaskIds { get; } = [];
        public List<int> AssignedTaskIds { get; } = [];
        public List<int> ApprovalRequestedTaskIds { get; } = [];
        public List<(int TaskId, string Decision)> ApprovalDecisionTaskIds { get; } = [];
        public List<(int TaskId, IReadOnlyList<string> ChangeLabels, string? Comment)> ChangedTasks { get; } = [];
        public List<int> CommentTaskIds { get; } = [];
        public List<(int TaskId, IReadOnlyList<int> ConflictIds)> ScheduleConflictTasks { get; } = [];
        public List<(int TaskId, int DaysWithoutActiveTask)> CaseInactivityControls { get; } = [];

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannelDto>>(Array.Empty<NotificationChannelDto>());

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationTemplateDto>>(Array.Empty<NotificationTemplateDto>());

        public Task<NotificationTemplateDto> UpdateTemplateAsync(string key, UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationMessage> RenderTemplateAsync(string key, string fallbackSubject, string fallbackBody, IReadOnlyDictionary<string, string?> values, bool isSensitive = false, string purpose = NotificationPurposes.ManualTest, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose));

        public Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(int userId, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> SendAsync(NotificationRecipient recipient, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<NotificationSettingsDto> UpsertSenderAsync(UpsertNotificationSenderRequest request, int? senderId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
        {
            AssignedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default)
        {
            CompletedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default)
        {
            ScheduleConflictTasks.Add((task.Id, conflicts.Select(item => item.Id).ToList()));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
        {
            ApprovalRequestedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default)
        {
            ApprovalDecisionTaskIds.Add((task.Id, decision));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default)
        {
            ChangedTasks.Add((task.Id, changeLabels, comment));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default)
        {
            CommentTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default)
        {
            CaseInactivityControls.Add((task.Id, daysWithoutActiveTask));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }
    }
}

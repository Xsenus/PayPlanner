using System.Security.Claims;
using System.Text;
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
    public async Task CreateTask_BindsClientAndClientCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
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
    public async Task UpdateStatus_DoneRequiresResult()
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

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateTask_DoneRequiresResult()
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

        Assert.IsType<BadRequestObjectResult>(result.Result);
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
        Assert.Equal("Тестовая задача", stored.Title);
        Assert.Equal(originalDueAt, stored.DueAt);
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
    public async Task GetAutomationCaseCandidates_SkipsCasesWithActiveTaskAndClosedCases()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        var closedCase = await CreateCaseAsync(db, client.Id, "ЗАКРЫТОЕ ДЕЛО", ClientCaseStatus.Closed);
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: activeCase.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(inactiveDays: 14, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);

        Assert.DoesNotContain(candidates, item => item.ClientCaseId == activeCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == closedCase.Id);
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_CreatesControlTaskWithCaseBinding()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
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
    public async Task CreateAutomationTasksForCandidates_CreatesTasksForCasesWithoutActiveTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var firstCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        var secondCase = await CreateCaseAsync(db, client.Id, "СЕМЕЙНЫЙ СПОР");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: activeCase.Id);
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
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsFreshAndAlreadyControlledCases()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var oldCase = await CreateCaseAsync(db, client.Id, "СТАРОЕ ДЕЛО");
        var freshCase = await CreateCaseAsync(db, client.Id, "НОВОЕ ДЕЛО");
        var activeCase = await CreateCaseAsync(db, client.Id, "АКТИВНОЕ ДЕЛО");
        oldCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        activeCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
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
    }

    [Fact]
    public async Task ExportCalendar_ReturnsIcsWithTaskDuration()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id);
        task.Title = "Проверить документы";
        task.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        task.PlannedDurationMinutes = 90;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportCalendar(from: null, to: null, ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Equal("text/calendar; charset=utf-8", file.ContentType);
        Assert.Contains("BEGIN:VCALENDAR", content);
        Assert.Contains("SUMMARY:Проверить документы", content);
        Assert.Contains("DTSTART:20260510T090000Z", content);
        Assert.Contains("DTEND:20260510T103000Z", content);
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
        var controller = CreateController(db, new RecordingNotificationService(), author);

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
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateComment(task.Id, new CreateTaskJournalCommentRequest
        {
            Text = "Клиент прислал документы.",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var comment = Assert.IsType<TaskJournalCommentDto>(created.Value);

        Assert.Equal("comment", comment.Kind);
        Assert.Equal("Клиент прислал документы.", comment.Text);
        Assert.Equal(author.Id, comment.AuthorUserId);
    }

    [Fact]
    public async Task UploadFile_CreatesDocumentVersionsAndHistory()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
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

        var filesResult = await controller.GetFiles(task.Id, CancellationToken.None);
        var filesOk = Assert.IsType<OkObjectResult>(filesResult.Result);
        var files = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalFileDto>>(filesOk.Value);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, item => item.Id == first.Id && !item.IsLatestVersion);
        Assert.Contains(files, item => item.Id == second.Id && item.IsLatestVersion);

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
        Assert.Contains(comments, item => item.Kind == "system" && item.Text.Contains("Возвращено на доработку", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal("inProgress", approvalDto.Status);

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
        User user)
        => new(db, notificationService)
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

    private static (TaskJournalAutomationSchedulerService Scheduler, ServiceProvider Provider) CreateAutomationScheduler(
        PaymentContext db,
        IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();

        return (
            new TaskJournalAutomationSchedulerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<TaskJournalAutomationSchedulerService>.Instance),
            provider);
    }

    private static async Task<User> CreateAdminUserAsync(PaymentContext db, string email, string fullName)
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
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateTaskUserAsync(PaymentContext db, string email, string fullName, bool canEdit)
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
                    CanCreate = false,
                    CanEdit = canEdit,
                    CanDelete = canEdit,
                    CanExport = true,
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
        int? legalEntityId = null)
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

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<int> CompletedTaskIds { get; } = [];

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(int userId, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> SendAsync(NotificationRecipient recipient, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
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

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default)
        {
            CompletedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }
    }
}

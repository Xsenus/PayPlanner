using System.Data.Common;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class TasksControllerTests
{
    [Fact]
    public async Task TaskTemplateCrud_PersistsAndReplacesParticipants()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "template-author@example.com", "Автор шаблона");
        var assignee = await CreateAdminUserAsync(db, "template-assignee@example.com", "Исполнитель шаблона");
        var approver = await CreateAdminUserAsync(db, "template-approver@example.com", "Согласующий шаблона");
        var observer = await CreateAdminUserAsync(db, "template-observer@example.com", "Наблюдатель шаблона");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var create = await controller.CreateTemplate(new UpsertTaskJournalTemplateRequest
        {
            Name = "Шаблон подготовки дела",
            Description = "Набор полей для старта дела.",
            Title = "Подготовить материалы",
            TaskDescription = "Проверить документы и запустить согласование.",
            Type = "document",
            Priority = "high",
            PlannedDurationMinutes = 125,
            DueOffsetMinutes = 180,
            AssigneeUserId = assignee.Id,
            BindingType = "caseStage",
            BindingKey = "start",
            IsActive = true,
            Reminders = ["before1h", "atDue"],
            Channels = ["inApp", "email"],
            Tags = ["дело", "старт"],
            Participants =
            [
                new TaskJournalTemplateParticipantRequest
                {
                    UserId = approver.Id,
                    Role = "approver",
                    SortOrder = 1,
                },
                new TaskJournalTemplateParticipantRequest
                {
                    UserId = observer.Id,
                    Role = "observer",
                    SortOrder = 2,
                },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(create.Result);
        var createdDto = Assert.IsType<TaskJournalTemplateDto>(created.Value);
        Assert.True(createdDto.Id > 0);
        Assert.Equal(assignee.Id, createdDto.AssigneeUserId);
        Assert.Equal("Исполнитель шаблона", createdDto.AssigneeName);
        Assert.Equal(2, createdDto.Participants.Count);
        Assert.Equal("approver", createdDto.Participants[0].Role);

        var stored = await db.TaskJournalTemplates
            .Include(item => item.Participants)
            .SingleAsync(item => item.Id == createdDto.Id);
        Assert.Equal("high", stored.Priority);
        Assert.Equal(2, stored.Participants.Count);

        var list = await controller.GetTemplates(null, true, "document", "high", "caseStage", assignee.Id, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(list.Result);
        var listDto = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalTemplateDto>>(listOk.Value);
        Assert.Single(listDto);
        Assert.Equal(createdDto.Id, listDto[0].Id);

        var update = await controller.UpdateTemplate(createdDto.Id, new UpsertTaskJournalTemplateRequest
        {
            Name = "Шаблон проверки дела",
            Title = "Проверить материалы",
            TaskDescription = "Отдельная инструкция по шаблону.",
            Type = "task",
            Priority = "normal",
            PlannedDurationMinutes = 40,
            DueOffsetMinutes = 0,
            AssigneeName = "Дежурный сотрудник",
            BindingType = "none",
            IsActive = false,
            Reminders = [],
            Channels = [],
            Tags = ["проверка"],
            Participants =
            [
                new TaskJournalTemplateParticipantRequest
                {
                    Name = "Внешний наблюдатель",
                    Role = "observer",
                },
            ],
        }, CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(update.Result);
        var updatedDto = Assert.IsType<TaskJournalTemplateDto>(updateOk.Value);
        Assert.False(updatedDto.IsActive);
        Assert.Null(updatedDto.AssigneeUserId);
        Assert.Equal("Дежурный сотрудник", updatedDto.AssigneeName);
        Assert.Equal("none", updatedDto.BindingType);
        Assert.Equal(string.Empty, updatedDto.BindingKey);
        Assert.Single(updatedDto.Participants);
        Assert.Equal("Внешний наблюдатель", updatedDto.Participants[0].DisplayName);

        var fetched = await controller.GetTemplate(createdDto.Id, CancellationToken.None);
        var fetchedOk = Assert.IsType<OkObjectResult>(fetched.Result);
        var fetchedDto = Assert.IsType<TaskJournalTemplateDto>(fetchedOk.Value);
        Assert.Equal(updatedDto.Id, fetchedDto.Id);
        Assert.Equal(updatedDto.Name, fetchedDto.Name);
        Assert.Equal(updatedDto.Title, fetchedDto.Title);
        Assert.Equal(updatedDto.TaskDescription, fetchedDto.TaskDescription);
        Assert.Equal("task", fetchedDto.Type);
        Assert.Equal("normal", fetchedDto.Priority);
        Assert.Equal(updatedDto.PlannedDurationMinutes, fetchedDto.PlannedDurationMinutes);
        Assert.Equal(0, fetchedDto.DueOffsetMinutes);
        Assert.Null(fetchedDto.AssigneeUserId);
        Assert.Equal(updatedDto.AssigneeName, fetchedDto.AssigneeName);
        Assert.Equal("none", fetchedDto.BindingType);
        Assert.Equal(string.Empty, fetchedDto.BindingKey);
        Assert.False(fetchedDto.IsActive);
        Assert.Equal(updatedDto.Reminders, fetchedDto.Reminders);
        Assert.Equal(updatedDto.Channels, fetchedDto.Channels);
        Assert.Equal(updatedDto.Tags, fetchedDto.Tags);
        Assert.Equal(author.Id, fetchedDto.CreatedByUserId);
        Assert.Equal(author.FullName, fetchedDto.CreatedByName);
        Assert.NotEqual(default, fetchedDto.CreatedAt);
        Assert.NotNull(fetchedDto.UpdatedAt);
        var fetchedParticipant = Assert.Single(fetchedDto.Participants);
        Assert.Null(fetchedParticipant.UserId);
        Assert.Equal("observer", fetchedParticipant.Role);
        Assert.Equal(updatedDto.Participants[0].DisplayName, fetchedParticipant.DisplayName);
        Assert.Equal(0, fetchedParticipant.SortOrder);

        var active = await controller.UpdateTemplateActive(createdDto.Id, new UpdateTaskJournalTemplateActiveRequest
        {
            IsActive = true,
        }, CancellationToken.None);
        var activeOk = Assert.IsType<OkObjectResult>(active.Result);
        Assert.True(Assert.IsType<TaskJournalTemplateDto>(activeOk.Value).IsActive);

        var delete = await controller.DeleteTemplate(createdDto.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(delete);

        var deleted = await controller.GetTemplate(createdDto.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(deleted.Result);
        Assert.True(await db.TaskJournalTemplates.IgnoreQueryFilters().AnyAsync(item => item.Id == createdDto.Id && EF.Property<bool>(item, "IsDeleted")));
    }

    [Fact]
    public async Task CreateTemplate_RejectsParticipantWithoutNameOrUser()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "template-invalid-author@example.com", "Автор шаблона");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTemplate(new UpsertTaskJournalTemplateRequest
        {
            Name = "Шаблон с ошибкой",
            Title = "Проверить участников",
            AssigneeName = "Исполнитель",
            Participants =
            [
                new TaskJournalTemplateParticipantRequest
                {
                    Role = "observer",
                },
            ],
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(db.TaskJournalTemplates);
    }

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
    public async Task CreateTask_PersistsRecurrenceAndReturnsNextOccurrence()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "recurrence-create@example.com", "Автор повторения");
        var client = await CreateClientAsync(db, "Клиент нового повторения");
        var dueAt = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(2).Date.AddHours(9), DateTimeKind.Utc);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Регулярно проверять документы",
            DueAt = dueAt,
            RecurrenceEnabled = true,
            RecurrenceFrequency = "monthly",
            RecurrenceInterval = 1,
            RecurrenceDayOfMonth = dueAt.Day,
            RecurrenceTimeMinutes = 9 * 60,
            RecurrenceSkipWeekends = true,
            RecurrenceTimeZoneId = "UTC",
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.True(dto.RecurrenceEnabled);
        Assert.Equal("monthly", dto.RecurrenceFrequency);
        Assert.Equal(dueAt.Day, dto.RecurrenceDayOfMonth);
        Assert.True(DateTime.Parse(dto.RecurrenceNextAt!).ToUniversalTime() > dueAt);

        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == dto.Id);
        Assert.True(stored.RecurrenceEnabled);
        Assert.NotNull(stored.RecurrenceNextAt);
        Assert.Equal("UTC", stored.RecurrenceTimeZoneId);
    }

    [Fact]
    public async Task CreateTask_RejectsWeeklyRecurrenceWithoutDayOfWeek()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "recurrence-invalid@example.com", "Автор повторения");
        var client = await CreateClientAsync(db, "Клиент ошибочного повторения");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Некорректное повторение",
            DueAt = DateTime.UtcNow.AddDays(1),
            RecurrenceEnabled = true,
            RecurrenceFrequency = "weekly",
            RecurrenceInterval = 1,
            RecurrenceTimeMinutes = 9 * 60,
            RecurrenceTimeZoneId = "UTC",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("день недели", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.TaskJournalItems);
    }

    [Fact]
    public async Task GetAssignees_ReturnsProfileTimeZone()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-assignees@example.com", "Администратор");
        var assignee = await CreateAdminUserAsync(db, "kamchatka-assignee@example.com", "Исполнитель Камчатка");
        var department = new DepartmentDictionaryItem { Name = "Контроль", IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        assignee.DepartmentId = department.Id;
        assignee.Department = "Контроль";
        db.UserSettings.Add(new UserSetting
        {
            UserId = assignee.Id,
            Key = "profile.timeZoneId",
            Value = "Asia/Kamchatka",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetAssignees(ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalAssigneeDto>>(ok.Value);
        var dto = Assert.Single(items, item => item.Id == assignee.Id);
        Assert.Equal("Asia/Kamchatka", dto.TimeZoneId);
        Assert.Equal(department.Id, dto.DepartmentId);
        Assert.Equal("Контроль", dto.Department);
    }

    [Fact]
    public async Task GetAssignees_WithTaskCreatePermission_ReturnsAllActiveUsersDespiteOwnVisibility()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-create-own@example.com", "Постановщик", canEdit: false, canCreate: true);
        var visibleAssignee = await CreateTaskUserAsync(db, "visible-assignee@example.com", "Доступный исполнитель", canEdit: false);
        var inactiveAssignee = await CreateTaskUserAsync(db, "inactive-assignee@example.com", "Неактивный исполнитель", canEdit: false);
        inactiveAssignee.IsActive = false;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetAssignees(ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalAssigneeDto>>(ok.Value);
        Assert.Contains(items, item => item.Id == current.Id);
        Assert.Contains(items, item => item.Id == visibleAssignee.Id);
        Assert.DoesNotContain(items, item => item.Id == inactiveAssignee.Id);
    }

    [Fact]
    public async Task GetAssignees_WithoutTaskAssignmentPermissionAppliesUserVisibility()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "assignees-own@example.com", "Ограниченный пользователь", canEdit: false);
        var hiddenAssignee = await CreateTaskUserAsync(db, "assignees-hidden@example.com", "Скрытый сотрудник", canEdit: false);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetAssignees(ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalAssigneeDto>>(ok.Value);
        Assert.Contains(items, item => item.Id == current.Id);
        Assert.DoesNotContain(items, item => item.Id == hiddenAssignee.Id);
    }

    [Fact]
    public async Task GetAssignees_UsesSpecificCalendarVisibilityForCalendarView()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "secretary-calendar",
            Description = "Секретарь с точечными календарями",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Tasks,
                    CanView = true,
                    ViewScope = DataVisibilityScope.All,
                },
                new RolePermission
                {
                    Section = RolePermissionCatalog.Calendar,
                    CanView = true,
                    ViewScope = DataVisibilityScope.Specific,
                },
            ],
        };
        var current = new User
        {
            Email = "calendar-viewer@example.com",
            PasswordHash = "test",
            FullName = "Секретарь",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        var selected = new User
        {
            Email = "calendar-selected-assignee@example.com",
            PasswordHash = "test",
            FullName = "Выбранный сотрудник",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        var hidden = new User
        {
            Email = "calendar-hidden-assignee@example.com",
            PasswordHash = "test",
            FullName = "Скрытый сотрудник",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.AddRange(current, selected, hidden);
        await db.SaveChangesAsync();

        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
        {
            RoleId = role.Id,
            UserId = selected.Id,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetAssignees(calendarView: true, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalAssigneeDto>>(ok.Value);
        Assert.Contains(items, item => item.Id == current.Id);
        Assert.Contains(items, item => item.Id == selected.Id);
        Assert.DoesNotContain(items, item => item.Id == hidden.Id);
    }

    [Fact]
    public async Task GetTasks_CalendarViewDoesNotExposeAuthorOnlyHiddenAssignee()
    {
        await using var db = await CreateContextAsync();
        var role = new Role
        {
            Name = "secretary-calendar-tasks",
            Description = "Секретарь с точечными календарями",
            Permissions =
            [
                new RolePermission
                {
                    Section = RolePermissionCatalog.Calendar,
                    CanView = true,
                    ViewScope = DataVisibilityScope.Specific,
                },
            ],
        };
        var current = new User
        {
            Email = "calendar-task-viewer@example.com",
            PasswordHash = "test",
            FullName = "Секретарь",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        var selected = new User
        {
            Email = "calendar-task-selected@example.com",
            PasswordHash = "test",
            FullName = "Выбранный сотрудник",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        var hidden = new User
        {
            Email = "calendar-task-hidden@example.com",
            PasswordHash = "test",
            FullName = "Скрытый сотрудник",
            IsActive = true,
            IsApproved = true,
            Role = role,
        };
        db.Users.AddRange(current, selected, hidden);
        await db.SaveChangesAsync();

        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
        {
            RoleId = role.Id,
            UserId = selected.Id,
        });
        var currentTask = await CreateTaskAsync(db, current.Id, null, current.Id);
        currentTask.Title = "Задача секретаря";
        currentTask.AuthorUserId = current.Id;
        currentTask.AuthorName = current.FullName;
        var selectedTask = await CreateTaskAsync(db, current.Id, null, selected.Id);
        selectedTask.Title = "Задача выбранного";
        selectedTask.AuthorUserId = current.Id;
        selectedTask.AuthorName = current.FullName;
        var hiddenTask = await CreateTaskAsync(db, current.Id, null, hidden.Id);
        hiddenTask.Title = "Задача скрытого";
        hiddenTask.AuthorUserId = current.Id;
        hiddenTask.AuthorName = current.FullName;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            calendarView: true,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);

        Assert.Contains(page.Items, item => item.Id == currentTask.Id);
        Assert.Contains(page.Items, item => item.Id == selectedTask.Id);
        Assert.DoesNotContain(page.Items, item => item.Id == hiddenTask.Id);
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
            ExecutorCompletionAllowedUserIds = [fallbackAssignee.Id],
            RequireCompletionResult = false,
            DailyOverdueReminderEnabled = false,
            BirthdayTaskAutomationEnabled = true,
            EmployeeBirthdayTaskAutomationEnabled = true,
            BirthdayTaskLeadDays = 14,
            BirthdayClientUseSelectedAssignee = true,
            BirthdayTaskAssigneeUserId = fallbackAssignee.Id,
            AllowAdminCompleteAnyTask = true,
            FinancialDocumentsResponsibleUserId = fallbackAssignee.Id,
            ApprovalDueDays = 400,
            TaskCardWindowMode = "modern",
            ApprovalQuickActionMode = "separateButton",
            CaseInactivityAutomationEnabled = false,
            CaseInactivityDays = 400,
            CaseInactivityLimit = 0,
            CaseInactivityDefaultAssigneeUserId = fallbackAssignee.Id,
            CaseInactivityEscalationDays = 7,
            CaseInactivityEscalationUserId = fallbackAssignee.Id,
            CaseInactivityRunLocalTime = "16:45",
            CaseInactivityScheduleTimeZone = "Asia/Novosibirsk",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalSettingsDto>(ok.Value);

        Assert.False(dto.RestrictExecutorEditing);
        Assert.False(dto.AllowExecutorDeadlineChangeRequests);
        Assert.Equal([fallbackAssignee.Id], dto.ExecutorCompletionAllowedUserIds);
        Assert.False(dto.RequireCompletionResult);
        Assert.False(dto.DailyOverdueReminderEnabled);
        Assert.True(dto.BirthdayTaskAutomationEnabled);
        Assert.True(dto.EmployeeBirthdayTaskAutomationEnabled);
        Assert.Equal(14, dto.BirthdayTaskLeadDays);
        Assert.True(dto.BirthdayClientUseSelectedAssignee);
        Assert.Equal(fallbackAssignee.Id, dto.BirthdayTaskAssigneeUserId);
        Assert.True(dto.AllowAdminCompleteAnyTask);
        Assert.Equal(fallbackAssignee.Id, dto.FinancialDocumentsResponsibleUserId);
        Assert.Equal("Резервный исполнитель", dto.FinancialDocumentsResponsibleUserName);
        Assert.Equal(365, dto.ApprovalDueDays);
        Assert.Equal("modern", dto.TaskCardWindowMode);
        Assert.Equal("separateButton", dto.ApprovalQuickActionMode);
        Assert.False(dto.CaseInactivityAutomationEnabled);
        Assert.Equal(365, dto.CaseInactivityDays);
        Assert.Equal(1, dto.CaseInactivityLimit);
        Assert.Equal(fallbackAssignee.Id, dto.CaseInactivityDefaultAssigneeUserId);
        Assert.Equal(365, dto.CaseInactivityEscalationDays);
        Assert.Equal(fallbackAssignee.Id, dto.CaseInactivityEscalationUserId);
        Assert.Equal(2, dto.CaseInactivityFirstRepeatAfterDays);
        Assert.Equal(4, dto.CaseInactivityCriticalRepeatAfterDays);
        Assert.Equal("16:45", dto.CaseInactivityRunLocalTime);
        Assert.Equal("Asia/Novosibirsk", dto.CaseInactivityScheduleTimeZone);

        var stored = await db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key.StartsWith("tasks.automation.caseControl.")
                || item.Key == "tasks.approvalDueDays"
                || item.Key == "tasks.approvalQuickActionMode"
                || item.Key == "tasks.financialDocumentsResponsibleUserId"
                || item.Key == "tasks.executorCompletionAllowedUserIds"
                || item.Key.StartsWith("tasks.automation.birthdays."))
            .ToDictionaryAsync(item => item.Key, item => item.Value);

        Assert.Equal("365", stored["tasks.approvalDueDays"]);
        Assert.Equal("separateButton", stored["tasks.approvalQuickActionMode"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.financialDocumentsResponsibleUserId"]);
        Assert.Equal($"[{fallbackAssignee.Id}]", stored["tasks.executorCompletionAllowedUserIds"]);
        Assert.Equal("false", stored["tasks.automation.caseControl.enabled"]);
        Assert.Equal("365", stored["tasks.automation.caseControl.inactiveDays"]);
        Assert.Equal("1", stored["tasks.automation.caseControl.limit"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.automation.caseControl.defaultAssigneeUserId"]);
        Assert.Equal("365", stored["tasks.automation.caseControl.escalationDays"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.automation.caseControl.escalationUserId"]);
        Assert.Equal("16:45", stored["tasks.automation.caseControl.runLocalTime"]);
        Assert.Equal("Asia/Novosibirsk", stored["tasks.automation.caseControl.scheduleTimeZone"]);
        Assert.Equal("true", stored["tasks.automation.birthdays.enabled"]);
        Assert.Equal("true", stored["tasks.automation.birthdays.employees.enabled"]);
        Assert.Equal("14", stored["tasks.automation.birthdays.leadDays"]);
        Assert.Equal("true", stored["tasks.automation.birthdays.clients.useSelectedAssignee"]);
        Assert.Equal(fallbackAssignee.Id.ToString(), stored["tasks.automation.birthdays.assigneeUserId"]);

        var requireCompletionResult = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.requireCompletionResult");
        Assert.Equal("false", requireCompletionResult.Value);

        var dailyOverdueReminder = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.dailyOverdueReminderEnabled");
        Assert.Equal("false", dailyOverdueReminder.Value);

        var taskCardWindowMode = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.taskCardWindowMode");
        Assert.Equal("modern", taskCardWindowMode.Value);
    }

    [Fact]
    public async Task UpdateSettings_RejectsEmployeeBirthdayAutomationWithoutAssignee()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-birthday@example.com", "Администратор");
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.UpdateSettings(new TaskJournalSettingsRequest
        {
            EmployeeBirthdayTaskAutomationEnabled = true,
            BirthdayTaskLeadDays = 7,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.SystemSettings.AsNoTracking()
            .Where(item => item.Key.StartsWith("tasks.automation.birthdays."))
            .ToListAsync());
    }

    [Fact]
    public async Task UpdateSettings_RejectsNonAdminWithFullTaskPermissionsAndPreservesGlobalRules()
    {
        await using var db = await CreateContextAsync();
        var employee = await CreateTaskUserAsync(
            db,
            "task-editor-settings@example.com",
            "Редактор задач",
            canEdit: true,
            canCreate: true,
            viewScope: DataVisibilityScope.All,
            canEditOthers: true,
            canCompleteOthers: true,
            canDeleteOthers: true);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.inactiveDays",
            Value = "14",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), employee);

        var result = await controller.UpdateSettings(new TaskJournalSettingsRequest
        {
            CaseInactivityAutomationEnabled = false,
            CaseInactivityDays = 2,
            CaseInactivityLimit = 100,
            CaseInactivityEscalationDays = 4,
            CaseInactivityRunLocalTime = "00:00",
            CaseInactivityScheduleTimeZone = "UTC",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.automation.caseControl.inactiveDays");
        Assert.Equal("14", stored.Value);
        Assert.Single(await db.SystemSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UpdateSettings_PersistsAndReturnsNormalizedTaskStickerStyles()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-stickers@example.com", "Администратор");
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.UpdateSettings(new TaskJournalSettingsRequest
        {
            StickerStyles = new Dictionary<string, TaskStickerStyle>
            {
                ["priority.normal"] = new()
                {
                    Label = "Средний",
                    ShortLabel = "СРЕДНИЙ",
                    BackgroundColor = "#FEF3C7",
                    TextColor = "#92400E",
                    BorderColor = "#F59E0B",
                },
            },
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalSettingsDto>(ok.Value);
        var style = dto.StickerStyles["priority.normal"];

        Assert.Equal("Средний", style.Label);
        Assert.Equal("СРЕДНИЙ", style.ShortLabel);
        Assert.Equal("#fef3c7", style.BackgroundColor);
        Assert.Equal("#92400e", style.TextColor);
        Assert.Equal("#f59e0b", style.BorderColor);
        Assert.Equal(9, dto.StickerStyles.Count);

        var stored = await db.SystemSettings
            .AsNoTracking()
            .SingleAsync(item => item.Key == "tasks.stickerStyles");
        var storedStyles = JsonSerializer.Deserialize<Dictionary<string, TaskStickerStyle>>(
            stored.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(storedStyles);
        Assert.Equal("СРЕДНИЙ", storedStyles["priority.normal"].ShortLabel);
    }

    [Fact]
    public async Task UpdateSettings_RejectsInvalidTaskStickerColor()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-invalid-stickers@example.com", "Администратор");
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.UpdateSettings(new TaskJournalSettingsRequest
        {
            StickerStyles = new Dictionary<string, TaskStickerStyle>
            {
                ["priority.normal"] = new()
                {
                    Label = "Средний",
                    ShortLabel = "СРЕДНИЙ",
                    BackgroundColor = "yellow",
                    TextColor = "#92400e",
                    BorderColor = "#f59e0b",
                },
            },
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await db.SystemSettings.AnyAsync(item => item.Key == "tasks.stickerStyles"));
    }

    [Fact]
    public async Task GetOverdueSummary_CountsOnlyCurrentEmployeesActiveOverdueTasks()
    {
        await using var db = await CreateContextAsync();
        var employee = await CreateAdminUserAsync(db, "overdue-summary@example.com", "Сотрудник");
        var otherEmployee = await CreateAdminUserAsync(db, "other-overdue-summary@example.com", "Другой сотрудник");

        var overdue = await CreateTaskAsync(db, employee.Id, null, employee.Id);
        overdue.DueAt = DateTime.UtcNow.AddHours(-2);
        var completed = await CreateTaskAsync(db, employee.Id, null, employee.Id, status: "done");
        completed.DueAt = DateTime.UtcNow.AddHours(-3);
        var future = await CreateTaskAsync(db, employee.Id, null, employee.Id);
        future.DueAt = DateTime.UtcNow.AddHours(2);
        var other = await CreateTaskAsync(db, employee.Id, null, otherEmployee.Id);
        other.DueAt = DateTime.UtcNow.AddHours(-4);
        await db.SaveChangesAsync();

        var controller = CreateController(db, new RecordingNotificationService(), employee);
        var result = await controller.GetOverdueSummary(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<TaskJournalOverdueSummaryDto>(ok.Value);
        Assert.Equal(1, summary.Count);
    }

    [Fact]
    public async Task GetCaseControlAutomationDiagnostics_ReturnsCurrentAutomationState()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
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
        db.ObjectAuditEvents.AddRange(
            new ObjectAuditEvent
            {
                ObjectType = "client-case",
                ObjectId = clientCase.Id,
                Action = "Automation",
                Description = "Старая контрольная задача.",
                MetadataJson = "{\"event\":\"caseInactivityControlTaskCreated\"}",
                CreatedAt = now.AddDays(-10),
            },
            new ObjectAuditEvent
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
        interceptor.Commands.Clear();

        var result = await controller.GetCaseControlAutomationDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationDiagnosticsDto>(ok.Value);

        Assert.True(dto.Enabled);
        Assert.Equal(14, dto.InactivityDays);
        Assert.Equal(30, dto.EscalationDays);
        Assert.Equal(10, dto.Limit);
        Assert.Equal(fallbackAssignee.Id, dto.DefaultAssigneeUserId);
        Assert.Equal("Резервный исполнитель", dto.DefaultAssigneeName);
        Assert.True(dto.DefaultAssigneeAvailable);
        Assert.Equal(escalationUser.Id, dto.EscalationUserId);
        Assert.Equal("Руководитель", dto.EscalationUserName);
        Assert.Equal(1, dto.VisibleCandidateCount);
        Assert.Equal(1, dto.NextRunCandidateCount);
        Assert.Equal(0, dto.DeferredByLimitCount);
        Assert.Equal(0, dto.VisibleUnassignedCount);
        Assert.Equal(1, dto.VisibleEscalationCandidateCount);
        Assert.Equal(1, dto.CreatedTaskCountLast7Days);
        Assert.NotNull(dto.NextScheduledRunAt);
        Assert.NotNull(dto.LastCreatedAt);
        Assert.Equal("Создана контрольная задача по делу БАНКРОТСТВО.", dto.LastCreatedDescription);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ObjectAuditEvents\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCaseControlAutomationDiagnostics_SeparatesDueCasesWithoutAvailableAssignee()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-unassigned-diagnostics@example.com", "Администратор");
        var client = await CreateClientAsync(db, "Клиент без исполнителя диагностики");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ИСПОЛНИТЕЛЯ ДИАГНОСТИКИ");
        var recentCase = await CreateCaseAsync(db, client.Id, "СВЕЖЕЕ ДЕЛО БЕЗ ИСПОЛНИТЕЛЯ ДИАГНОСТИКИ");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-45);
        recentCase.CreatedAt = now.AddDays(-2);
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "tasks.automation.caseControl.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.inactiveDays", Value = "14", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.limit", Value = "10", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.escalationDays", Value = "30", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetCaseControlAutomationDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationDiagnosticsDto>(ok.Value);
        Assert.Equal(0, dto.VisibleCandidateCount);
        Assert.Equal(0, dto.NextRunCandidateCount);
        Assert.Equal(0, dto.DeferredByLimitCount);
        Assert.Equal(1, dto.VisibleUnassignedCount);
        Assert.Equal(0, dto.VisibleEscalationCandidateCount);
        Assert.False(dto.DefaultAssigneeAvailable);
    }

    [Fact]
    public async Task PreviewCaseControlAutomation_CountsCandidatesForUnsavedThresholdsInSingleCaseQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var admin = await CreateAdminUserAsync(db, "preview-settings@example.com", "Администратор");
        var client = await CreateClientAsync(db, "Клиент предварительного расчёта");
        client.ResponsibleUserId = admin.Id;
        var oldCase = await CreateCaseAsync(db, client.Id, "ДАВНО БЕЗ ДВИЖЕНИЯ");
        var recentCase = await CreateCaseAsync(db, client.Id, "НЕДАВНЕЕ ДЕЛО");
        var activeCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С АКТИВНОЙ ЗАДАЧЕЙ");
        var closedCase = await CreateCaseAsync(db, client.Id, "ЗАКРЫТОЕ ДЕЛО", ClientCaseStatus.Closed);
        var disabledCase = await CreateCaseAsync(db, client.Id, "АВТОКОНТРОЛЬ ОТКЛЮЧЁН");
        var vacationUser = await CreateAdminUserAsync(db, "preview-vacation@example.com", "Ответственный в отпуске");
        var vacationClient = await CreateClientAsync(db, "Клиент ответственного в отпуске");
        vacationClient.ResponsibleUserId = vacationUser.Id;
        var vacationCase = await CreateCaseAsync(db, vacationClient.Id, "ДЕЛО НА ПАУЗЕ ИЗ-ЗА ОТПУСКА");
        var unassignedClient = await CreateClientAsync(db, "Клиент без ответственного");
        var unassignedCase = await CreateCaseAsync(db, unassignedClient.Id, "ДЕЛО БЕЗ ИСПОЛНИТЕЛЯ");
        var now = DateTime.UtcNow;
        oldCase.CreatedAt = now.AddDays(-45);
        recentCase.CreatedAt = now.AddDays(-5);
        activeCase.CreatedAt = now.AddDays(-45);
        closedCase.CreatedAt = now.AddDays(-45);
        disabledCase.CreatedAt = now.AddDays(-45);
        disabledCase.InactivityRemindersEnabled = false;
        disabledCase.InactivityRemindersDisabledReason = "Ручное исключение";
        vacationCase.CreatedAt = now.AddDays(-45);
        unassignedCase.CreatedAt = now.AddDays(-45);
        await SetVacationAsync(db, vacationUser.Id, now.AddDays(5));
        await CreateTaskAsync(
            db,
            admin.Id,
            client.Id,
            assigneeUserId: admin.Id,
            clientCaseId: activeCase.Id,
            status: "new",
            createdAt: now.AddDays(-1));
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);
        interceptor.Commands.Clear();

        var result = await controller.PreviewCaseControlAutomation(
            inactivityDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationPreviewDto>(ok.Value);
        Assert.Equal(14, dto.InactivityDays);
        Assert.Equal(30, dto.EscalationDays);
        Assert.Equal(7, dto.TrackedCaseCount);
        Assert.Equal(1, dto.CandidateCount);
        Assert.Equal(1, dto.EscalationCandidateCount);
        Assert.Equal(1, dto.ExcludedByStatusCount);
        Assert.Equal(1, dto.PausedByVacationCount);
        Assert.Equal(1, dto.DisabledCount);
        Assert.Equal(1, dto.ActiveTaskCount);
        Assert.Equal(1, dto.UnassignedCount);
        Assert.Equal(1, dto.RecentlyActiveCount);
        Assert.Equal(
            dto.TrackedCaseCount,
            dto.CandidateCount + dto.ExcludedByStatusCount + dto.PausedByVacationCount +
            dto.DisabledCount + dto.ActiveTaskCount + dto.UnassignedCount + dto.RecentlyActiveCount);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewCaseControlAutomation_UsesUnsavedDefaultAssigneeAndExplainsMissingAssignee()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "preview-assignee@example.com", "Резервный исполнитель");
        var client = await CreateClientAsync(db, "Клиент без ответственного для preview");
        var firstCase = await CreateCaseAsync(db, client.Id, "ПЕРВОЕ ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        var secondCase = await CreateCaseAsync(db, client.Id, "ВТОРОЕ ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        var thirdCase = await CreateCaseAsync(db, client.Id, "ТРЕТЬЕ ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        firstCase.CreatedAt = DateTime.UtcNow.AddDays(-45);
        secondCase.CreatedAt = DateTime.UtcNow.AddDays(-44);
        thirdCase.CreatedAt = DateTime.UtcNow.AddDays(-43);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var withoutFallbackAction = await controller.PreviewCaseControlAutomation(
            inactivityDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None,
            defaultAssigneeUserId: 0,
            dailyLimit: 2);
        var withoutFallback = Assert.IsType<TaskJournalAutomationPreviewDto>(
            Assert.IsType<OkObjectResult>(withoutFallbackAction.Result).Value);
        Assert.Equal(0, withoutFallback.CandidateCount);
        Assert.Equal(3, withoutFallback.UnassignedCount);
        Assert.Equal(2, withoutFallback.DailyLimit);
        Assert.Equal(0, withoutFallback.NextRunCandidateCount);
        Assert.Equal(0, withoutFallback.DeferredByLimitCount);

        var withFallbackAction = await controller.PreviewCaseControlAutomation(
            inactivityDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None,
            defaultAssigneeUserId: admin.Id,
            dailyLimit: 2);
        var withFallback = Assert.IsType<TaskJournalAutomationPreviewDto>(
            Assert.IsType<OkObjectResult>(withFallbackAction.Result).Value);
        Assert.Equal(3, withFallback.CandidateCount);
        Assert.Equal(0, withFallback.UnassignedCount);
        Assert.Equal(2, withFallback.NextRunCandidateCount);
        Assert.Equal(1, withFallback.DeferredByLimitCount);
    }

    [Fact]
    public async Task GetCaseControlAutomationPreviewDetails_ExplainsEveryCategoryAndPaginatesOnDemand()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var admin = await CreateAdminUserAsync(db, "preview-details@example.com", "Администратор");
        var client = await CreateClientAsync(db, "Клиент детализации");
        client.ResponsibleUserId = admin.Id;
        var firstCandidate = await CreateCaseAsync(db, client.Id, "ЯВНО СРОЧНЫЙ КАНДИДАТ");
        var secondCandidate = await CreateCaseAsync(db, client.Id, "АЛФАВИТНЫЙ КАНДИДАТ");
        var recentCase = await CreateCaseAsync(db, client.Id, "НЕДАВНЕЕ ДЕЛО");
        var activeCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С АКТИВНОЙ ЗАДАЧЕЙ");
        var closedCase = await CreateCaseAsync(db, client.Id, "ЗАКРЫТОЕ ДЕЛО", ClientCaseStatus.Closed);
        var disabledCase = await CreateCaseAsync(db, client.Id, "АВТОКОНТРОЛЬ ОТКЛЮЧЁН");
        var vacationUser = await CreateAdminUserAsync(db, "preview-details-vacation@example.com", "Ответственный в отпуске");
        var vacationClient = await CreateClientAsync(db, "Клиент в отпуске");
        vacationClient.ResponsibleUserId = vacationUser.Id;
        var vacationCase = await CreateCaseAsync(db, vacationClient.Id, "ДЕЛО НА ПАУЗЕ");
        var unassignedClient = await CreateClientAsync(db, "Клиент детализации без ответственного");
        var unassignedCase = await CreateCaseAsync(db, unassignedClient.Id, "ДЕЛО БЕЗ ИСПОЛНИТЕЛЯ");
        var now = DateTime.UtcNow;
        firstCandidate.CreatedAt = now.AddDays(-45);
        secondCandidate.CreatedAt = now.AddDays(-20);
        recentCase.CreatedAt = now.AddDays(-5);
        activeCase.CreatedAt = now.AddDays(-45);
        closedCase.CreatedAt = now.AddDays(-45);
        disabledCase.CreatedAt = now.AddDays(-45);
        disabledCase.InactivityRemindersEnabled = false;
        disabledCase.InactivityRemindersDisabledReason = "Работа ведётся вне CRM";
        vacationCase.CreatedAt = now.AddDays(-45);
        unassignedCase.CreatedAt = now.AddDays(-45);
        await SetVacationAsync(db, vacationUser.Id, now.AddDays(5));
        await CreateTaskAsync(
            db,
            admin.Id,
            client.Id,
            assigneeUserId: admin.Id,
            clientCaseId: activeCase.Id,
            status: "new",
            createdAt: now.AddDays(-1));
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        async Task<TaskJournalAutomationPreviewDetailsDto> LoadAsync(
            string category,
            string? search = null,
            int limit = 20)
        {
            var action = await controller.GetCaseControlAutomationPreviewDetails(
                inactivityDays: 14,
                escalationDays: 30,
                category: category,
                search: search,
                offset: 0,
                limit: limit,
                ct: CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(action.Result);
            return Assert.IsType<TaskJournalAutomationPreviewDetailsDto>(ok.Value);
        }

        interceptor.Commands.Clear();
        var candidates = await LoadAsync("candidate", limit: 1);
        Assert.Equal(2, candidates.TotalCount);
        Assert.Single(candidates.Items);
        Assert.Equal(firstCandidate.Id, candidates.Items[0].ClientCaseId);
        Assert.Contains("срок постановки контроля наступил", candidates.Items[0].Explanation);
        Assert.Equal(
            DateTime.SpecifyKind(firstCandidate.CreatedAt.AddDays(14), DateTimeKind.Utc).ToString("O"),
            candidates.Items[0].FirstControlDueAt);
        Assert.Equal(
            DateTime.SpecifyKind(firstCandidate.CreatedAt.AddDays(30), DateTimeKind.Utc).ToString("O"),
            candidates.Items[0].CriticalControlDueAt);
        Assert.Equal(2, interceptor.Commands.Count(command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal)));

        var criticalCandidates = await LoadAsync("criticalCandidate");
        Assert.Equal(30, criticalCandidates.EscalationDays);
        Assert.Equal(firstCandidate.Id, Assert.Single(criticalCandidates.Items).ClientCaseId);
        Assert.Contains("критический порог 30 дн.", criticalCandidates.Items[0].Explanation);

        var recent = await LoadAsync("recentActivity");
        Assert.Equal(recentCase.Id, Assert.Single(recent.Items).ClientCaseId);
        Assert.Contains("порог ещё не наступил", recent.Items[0].Explanation);

        var active = await LoadAsync("activeTask");
        Assert.Equal(activeCase.Id, Assert.Single(active.Items).ClientCaseId);
        Assert.Contains("активные задачи: 1", active.Items[0].Explanation);

        var unassigned = await LoadAsync("unassigned");
        Assert.Equal(unassignedCase.Id, Assert.Single(unassigned.Items).ClientCaseId);
        Assert.Contains("нет активного ответственного", unassigned.Items[0].Explanation);

        var excluded = await LoadAsync("excludedStatus");
        Assert.Equal(closedCase.Id, Assert.Single(excluded.Items).ClientCaseId);
        Assert.Contains("Закрыто", excluded.Items[0].Explanation);

        var disabled = await LoadAsync("disabled");
        Assert.Equal(disabledCase.Id, Assert.Single(disabled.Items).ClientCaseId);
        Assert.Contains("Работа ведётся вне CRM", disabled.Items[0].Explanation);

        var vacation = await LoadAsync("vacation");
        Assert.Equal(vacationCase.Id, Assert.Single(vacation.Items).ClientCaseId);
        Assert.Contains("проверка продолжится после возвращения", vacation.Items[0].Explanation);

        var searched = await LoadAsync("candidate", "АЛФАВИТНЫЙ");
        Assert.Equal(1, searched.TotalCount);
        Assert.Equal(secondCandidate.Id, Assert.Single(searched.Items).ClientCaseId);
    }

    [Fact]
    public async Task GetCaseControlAutomationPreviewDetails_RejectsNonAdmin()
    {
        await using var db = await CreateContextAsync();
        var employee = await CreateTaskUserAsync(
            db,
            "preview-details-restricted@example.com",
            "Обычный сотрудник",
            canEdit: true,
            canCreate: true,
            viewScope: DataVisibilityScope.All);
        var controller = CreateController(db, new RecordingNotificationService(), employee);

        var result = await controller.GetCaseControlAutomationPreviewDetails(
            inactivityDays: 14,
            escalationDays: 30,
            category: "candidate",
            search: null,
            offset: 0,
            limit: 20,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task PreviewCaseControlAutomation_RejectsNonAdminWithTaskViewAccess()
    {
        await using var db = await CreateContextAsync();
        var employee = await CreateTaskUserAsync(
            db,
            "preview-restricted@example.com",
            "Обычный сотрудник",
            canEdit: true,
            canCreate: true,
            viewScope: DataVisibilityScope.All);
        var controller = CreateController(db, new RecordingNotificationService(), employee);

        var result = await controller.PreviewCaseControlAutomation(
            inactivityDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task PreviewCaseControlAutomation_HandlesTenThousandCasesWithOneAggregateQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var admin = await CreateAdminUserAsync(db, "preview-load@example.com", "Администратор");
        var client = await CreateClientAsync(db, "Клиент нагрузочной проверки");
        client.ResponsibleUserId = admin.Id;
        var createdAt = DateTime.UtcNow.AddDays(-45);
        var cases = Enumerable.Range(1, 10_000)
            .Select(index => new ClientCase
            {
                ClientId = client.Id,
                Title = $"НАГРУЗОЧНОЕ ДЕЛО {index}",
                Status = ClientCaseStatus.Open,
                CreatedAt = createdAt,
                InactivityRemindersEnabled = true,
            })
            .ToArray();
        db.ClientCases.AddRange(cases);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, new RecordingNotificationService(), admin);
        interceptor.Commands.Clear();
        var stopwatch = Stopwatch.StartNew();

        var result = await controller.PreviewCaseControlAutomation(
            inactivityDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None);

        stopwatch.Stop();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationPreviewDto>(ok.Value);
        Assert.Equal(10_000, dto.CandidateCount);
        Assert.Equal(10_000, dto.EscalationCandidateCount);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"ClientCases\"", StringComparison.Ordinal));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Preview 10 000 дел занял {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task GetCaseControlAutomationDiagnostics_AppliesCaseOwnerVisibility()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "diagnostics-own@example.com", "Ограниченный пользователь", canEdit: false);
        var otherResponsible = await CreateAdminUserAsync(db, "diagnostics-other@example.com", "Другой ответственный");
        var visibleClient = await CreateClientAsync(db, "Видимый клиент диагностики");
        visibleClient.ResponsibleUserId = current.Id;
        var hiddenClient = await CreateClientAsync(db, "Скрытый клиент диагностики");
        hiddenClient.ResponsibleUserId = otherResponsible.Id;
        var visibleCase = await CreateCaseAsync(db, visibleClient.Id, "ВИДИМОЕ ДЕЛО");
        var hiddenCase = await CreateCaseAsync(db, hiddenClient.Id, "СКРЫТОЕ ДЕЛО");
        var now = DateTime.UtcNow;
        visibleCase.CreatedAt = now.AddDays(-45);
        hiddenCase.CreatedAt = now.AddDays(-45);
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "tasks.automation.caseControl.enabled", Value = "true", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.inactiveDays", Value = "14", CreatedAt = now, UpdatedAt = now },
            new SystemSetting { Key = "tasks.automation.caseControl.escalationDays", Value = "30", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetCaseControlAutomationDiagnostics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationDiagnosticsDto>(ok.Value);
        Assert.Equal(1, dto.VisibleCandidateCount);
        Assert.Equal(1, dto.VisibleEscalationCandidateCount);
    }

    [Fact]
    public async Task CreateTask_AllowsInternalTaskWithoutClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var realtimePublisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            realtimePublisher: realtimePublisher);

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
        Assert.Equal(1, realtimePublisher.TaskJournalChangeCount);
    }

    [Fact]
    public async Task CreateTask_QueuesExternalCalendarSyncWithoutWaitingForProvider()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "task-calendar-outbox@example.com", "Автор");
        var outbox = new RecordingExternalCalendarTaskSyncOutbox();
        var coordinator = new ThrowingExternalCalendarSyncCoordinator();
        var controller = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            externalCalendarSyncCoordinator: coordinator,
            externalCalendarTaskSyncOutbox: outbox);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            Title = "Передать задачу в очередь календаря",
            AssigneeUserId = author.Id,
            DueAt = DateTime.UtcNow.AddHours(2),
            Participants =
            [
                new TaskJournalParticipantRequest
                {
                    UserId = author.Id,
                    Role = "assignee",
                },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal([dto.Id], outbox.TaskIds);
        Assert.Equal(0, coordinator.SyncCalls);
    }

    [Fact]
    public async Task CreateTask_SucceedsWhenExternalCalendarQueueIsUnavailable()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "task-calendar-outbox-failure@example.com", "Автор");
        var controller = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            externalCalendarTaskSyncOutbox: new ThrowingExternalCalendarTaskSyncOutbox());

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            Title = "Сохранить задачу при недоступной очереди календаря",
            AssigneeUserId = author.Id,
            DueAt = DateTime.UtcNow.AddHours(2),
            Participants =
            [
                new TaskJournalParticipantRequest
                {
                    UserId = author.Id,
                    Role = "assignee",
                },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.Id == dto.Id));
    }

    [Fact]
    public async Task GetSettings_ReturnsGlobalTaskRulesForAuthorizedUser()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "task-settings-read@example.com", "Администратор");
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.taskCardWindowMode",
            Value = "modern",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetSettings(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var settings = Assert.IsType<TaskJournalSettingsDto>(ok.Value);
        Assert.Equal("modern", settings.TaskCardWindowMode);
        Assert.True(settings.RequireCompletionResult);
    }

    [Fact]
    public async Task GetTasks_SortsByAssigneeOnServer()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "sort-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент сортировки");
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                ClientId = client.Id,
                ObjectName = client.Name,
                Title = "Задача Анны",
                AssigneeName = "Анна",
                AuthorUserId = author.Id,
                AuthorName = author.FullName,
                DueAt = DateTime.UtcNow.AddDays(2),
                Status = "new",
                Type = "task",
                Priority = "normal",
            },
            new TaskJournalItem
            {
                ClientId = client.Id,
                ObjectName = client.Name,
                Title = "Задача Сергея",
                AssigneeName = "Сергей",
                AuthorUserId = author.Id,
                AuthorName = author.FullName,
                DueAt = DateTime.UtcNow.AddDays(1),
                Status = "new",
                Type = "task",
                Priority = "normal",
            });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: "all",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            sortBy: "assignee",
            sortDir: "desc",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        Assert.Equal(["Сергей", "Анна"], page.Items.Select(item => item.AssigneeName).ToArray());
    }

    [Fact]
    public async Task GetTasks_StatusAndDueDateSortKeepStableOrderAcrossPages()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "stable-sort-author@example.com", "Автор сортировки");
        var dueAt = DateTime.UtcNow.Date.AddDays(1);
        var tasks = Enumerable.Range(1, 5)
            .Select(number => new TaskJournalItem
            {
                ObjectName = "Внутренняя задача",
                Title = $"Задача {number}",
                AssigneeName = "Исполнитель",
                AuthorUserId = author.Id,
                AuthorName = author.FullName,
                DueAt = dueAt,
                Status = number == 5 ? "done" : "new",
                Type = "task",
                Priority = "normal",
            })
            .ToList();
        db.TaskJournalItems.AddRange(tasks);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var firstResult = await controller.GetTasks(
            search: null,
            status: "new",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 2,
            sortBy: "dueAt",
            sortDir: "asc",
            ct: CancellationToken.None);
        var secondResult = await controller.GetTasks(
            search: null,
            status: "new",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 2,
            pageSize: 2,
            sortBy: "dueAt",
            sortDir: "asc",
            ct: CancellationToken.None);

        var firstPage = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(firstResult.Result).Value);
        var secondPage = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(secondResult.Result).Value);
        var expectedIds = tasks.Take(4).Select(task => task.Id).Order().ToArray();

        Assert.Equal(4, firstPage.Total);
        Assert.Equal(expectedIds[..2], firstPage.Items.Select(task => task.Id).ToArray());
        Assert.Equal(expectedIds[2..], secondPage.Items.Select(task => task.Id).ToArray());
    }

    [Fact]
    public async Task GetTasks_AppliesAuthorAndAdditionalAssigneeBeforePagination()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "server-filter-author@example.com", "Автор фильтра");
        var otherAuthor = await CreateAdminUserAsync(db, "server-filter-other@example.com", "Другой автор");
        var assignee = await CreateAdminUserAsync(db, "server-filter-assignee@example.com", "Дополнительный исполнитель");
        var expected = await CreateTaskAsync(db, author.Id, null, author.Id);
        expected.Title = "Нужная задача";
        expected.Participants.Add(new TaskJournalParticipant
        {
            UserId = assignee.Id,
            Role = "assignee",
            DisplayName = assignee.FullName,
            SortOrder = 1,
        });
        var excluded = await CreateTaskAsync(db, otherAuthor.Id, null, assignee.Id);
        excluded.Title = "Задача другого автора";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: "all",
            type: null,
            assigneeUserId: assignee.Id,
            from: null,
            to: null,
            authorUserId: author.Id,
            page: 1,
            pageSize: 1,
            ct: CancellationToken.None);

        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(expected.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetTasks_FiltersTasksByCurrentUserParticipantRoleBeforePagination()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateAdminUserAsync(db, "participant-filter@example.com", "Участник задачи");
        var author = await CreateAdminUserAsync(db, "participant-filter-author@example.com", "Постановщик");
        var observerTask = await CreateTaskAsync(db, author.Id, null, author.Id);
        observerTask.Title = "Задача наблюдателя";
        observerTask.Participants.Add(new TaskJournalParticipant
        {
            UserId = currentUser.Id,
            Role = "observer",
            DisplayName = currentUser.FullName,
            SortOrder = 1,
        });
        var approverTask = await CreateTaskAsync(db, author.Id, null, author.Id);
        approverTask.Title = "Задача согласующего";
        approverTask.Participants.Add(new TaskJournalParticipant
        {
            UserId = currentUser.Id,
            Role = "approver",
            DisplayName = currentUser.FullName,
            SortOrder = 1,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), currentUser);

        var result = await controller.GetTasks(
            search: null,
            status: "all",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            participantRole: "observer",
            page: 1,
            pageSize: 1,
            ct: CancellationToken.None);

        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(observerTask.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task GetTaskListSummary_CalculatesMetricsAndBillingAnalysisInDatabase()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "server-summary@example.com", "Администратор сводки");
        var now = DateTime.UtcNow;
        var todayFrom = now.AddHours(-1);
        var todayTo = now.AddHours(1);
        var invoice = new Payment
        {
            Date = now,
            PlannedDate = now,
            Amount = 1_000m,
            Description = "Тестовый счёт по задачам",
            DocumentKind = DocumentKind.Invoice,
        };
        db.Payments.Add(invoice);
        await db.SaveChangesAsync();
        var activeToday = await CreateTaskAsync(db, admin.Id, null, admin.Id);
        activeToday.DueAt = now.AddMinutes(30);
        activeToday.ClientBillingClassification = TaskClientBillingClassification.Billable;
        activeToday.BillingModeSnapshot = ContractBillingMode.Actual;
        var overdue = await CreateTaskAsync(db, admin.Id, null, admin.Id);
        overdue.DueAt = now.AddHours(-2);
        overdue.Priority = "urgent";
        overdue.ClientBillingClassification = TaskClientBillingClassification.Internal;
        var completed = await CreateTaskAsync(db, admin.Id, null, admin.Id, status: "done");
        completed.DueAt = now.AddMinutes(15);
        completed.Priority = "high";
        completed.ClientBillingClassification = TaskClientBillingClassification.Billable;
        completed.BillingModeSnapshot = ContractBillingMode.Actual;
        var cancelled = await CreateTaskAsync(db, admin.Id, null, admin.Id, status: "cancelled");
        cancelled.DueAt = now.AddMinutes(20);
        cancelled.ClientBillingClassification = TaskClientBillingClassification.Billable;
        cancelled.BillingModeSnapshot = ContractBillingMode.Actual;
        var fixedTask = await CreateTaskAsync(db, admin.Id, null, admin.Id);
        fixedTask.DueAt = now.AddMinutes(45);
        fixedTask.ClientBillingClassification = TaskClientBillingClassification.Billable;
        fixedTask.BillingModeSnapshot = ContractBillingMode.Fixed;
        var invoicedTask = await CreateTaskAsync(db, admin.Id, null, admin.Id);
        invoicedTask.DueAt = now.AddMinutes(50);
        invoicedTask.ClientBillingClassification = TaskClientBillingClassification.Billable;
        invoicedTask.BillingModeSnapshot = ContractBillingMode.Actual;
        invoicedTask.BillingInvoiceId = invoice.Id;
        var legacyTask = await CreateTaskAsync(db, admin.Id, null, admin.Id);
        legacyTask.DueAt = now.AddMinutes(55);
        legacyTask.ClientBillingClassification = TaskClientBillingClassification.Legacy;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        var result = await controller.GetTaskListSummary(
            search: null,
            status: "all",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            todayFrom: todayFrom,
            todayTo: todayTo,
            ct: CancellationToken.None);

        var summary = Assert.IsType<TaskJournalListSummaryDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(5, summary.Active);
        Assert.Equal(1, summary.Overdue);
        Assert.Equal(4, summary.Today);
        Assert.Equal(1, summary.HighPriority);
        Assert.Equal(2, summary.Unbilled);
        Assert.Equal(1, summary.InvoicesToPrepare);
        Assert.Equal(1, summary.Fixed);
        Assert.Equal(1, summary.Invoiced);
        Assert.Equal(1, summary.NonBillable);
        Assert.Equal(1, summary.Unclassified);
    }

    [Fact]
    public async Task TaskBillingClassificationBackfill_DistributesLegacyTasksByOwnerAndContract()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "billing-backfill@example.com", "Администратор распределения");
        var fixedClient = await CreateClientAsync(db, "Клиент фиксированного договора");
        var actualClient = await CreateClientAsync(db, "Клиент без договора");
        var otherActualClient = await CreateClientAsync(db, "Второй клиент без договора");
        var contract = new Contract
        {
            Number = "ДОГ-ФИКС-1",
            Date = DateTime.UtcNow.Date.AddDays(-10),
            Status = ContractStatus.Concluded,
            BillingMode = ContractBillingMode.Fixed,
        };
        contract.ClientContracts.Add(new ClientContract { ClientId = fixedClient.Id, Client = fixedClient });
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var fixedTask = await CreateTaskAsync(db, admin.Id, fixedClient.Id, admin.Id);
        var actualTask = await CreateTaskAsync(db, admin.Id, actualClient.Id, admin.Id);
        await CreateTaskAsync(db, admin.Id, actualClient.Id, admin.Id);
        await CreateTaskAsync(db, admin.Id, otherActualClient.Id, admin.Id);
        var internalTask = await CreateTaskAsync(db, admin.Id, null, admin.Id);

        var changed = await TaskBillingClassificationBackfillService.EnsureLegacyTasksClassifiedAsync(db);

        Assert.Equal(5, changed);
        Assert.Equal(TaskClientBillingClassification.Billable, fixedTask.ClientBillingClassification);
        Assert.Equal(contract.Id, fixedTask.BillingContractId);
        Assert.Equal(ContractBillingMode.Fixed, fixedTask.BillingModeSnapshot);
        Assert.Equal(TaskClientBillingClassification.Billable, actualTask.ClientBillingClassification);
        Assert.Null(actualTask.BillingContractId);
        Assert.Null(actualTask.BillingModeSnapshot);
        Assert.Equal(TaskClientBillingClassification.Internal, internalTask.ClientBillingClassification);
        Assert.NotNull(fixedTask.BillingDecisionAt);
        Assert.Equal(0, await TaskBillingClassificationBackfillService.EnsureLegacyTasksClassifiedAsync(db));
    }

    [Fact]
    public async Task GetTasks_AndSummaryUseFixedNumberOfTaskQueriesForLargeJournal()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var admin = await CreateAdminUserAsync(db, "server-query-budget@example.com", "Администратор нагрузки");
        var dueAt = DateTime.UtcNow.AddDays(1);
        db.TaskJournalItems.AddRange(Enumerable.Range(1, 120).Select(index => new TaskJournalItem
        {
            ObjectName = "Нагрузочная проверка",
            Title = $"Задача {index}",
            AuthorUserId = admin.Id,
            AuthorName = admin.FullName,
            AssigneeUserId = admin.Id,
            AssigneeName = admin.FullName,
            DueAt = dueAt.AddMinutes(index),
            Status = "new",
            Type = "task",
            Priority = "normal",
            ClientBillingClassification = TaskClientBillingClassification.Internal,
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var controller = CreateController(db, new RecordingNotificationService(), admin);

        interceptor.Commands.Clear();
        var listResult = await controller.GetTasks(
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            includeDeadlineChangeRequests: true,
            page: 3,
            pageSize: 25,
            ct: CancellationToken.None);

        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Equal(120, page.Total);
        Assert.Equal(25, page.Items.Count);
        // SQLite-тесты используют SplitQuery: страница и её участники читаются
        // двумя ограниченными запросами, ещё один запрос получает только COUNT.
        Assert.Equal(3, interceptor.Commands.Count(command =>
            command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal) &&
            !command.Contains("TaskJournalDeadlineChangeRequests", StringComparison.Ordinal)));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"TaskJournalDeadlineChangeRequests\"", StringComparison.Ordinal));

        interceptor.Commands.Clear();
        var summaryResult = await controller.GetTaskListSummary(
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            ct: CancellationToken.None);

        var summary = Assert.IsType<TaskJournalListSummaryDto>(
            Assert.IsType<OkObjectResult>(summaryResult.Result).Value);
        Assert.Equal(120, summary.Active);
        Assert.Equal(2, interceptor.Commands.Count(command =>
            command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetTaskListSummary_DoesNotCountTasksOutsideOwnVisibility()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(
            db,
            "summary-own-visibility@example.com",
            "Ограниченный сотрудник",
            canEdit: false);
        var other = await CreateTaskUserAsync(
            db,
            "summary-hidden-visibility@example.com",
            "Скрытый сотрудник",
            canEdit: false);
        var visible = await CreateTaskAsync(db, current.Id, null, current.Id);
        visible.ClientBillingClassification = TaskClientBillingClassification.Billable;
        visible.BillingModeSnapshot = ContractBillingMode.Actual;
        var hidden = await CreateTaskAsync(db, other.Id, null, other.Id);
        hidden.ClientBillingClassification = TaskClientBillingClassification.Billable;
        hidden.BillingModeSnapshot = ContractBillingMode.Actual;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetTaskListSummary(
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            ct: CancellationToken.None);

        var summary = Assert.IsType<TaskJournalListSummaryDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, summary.Active);
        Assert.Equal(1, summary.Unbilled);
    }

    [Fact]
    public async Task GetTasks_ApprovalScopeReturnsOnlyCurrentDecisionAndDeadlineReview()
    {
        await using var db = await CreateContextAsync();
        var reviewer = await CreateTaskUserAsync(
            db,
            "approval-scope-reviewer@example.com",
            "Текущий согласующий",
            canEdit: true,
            viewScope: DataVisibilityScope.All);
        var author = await CreateAdminUserAsync(db, "approval-scope-author@example.com", "Постановщик");
        var firstApprover = await CreateAdminUserAsync(db, "approval-scope-first@example.com", "Первый согласующий");

        var currentDecision = await CreateTaskAsync(db, author.Id, null, reviewer.Id, status: "onApproval");
        currentDecision.Title = "Текущее согласование";
        currentDecision.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            SortOrder = 0,
            ApprovalStatus = "approved",
        });
        currentDecision.Participants.Add(new TaskJournalParticipant
        {
            UserId = reviewer.Id,
            Role = "approver",
            DisplayName = reviewer.FullName,
            SortOrder = 1,
            ApprovalStatus = "pending",
        });

        var blockedDecision = await CreateTaskAsync(db, author.Id, null, reviewer.Id, status: "onApproval");
        blockedDecision.Title = "Ожидает первого согласующего";
        blockedDecision.Participants.Add(new TaskJournalParticipant
        {
            UserId = firstApprover.Id,
            Role = "approver",
            DisplayName = firstApprover.FullName,
            SortOrder = 0,
            ApprovalStatus = "pending",
        });
        blockedDecision.Participants.Add(new TaskJournalParticipant
        {
            UserId = reviewer.Id,
            Role = "approver",
            DisplayName = reviewer.FullName,
            SortOrder = 1,
            ApprovalStatus = "pending",
        });

        var deadlineReview = await CreateTaskAsync(db, author.Id, null, reviewer.Id);
        deadlineReview.Title = "Запрос переноса срока";
        deadlineReview.DeadlineChangeRequests.Add(new TaskJournalDeadlineChangeRequest
        {
            RequestedByUserId = author.Id,
            RequestedByName = author.FullName,
            ReviewerUserId = reviewer.Id,
            ReviewerName = reviewer.FullName,
            CurrentDueAt = deadlineReview.DueAt,
            RequestedDueAt = deadlineReview.DueAt.AddDays(1),
            Reason = "Нужно дождаться документа",
            Status = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), reviewer);

        var result = await controller.GetTasks(
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            approvalScope: true,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, item => item.Id == currentDecision.Id);
        Assert.Contains(page.Items, item => item.Id == deadlineReview.Id);
        Assert.DoesNotContain(page.Items, item => item.Id == blockedDecision.Id);
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
    public async Task QuickTask_RemainsUnassignedAndAppearsInUntriagedList()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "quick-untriaged@example.com", "Автор");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var createdResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            IsUntriaged = true,
            Title = "Срочная внутренняя задача",
            DueAt = DateTime.UtcNow.AddDays(3),
        }, CancellationToken.None);

        var created = Assert.IsType<TaskJournalItemDto>(Assert.IsType<CreatedResult>(createdResult.Result).Value);
        Assert.Null(created.AssigneeUserId);
        Assert.Equal(string.Empty, created.AssigneeName);
        Assert.Empty(created.Participants);

        var listResult = await controller.GetTasks(
            search: null, status: null, type: null, assigneeUserId: null, from: null, to: null,
            untriaged: true, pageSize: 1, ct: CancellationToken.None);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(created.Id, Assert.Single(page.Items).Id);

        var updatedResult = await controller.UpdateTask(created.Id, new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            IsUntriaged = true,
            Title = "Срочная задача уточнена",
            DueAt = DateTime.UtcNow.AddDays(4),
        }, CancellationToken.None);
        var updated = Assert.IsType<TaskJournalItemDto>(Assert.IsType<OkObjectResult>(updatedResult.Result).Value);
        Assert.Null(updated.AssigneeUserId);
        Assert.Equal(string.Empty, updated.AssigneeName);
        Assert.Empty(updated.Participants);
    }

    [Fact]
    public async Task QuickTask_CannotBeUntriagedWhenAssignedOrBoundToClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "quick-invalid@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        foreach (var request in new[]
        {
            new UpsertTaskJournalItemRequest { IsInternal = true, IsUntriaged = true, AssigneeUserId = author.Id, Title = "Назначенная", DueAt = DateTime.UtcNow.AddHours(2) },
            new UpsertTaskJournalItemRequest { IsInternal = true, IsUntriaged = true, ClientId = client.Id, Title = "Клиентская", DueAt = DateTime.UtcNow.AddHours(2) },
        })
        {
            var result = await controller.CreateTask(request, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        Assert.Empty(await db.TaskJournalItems.ToListAsync());
    }

    [Fact]
    public async Task QuickTask_UntriagedListRespectsTaskVisibility()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "quick-visible@example.com", "Автор", canEdit: false, canCreate: true);
        var other = await CreateTaskUserAsync(db, "quick-hidden@example.com", "Другой сотрудник", canEdit: false, canCreate: true);
        var authorController = CreateController(db, new RecordingNotificationService(), author);
        var createdResult = await authorController.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            IsUntriaged = true,
            Title = "Личная неразобранная задача",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createdResult.Result);

        var otherController = CreateController(db, new RecordingNotificationService(), other);
        var listResult = await otherController.GetTasks(
            search: null, status: null, type: null, assigneeUserId: null, from: null, to: null,
            untriaged: true, ct: CancellationToken.None);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task QuickTask_RequiresBillingDecisionWhenLinkedToClient()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "quick-billing@example.com", "Автор");
        var client = await CreateClientAsync(db, "Платный клиент");
        var clientCase = await CreateCaseAsync(db, client.Id, "Дело для срочной проверки");
        var controller = CreateController(db, new RecordingNotificationService(), author);
        var createdResult = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            IsInternal = true,
            IsUntriaged = true,
            Title = "Срочная проверка",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);
        var created = Assert.IsType<TaskJournalItemDto>(Assert.IsType<CreatedResult>(createdResult.Result).Value);

        var missingDecision = await controller.UpdateTask(created.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            Title = "Срочная проверка",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingDecision.Result);
        Assert.Null((await db.TaskJournalItems.SingleAsync(task => task.Id == created.Id)).ClientId);

        var updatedResult = await controller.UpdateTask(created.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ClientBillingClassification = TaskClientBillingClassification.Billable,
            Title = "Срочная проверка",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);
        var updated = Assert.IsType<TaskJournalItemDto>(Assert.IsType<OkObjectResult>(updatedResult.Result).Value);
        Assert.Equal(client.Id, updated.ClientId);
        Assert.Equal(TaskClientBillingClassification.Billable, updated.ClientBillingClassification);
        Assert.Equal("unbilled", updated.BillingStatus);
        Assert.NotNull((await db.TaskJournalItems.SingleAsync(task => task.Id == created.Id)).BillingDecisionAt);
    }

    [Fact]
    public async Task CreateTask_PersistsActualBillingDecisionAndReturnsContractContext()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "billing-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент по факту");
        var clientCase = await CreateCaseAsync(db, client.Id, "Дело с почасовой оплатой");
        var contract = new Contract
        {
            Number = "ACTUAL-15",
            Title = "Оплата фактических задач",
            Date = DateTime.UtcNow.Date,
            BillingMode = ContractBillingMode.Actual,
            Status = ContractStatus.Concluded,
        };
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        db.ClientContracts.Add(new ClientContract { ClientId = client.Id, ContractId = contract.Id });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var contextResult = await controller.GetBillingContext(client.Id, null, clientCase.Id, CancellationToken.None);
        var contextOk = Assert.IsType<OkObjectResult>(contextResult.Result);
        var context = Assert.IsType<TaskBillingContextDto>(contextOk.Value);
        var contractOption = Assert.Single(context.Contracts);
        Assert.Equal(contract.Id, contractOption.Id);
        Assert.Equal(ContractBillingMode.Actual, contractOption.BillingMode);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ClientBillingClassification = TaskClientBillingClassification.Billable,
            BillingContractId = contract.Id,
            Title = "Подготовить правовое заключение",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal(TaskClientBillingClassification.Billable, dto.ClientBillingClassification);
        Assert.Equal(contract.Id, dto.BillingContractId);
        Assert.Equal(ContractBillingMode.Actual, dto.BillingMode);
        Assert.Equal("unbilled", dto.BillingStatus);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == dto.Id);
        Assert.NotNull(stored.BillingDecisionAt);
        Assert.Null(stored.BillingInvoiceId);
    }

    [Fact]
    public async Task CreateTask_PersistsBillableDecisionWithoutContract()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "billing-without-contract-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент без договора");
        var clientCase = await CreateCaseAsync(db, client.Id, "Дело без договора");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var contextResult = await controller.GetBillingContext(client.Id, null, clientCase.Id, CancellationToken.None);
        var contextOk = Assert.IsType<OkObjectResult>(contextResult.Result);
        var context = Assert.IsType<TaskBillingContextDto>(contextOk.Value);
        Assert.Empty(context.Contracts);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ClientBillingClassification = TaskClientBillingClassification.Billable,
            BillingContractId = null,
            Title = "Платная работа без договора",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal(TaskClientBillingClassification.Billable, dto.ClientBillingClassification);
        Assert.Null(dto.BillingContractId);
        Assert.Equal("unbilled", dto.BillingStatus);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == dto.Id);
        Assert.Equal(TaskClientBillingClassification.Billable, stored.ClientBillingClassification);
        Assert.Null(stored.BillingContractId);
        Assert.NotNull(stored.BillingDecisionAt);
    }

    [Fact]
    public async Task CreateTask_InternalBillingDecisionKeepsClientCaseButDoesNotEnterInvoices()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "internal-billing-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент внутренней задачи");
        var clientCase = await CreateCaseAsync(db, client.Id, "Основное дело");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = clientCase.Id,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            Title = "Внутренняя проверка качества",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal(TaskClientBillingClassification.Internal, dto.ClientBillingClassification);
        Assert.Equal("internal", dto.BillingStatus);
    }

    [Fact]
    public async Task CreateTask_InternalBillingDecisionKeepsClientWithoutCaseAndDoesNotEnterInvoices()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "internal-client-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент без дела");
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            ClientCaseId = null,
            ClientBillingClassification = TaskClientBillingClassification.Internal,
            BillingContractId = 999_999,
            Title = "Неоплачиваемая задача клиента",
            DueAt = DateTime.UtcNow.AddHours(2),
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal(client.Id, dto.ClientId);
        Assert.Null(dto.ClientCaseId);
        Assert.False(dto.IsInternal);
        Assert.Equal(TaskClientBillingClassification.Internal, dto.ClientBillingClassification);
        Assert.Equal("internal", dto.BillingStatus);
        Assert.Null(dto.BillingContractId);
        Assert.Null(dto.BillingInvoiceId);
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
            Result = "Документы подготовлены и переданы на проверку.",
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
    public async Task CreateTask_OnApprovalWithoutApproversAddsAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "create-author-approval-fallback@example.com", "Постановщик");
        var assignee = await CreateAdminUserAsync(db, "create-assignee-approval-fallback@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Клиент создания с согласованием");
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, author);

        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Создать и отправить постановщику",
            AssigneeUserId = assignee.Id,
            DueAt = DateTime.UtcNow.AddHours(2),
            Status = "onApproval",
            Result = "Результат готов к проверке.",
            Participants = [],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.Equal("onApproval", dto.Status);
        var approver = Assert.Single(dto.Participants, participant => participant.Role == "approver");
        Assert.Equal(author.Id, approver.UserId);
        Assert.Equal("Постановщик", approver.DisplayName);
        Assert.Equal("pending", approver.ApprovalStatus);
        Assert.Contains(dto.Id, notifications.ApprovalRequestedTaskIds);

        var stored = await db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == dto.Id);
        Assert.Contains(stored.Participants, participant =>
            participant.Role == "approver" && participant.UserId == author.Id);
        Assert.Contains(stored.Comments, comment =>
            comment.Kind == "system"
            && comment.Text.Contains("постановщик автоматически добавлен", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateTask_DefaultsApprovalDueAtFromSettingsWhenApprovalStarts()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-default-approval@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-default-approval@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Портнягин Дмитрий Олегович");
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.approvalDueDays",
            Value = "3",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var before = DateTime.UtcNow;
        var result = await controller.CreateTask(new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Проверить документы",
            DueAt = DateTime.UtcNow.AddHours(2),
            Status = "onApproval",
            Result = "Документы проверены и переданы на согласование.",
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
        var after = DateTime.UtcNow;

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);
        Assert.NotNull(dto.ApprovalDueAt);

        var storedApprovalDueAt = await db.TaskJournalItems
            .Where(item => item.Id == dto.Id)
            .Select(item => item.ApprovalDueAt)
            .SingleAsync();
        Assert.NotNull(storedApprovalDueAt);
        Assert.InRange(storedApprovalDueAt.Value, before.AddDays(3).AddSeconds(-2), after.AddDays(3).AddSeconds(2));
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
    public async Task UpdateStatus_DoneCreatesExactlyOneRecurringTaskWithoutResultCommentsFilesOrPayment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "recurrence-author@example.com", "Постановщик повторения");
        var assignee = await CreateAdminUserAsync(db, "recurrence-assignee@example.com", "Исполнитель повторения");
        var approver = await CreateAdminUserAsync(db, "recurrence-approver@example.com", "Согласующий повторения");
        var client = await CreateClientAsync(db, "Клиент повторяющейся задачи");
        var nextDueAt = DateTime.UtcNow.AddDays(20);
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: null);
        task.Title = "Ежемесячно проверить документы";
        task.Description = "Рабочее описание должно перейти в следующее повторение.";
        task.ContractorPaymentEnabled = true;
        task.ContractorPaymentAmount = 12_500m;
        task.ContractorPaymentDueDate = DateTime.UtcNow.AddDays(10).Date;
        task.RecurrenceEnabled = true;
        task.RecurrenceFrequency = "monthly";
        task.RecurrenceInterval = 1;
        task.RecurrenceDayOfMonth = nextDueAt.Day;
        task.RecurrenceTimeMinutes = nextDueAt.Hour * 60 + nextDueAt.Minute;
        task.RecurrenceSkipWeekends = false;
        task.RecurrenceTimeZoneId = "UTC";
        task.RecurrenceNextAt = nextDueAt;
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "approved",
            Resolution = "Согласовано в текущем цикле.",
            RespondedAt = DateTime.UtcNow,
        });
        db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            Kind = "comment",
            Text = "Комментарий текущего цикла не должен копироваться.",
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, assignee);

        var completed = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Документы проверены.",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(completed.Result);
        var nextTask = await db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.RecurrencePreviousTaskId == task.Id);
        Assert.Equal("new", nextTask.Status);
        Assert.Equal(task.Title, nextTask.Title);
        Assert.Equal(task.Description, nextTask.Description);
        Assert.Equal(nextDueAt, nextTask.DueAt);
        Assert.Null(nextTask.Result);
        Assert.True(nextTask.RecurrenceEnabled);
        Assert.Equal(1, nextTask.RecurrenceSequence);
        Assert.False(nextTask.ContractorPaymentEnabled);
        Assert.Null(nextTask.ContractorPaymentAmount);
        Assert.Null(nextTask.ContractorPaymentDueDate);
        Assert.DoesNotContain(nextTask.Comments, item => item.Text.Contains("Комментарий текущего цикла", StringComparison.Ordinal));
        var nextApprover = Assert.Single(nextTask.Participants, item => item.Role == "approver");
        Assert.Equal("pending", nextApprover.ApprovalStatus);
        Assert.Null(nextApprover.Resolution);
        Assert.Null(nextApprover.RespondedAt);
        Assert.Contains(nextTask.Id, notifications.AssignedTaskIds);

        var repeatedCompletion = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Документы проверены.",
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(repeatedCompletion.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.RecurrencePreviousTaskId == task.Id));

        Assert.IsType<NoContentResult>(await controller.DeleteTask(nextTask.Id, CancellationToken.None));
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "inProgress",
        }, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Документы проверены повторно.",
        }, CancellationToken.None)).Result);
        Assert.Equal(1, await db.TaskJournalItems
            .IgnoreQueryFilters()
            .CountAsync(item => item.RecurrencePreviousTaskId == task.Id));
        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item => item.RecurrencePreviousTaskId == task.Id));
    }

    [Fact]
    public async Task UpdateStatus_OnApprovalWithoutResultRequiresResult()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-approval-result@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-approval-result@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент результата согласования");
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
            Status = "onApproval",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("результат", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task UpdateStatus_OnApprovalWithResultSavesResultAndStartsApproval()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-approval-result-ok@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-approval-result-ok@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент результата согласования");
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
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "onApproval",
            Result = "Работа выполнена, документы приложены к задаче.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("onApproval", dto.Status);
        Assert.Equal("Работа выполнена, документы приложены к задаче.", dto.Result);
        Assert.Contains(task.Id, notifications.ApprovalRequestedTaskIds);
    }

    [Fact]
    public async Task UpdateStatus_RevisionRequiresFutureDueAt()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-revision-status@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент статуса доработки");
        var task = await CreateTaskAsync(db, author.Id, client.Id, result: null);
        task.DueAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "revision",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("срок", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.True(stored.DueAt < DateTime.UtcNow);
    }

    [Fact]
    public async Task UpdateStatus_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "status-own@example.com", "Ограниченный пользователь", canEdit: true);
        var other = await CreateTaskUserAsync(db, "status-other@example.com", "Другой пользователь", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент статуса задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.UpdateStatus(hiddenTask.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Попытка закрыть скрытую задачу",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == hiddenTask.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task GetTask_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-read-own@example.com", "Ограниченный пользователь", canEdit: false);
        var other = await CreateTaskUserAsync(db, "task-read-other@example.com", "Другой пользователь", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент чтения задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetTask(hiddenTask.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
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

    [Theory]
    [InlineData("onApproval")]
    [InlineData("inProgress")]
    public async Task UpdateTask_RemovingLastApproverReturnsTaskToInProgress(string requestedStatus)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "approval-author@example.com", "Постановщик");
        var approver = await CreateAdminUserAsync(db, "approval-removed@example.com", "Ошибочный согласующий");
        var client = await CreateClientAsync(db, "Клиент согласования");
        var approvalDueAt = DateTime.UtcNow.AddDays(2);
        var task = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            author.Id,
            result: "Документ отправлен на согласование.",
            status: "onApproval",
            approvalDueAt: approvalDueAt);
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
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = task.Title,
            AssigneeUserId = author.Id,
            DueAt = task.DueAt,
            ApprovalDueAt = approvalDueAt,
            Status = requestedStatus,
            Result = task.Result,
            Participants = [],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("inProgress", dto.Status);
        Assert.Null(dto.ApprovalDueAt);
        Assert.DoesNotContain(dto.Participants, participant => participant.Role == "approver");

        var stored = await db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal("inProgress", stored.Status);
        Assert.Null(stored.ApprovalDueAt);
        Assert.DoesNotContain(stored.Participants, participant => participant.Role == "approver");
        Assert.Contains(stored.Comments, comment =>
            comment.Kind == "system"
            && comment.Text.Contains("Последний согласующий удалён", StringComparison.Ordinal)
            && comment.Text.Contains("возвращена в работу", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateTask_EnteringApprovalWithoutApproversAddsAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "update-author-approval-fallback@example.com", "Постановщик");
        var assignee = await CreateAdminUserAsync(db, "update-assignee-approval-fallback@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Клиент редактирования с согласованием");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, status: "inProgress");
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = task.Title,
            AssigneeUserId = assignee.Id,
            DueAt = task.DueAt,
            Status = "onApproval",
            Result = "Документы подготовлены.",
            Participants = [],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("onApproval", dto.Status);
        var approver = Assert.Single(dto.Participants, participant => participant.Role == "approver");
        Assert.Equal(author.Id, approver.UserId);
        Assert.Equal("pending", approver.ApprovalStatus);
        Assert.Contains(task.Id, notifications.ApprovalRequestedTaskIds);

        var stored = await db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Contains(stored.Participants, participant =>
            participant.Role == "approver" && participant.UserId == author.Id);
        Assert.Contains(stored.Comments, comment =>
            comment.Kind == "system"
            && comment.Text.Contains("постановщик автоматически добавлен", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateTask_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-update-own@example.com", "Ограниченный редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "task-update-other@example.com", "Другой редактор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент редактирования задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var originalTitle = hiddenTask.Title;
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.UpdateTask(hiddenTask.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Попытка изменить скрытую задачу",
            DueAt = DateTime.UtcNow.AddHours(3),
            Status = "inProgress",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        var stored = await db.TaskJournalItems.SingleAsync(item => item.Id == hiddenTask.Id);
        Assert.Equal(originalTitle, stored.Title);
        Assert.Equal("new", stored.Status);
    }

    [Fact]
    public async Task UpdateControl_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-control-own@example.com", "Ограниченный редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "task-control-other@example.com", "Другой редактор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент контроля задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.UpdateControl(hiddenTask.Id, new UpdateTaskJournalControlRequest
        {
            IsUnderControl = true,
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.Id == hiddenTask.Id);
        Assert.False(stored.IsUnderControl);
        Assert.DoesNotContain(stored.Participants, participant => participant.UserId == current.Id);
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
        var changeNotification = Assert.Single(notificationService.ChangedTasks);
        Assert.Contains(changeNotification.ChangeLabels, label =>
            label.Contains("Статус:", StringComparison.OrdinalIgnoreCase)
            && label.Contains("Новая", StringComparison.OrdinalIgnoreCase)
            && label.Contains("Готово", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(changeNotification.ChangeLabels, label =>
            label.Contains("Результат:", StringComparison.OrdinalIgnoreCase)
            && label.Contains("Документы подготовлены", StringComparison.OrdinalIgnoreCase));
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
    public async Task UpdateTask_AllowsAdminToChangeAuthorAndPendingDeadlineReviewer()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "author-change-admin@example.com", "Администратор");
        var oldAuthor = await CreateAdminUserAsync(db, "author-change-old@example.com", "Старый постановщик");
        var newAuthor = await CreateAdminUserAsync(db, "author-change-new@example.com", "Новый постановщик");
        var assignee = await CreateAdminUserAsync(db, "author-change-assignee@example.com", "Исполнитель");
        var client = await CreateClientAsync(db, "Клиент смены постановщика");
        var task = await CreateTaskAsync(db, oldAuthor.Id, client.Id, assignee.Id);
        db.TaskJournalDeadlineChangeRequests.Add(new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = oldAuthor.Id,
            ReviewerName = oldAuthor.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "Нужен дополнительный день.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, admin);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = task.Title,
            AssigneeUserId = assignee.Id,
            AuthorUserId = newAuthor.Id,
            DueAt = task.DueAt,
            Status = task.Status,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal(newAuthor.Id, dto.AuthorUserId);
        Assert.Equal("Новый постановщик", dto.AuthorName);

        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(newAuthor.Id, stored.AuthorUserId);
        Assert.Equal("Новый постановщик", stored.AuthorName);
        var pendingRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal(newAuthor.Id, pendingRequest.ReviewerUserId);
        Assert.Equal("Новый постановщик", pendingRequest.ReviewerName);
        Assert.Contains(await db.TaskJournalComments.Where(item => item.TaskJournalItemId == task.Id).ToListAsync(), item =>
            item.Kind == "system"
            && item.Text.Contains("Постановщик", StringComparison.OrdinalIgnoreCase)
            && item.Text.Contains("Новый постановщик", StringComparison.OrdinalIgnoreCase));
        var notification = Assert.Single(notificationService.ChangedTasks);
        Assert.Contains(oldAuthor.Id, notification.AdditionalRecipientUserIds);
    }

    [Fact]
    public async Task UpdateTask_ForbidsNonAdminFromChangingAuthor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "author-change-user@example.com", "Постановщик", canEdit: true);
        var newAuthor = await CreateTaskUserAsync(db, "author-change-target@example.com", "Другой постановщик", canEdit: true);
        var assignee = await CreateTaskUserAsync(db, "author-change-worker@example.com", "Исполнитель", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент запрета смены постановщика");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = task.Title,
            AssigneeUserId = assignee.Id,
            AuthorUserId = newAuthor.Id,
            DueAt = task.DueAt,
            Status = task.Status,
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(author.Id, stored.AuthorUserId);
        Assert.Equal(task.AuthorName, stored.AuthorName);
    }

    [Fact]
    public async Task UpdateTask_WhenAssigneeChanges_NotifiesReassignment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "reassign-author@example.com", "Автор");
        var oldAssignee = await CreateAdminUserAsync(db, "old-assignee@example.com", "Старый исполнитель");
        var newAssignee = await CreateAdminUserAsync(db, "new-assignee@example.com", "Новый исполнитель");
        var client = await CreateClientAsync(db, "Сидоров Семен Семенович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, oldAssignee.Id);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = task.Title,
            AssigneeUserId = newAssignee.Id,
            DueAt = task.DueAt,
            Status = task.Status,
            ChangeComment = "Передали задачу другому исполнителю.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Empty(notificationService.ChangedTasks);
        var reassignment = Assert.Single(notificationService.ReassignedTasks);
        Assert.Equal(task.Id, reassignment.TaskId);
        Assert.Equal(oldAssignee.Id, reassignment.PreviousAssigneeUserId);
        Assert.Equal("Исполнитель", reassignment.PreviousAssigneeName);
        Assert.Equal("Передали задачу другому исполнителю.", reassignment.Comment);
        Assert.Contains(reassignment.ChangeLabels, item =>
            item.Contains("Исполнитель:", StringComparison.OrdinalIgnoreCase)
            && item.Contains("Исполнитель", StringComparison.OrdinalIgnoreCase)
            && item.Contains("Новый исполнитель", StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData("done")]
    [InlineData("onApproval")]
    public async Task UpdateStatus_ForbidsObserverCompletionAndApprovalSubmissionEvenWhenRoleCanEdit(string nextStatus)
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
            Status = nextStatus,
            Result = "Observer result",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
    }

    [Fact]
    public async Task OtherTaskPermissions_EditDoesNotGrantCompletionOrDeletion()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db, "other-task-owner-edit@example.com", "Владелец задачи");
        var specialist = await CreateTaskUserAsync(
            db,
            "other-task-editor@example.com",
            "Редактор чужих задач",
            canEdit: false,
            viewScope: DataVisibilityScope.All,
            canEditOthers: true);
        var client = await CreateClientAsync(db, "Клиент чужой задачи для редактирования");
        var task = await CreateTaskAsync(db, owner.Id, client.Id, owner.Id);
        var controller = CreateController(db, new RecordingNotificationService(), specialist);

        var update = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Чужая задача отредактирована",
            DueAt = task.DueAt,
            Status = "new",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(update.Result);

        var statusChange = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "inProgress",
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(statusChange.Result);

        var controlChange = await controller.UpdateControl(task.Id, new UpdateTaskJournalControlRequest
        {
            IsUnderControl = true,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(controlChange.Result);

        var completion = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Попытка завершить чужую задачу.",
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(completion.Result);

        var deletion = await controller.DeleteTask(task.Id, CancellationToken.None);
        Assert.IsType<ForbidResult>(deletion);
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.Id == task.Id));
    }

    [Fact]
    public async Task OtherTaskPermissions_BaseEditDoesNotBypassForeignTaskRules()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db, "other-task-owner-base-edit@example.com", "Владелец задачи");
        var specialist = await CreateTaskUserAsync(
            db,
            "other-task-base-editor@example.com",
            "Специалист без прав на чужие задачи",
            canEdit: true,
            viewScope: DataVisibilityScope.All);
        var client = await CreateClientAsync(db, "Клиент чужой задачи без специальных прав");
        var task = await CreateTaskAsync(db, owner.Id, client.Id, owner.Id);
        var controller = CreateController(db, new RecordingNotificationService(), specialist);

        var statusChange = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "inProgress",
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(statusChange.Result);

        var controlChange = await controller.UpdateControl(task.Id, new UpdateTaskJournalControlRequest
        {
            IsUnderControl = true,
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(controlChange.Result);

        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.False(stored.IsUnderControl);
    }

    [Fact]
    public async Task OtherTaskPermissions_CompletionDoesNotGrantEditingOrDeletion()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db, "other-task-owner-complete@example.com", "Владелец задачи");
        var specialist = await CreateTaskUserAsync(
            db,
            "other-task-completer@example.com",
            "Завершающий чужие задачи",
            canEdit: false,
            viewScope: DataVisibilityScope.All,
            canCompleteOthers: true);
        var client = await CreateClientAsync(db, "Клиент чужой задачи для завершения");
        var task = await CreateTaskAsync(db, owner.Id, client.Id, owner.Id);
        var controller = CreateController(db, new RecordingNotificationService(), specialist);

        var update = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Недопустимое редактирование",
            DueAt = task.DueAt,
            Status = "new",
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(update.Result);

        var completion = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Чужая задача завершена по отдельному праву.",
        }, CancellationToken.None);
        var completed = Assert.IsType<OkObjectResult>(completion.Result);
        Assert.Equal("done", Assert.IsType<TaskJournalItemDto>(completed.Value).Status);

        var deletion = await controller.DeleteTask(task.Id, CancellationToken.None);
        Assert.IsType<ForbidResult>(deletion);
    }

    [Fact]
    public async Task OtherTaskPermissions_DeletionDoesNotGrantEditingOrCompletion()
    {
        await using var db = await CreateContextAsync();
        var owner = await CreateAdminUserAsync(db, "other-task-owner-delete@example.com", "Владелец задачи");
        var specialist = await CreateTaskUserAsync(
            db,
            "other-task-deleter@example.com",
            "Удаляющий чужие задачи",
            canEdit: false,
            viewScope: DataVisibilityScope.All,
            canDeleteOthers: true);
        var client = await CreateClientAsync(db, "Клиент чужой задачи для удаления");
        var task = await CreateTaskAsync(db, owner.Id, client.Id, owner.Id);
        var controller = CreateController(db, new RecordingNotificationService(), specialist);

        var update = await controller.UpdateTask(task.Id, new UpsertTaskJournalItemRequest
        {
            ClientId = client.Id,
            Title = "Недопустимое редактирование",
            DueAt = task.DueAt,
            Status = "new",
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(update.Result);

        var completion = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Недопустимое завершение.",
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(completion.Result);

        var deletion = await controller.DeleteTask(task.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deletion);
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.Id == task.Id));
    }

    [Fact]
    public async Task UpdateStatus_AllowsExecutorToSaveResultWithoutEditPermission()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: null);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.executorCompletionAllowedUserIds",
            Value = $"[{assignee.Id}]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
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
    public async Task UpdateStatus_OnApprovalAddsAuthorWhenTaskHasNoApprovers()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-fallback-approval@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "assignee-fallback-approval@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент согласования с постановщиком");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, status: "inProgress");
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, assignee);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "onApproval",
            Result = "Заявление подготовлено и передано постановщику.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("onApproval", dto.Status);
        var approver = Assert.Single(dto.Participants, item => item.Role == "approver");
        Assert.Equal(author.Id, approver.UserId);
        Assert.Equal("Постановщик", approver.DisplayName);
        Assert.Equal("pending", approver.ApprovalStatus);
        Assert.Contains(task.Id, notifications.ApprovalRequestedTaskIds);

        var storedApprover = await db.TaskJournalParticipants
            .AsNoTracking()
            .SingleAsync(item => item.TaskJournalItemId == task.Id && item.Role == "approver");
        Assert.Equal(author.Id, storedApprover.UserId);
    }

    [Fact]
    public async Task UpdateStatus_ForbidsExecutorCompletionWithoutPersonalAccess()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-no-completion-access@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "assignee-no-completion-access@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент без права завершения");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, result: null);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Попытка завершения.",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        var stored = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal("new", stored.Status);
        Assert.Null(stored.Result);
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
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, assignee);

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

        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);
        Assert.Equal("pending", deadlineRequest.Status);
        Assert.Equal(originalDueAt, deadlineRequest.CurrentDueAt);
        Assert.Equal(requestedDueAt, deadlineRequest.RequestedDueAt);
        Assert.Equal("Жду документы от клиента.", deadlineRequest.Reason);
        Assert.Equal(assignee.Id, deadlineRequest.RequestedByUserId);
        Assert.Equal("Исполнитель", deadlineRequest.RequestedByName);
        Assert.Equal(author.Id, deadlineRequest.ReviewerUserId);
        Assert.Equal("Автор", deadlineRequest.ReviewerName);
        Assert.Equal(comment.Id, deadlineRequest.CreatedCommentId);
        Assert.Equal([task.Id], notifications.DeadlineChangeRequestedTaskIds);
    }

    [Fact]
    public async Task CreateDeadlineChangeRequest_RequiresReason()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-reason-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-reason-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент причины переноса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "   ",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("причину", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.TaskJournalComments.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.False(await db.TaskJournalDeadlineChangeRequests.AnyAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task CreateDeadlineChangeRequest_RejectsWhenSettingDisabled()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-disabled-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-disabled-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент отключенного переноса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.allowExecutorDeadlineChangeRequests",
            Value = "false",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, assignee);

        var result = await controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "Нужно больше времени.",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("отключен", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.TaskJournalComments.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.False(await db.TaskJournalDeadlineChangeRequests.AnyAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Empty(notifications.DeadlineChangeRequestedTaskIds);
    }

    [Fact]
    public async Task CreateDeadlineChangeRequest_RejectsDuplicatePendingRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-duplicate-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-duplicate-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент повторного переноса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var firstResult = await controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "Нужны дополнительные материалы.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(firstResult.Result);

        var secondResult = await controller.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = task.DueAt.AddDays(2),
            Reason = "Пробую отправить второй запрос.",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(secondResult.Result);
        Assert.Contains("активный запрос", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.TaskJournalDeadlineChangeRequests.CountAsync(item => item.TaskJournalItemId == task.Id));
        Assert.Equal(1, await db.TaskJournalComments.CountAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task GetDeadlineChangeRequests_ReturnsVisibleTaskRequests()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-list-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-list-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент списка переносов");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var requestedDueAt = task.DueAt.AddDays(2);
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = requestedDueAt,
            Reason = "Нужно дождаться документы.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);

        var authorController = CreateController(db, new RecordingNotificationService(), author);
        var result = await authorController.GetDeadlineChangeRequests(task.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalDeadlineChangeRequestDto>>(ok.Value);
        var item = Assert.Single(items);
        Assert.Equal(task.Id, item.TaskJournalItemId);
        Assert.Equal("pending", item.Status);
        Assert.Equal(assignee.Id, item.RequestedByUserId);
        Assert.Equal("Исполнитель", item.RequestedByName);
        Assert.Equal(author.Id, item.ReviewerUserId);
        Assert.Equal("Автор", item.ReviewerName);
        Assert.Equal("Нужно дождаться документы.", item.Reason);
        Assert.Equal(DateTime.SpecifyKind(requestedDueAt, DateTimeKind.Utc).ToString("O"), item.RequestedDueAt);
        Assert.Null(item.DecidedAt);
    }

    [Fact]
    public async Task GetDeadlineChangeRequests_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "deadline-list-hidden-current@example.com", "Ограниченный исполнитель", canEdit: false);
        var other = await CreateTaskUserAsync(db, "deadline-list-hidden-other@example.com", "Другой исполнитель", canEdit: false);
        var author = await CreateAdminUserAsync(db, "deadline-list-hidden-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент скрытого списка переносов");
        var hiddenTask = await CreateTaskAsync(db, author.Id, client.Id, other.Id);
        db.TaskJournalDeadlineChangeRequests.Add(new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = hiddenTask.Id,
            RequestedByUserId = other.Id,
            RequestedByName = "Другой исполнитель",
            ReviewerUserId = author.Id,
            ReviewerName = "Автор",
            CurrentDueAt = hiddenTask.DueAt,
            RequestedDueAt = hiddenTask.DueAt.AddDays(1),
            Reason = "Скрытый запрос.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetDeadlineChangeRequests(hiddenTask.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetDeadlineChangeRequest_ReturnsExactPersistedDates()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-item-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-item-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент точного запроса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var currentDueAt = task.DueAt;
        var requestedDueAt = currentDueAt.AddDays(3);
        var deadlineRequest = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = currentDueAt,
            RequestedDueAt = requestedDueAt,
            Reason = "Требуется дополнительное время.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
        };
        db.TaskJournalDeadlineChangeRequests.Add(deadlineRequest);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetDeadlineChangeRequest(task.Id, deadlineRequest.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalDeadlineChangeRequestDto>(ok.Value);
        Assert.Equal(deadlineRequest.Id, dto.Id);
        Assert.Equal(DateTime.SpecifyKind(currentDueAt, DateTimeKind.Utc).ToString("O"), dto.CurrentDueAt);
        Assert.Equal(DateTime.SpecifyKind(requestedDueAt, DateTimeKind.Utc).ToString("O"), dto.RequestedDueAt);
    }

    [Fact]
    public async Task GetTasks_IncludesActiveDeadlineChangeRequestsWhenRequested()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-approvals-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-approvals-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент для согласования срока");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var createdAt = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-10), DateTimeKind.Utc);
        db.TaskJournalDeadlineChangeRequests.Add(new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "Нужно дождаться документы от клиента.",
            Status = "pending",
            CreatedAt = createdAt,
        });
        db.TaskJournalDeadlineChangeRequests.Add(new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = assignee.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(2),
            Reason = "Старый обработанный запрос.",
            Status = "rejected",
            CreatedAt = createdAt.AddMinutes(-1),
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
            includeDeadlineChangeRequests: true,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        var dto = Assert.Single(page.Items, item => item.Id == task.Id);
        var request = Assert.Single(dto.ActiveDeadlineChangeRequests);
        Assert.Equal("pending", request.Status);
        Assert.Equal("Нужно дождаться документы от клиента.", request.Reason);
        Assert.Equal(author.FullName, request.ReviewerName);
    }

    [Fact]
    public async Task ApproveDeadlineChangeRequest_UpdatesDueAtAndClosesRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-approve-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-approve-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент согласования переноса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var requestedDueAt = originalDueAt.AddDays(2);
        var approvedDueAt = originalDueAt.AddDays(3);
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = requestedDueAt,
            Reason = "Не успеваю получить документы.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);
        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);
        var notifications = new RecordingNotificationService();
        var authorController = CreateController(db, notifications, author);

        var approveResult = await authorController.ApproveDeadlineChangeRequest(task.Id, deadlineRequest.Id, new ApproveTaskJournalDeadlineChangeRequest
        {
            ApprovedDueAt = approvedDueAt,
            Comment = "Согласовано до новой даты.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(approveResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal(DateTime.SpecifyKind(approvedDueAt, DateTimeKind.Utc).ToString("O"), dto.DueAt);

        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(approvedDueAt, storedTask.DueAt);

        var storedRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == deadlineRequest.Id);
        Assert.Equal("approved", storedRequest.Status);
        Assert.Equal(approvedDueAt, storedRequest.DecisionDueAt);
        Assert.Equal("Согласовано до новой даты.", storedRequest.DecisionComment);
        Assert.Equal(author.Id, storedRequest.DecidedByUserId);
        Assert.Equal("Автор", storedRequest.DecidedByName);
        Assert.NotNull(storedRequest.DecidedAt);
        Assert.NotNull(storedRequest.DecisionCommentId);

        var comments = await db.TaskJournalComments.AsNoTracking()
            .Where(item => item.TaskJournalItemId == task.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, comments.Count);
        Assert.Contains("согласовал перенос срока", comments[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Согласовано до новой даты", comments[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([task.Id], notifications.DeadlineChangeApprovedTaskIds);
        Assert.Empty(notifications.ChangedTasks);
    }

    [Fact]
    public async Task ApproveDeadlineChangeRequest_ForbidsExecutor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-approve-forbid-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-approve-forbid-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент запрета согласования");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = originalDueAt.AddDays(1),
            Reason = "Нужно больше времени.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);
        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);

        var approveResult = await assigneeController.ApproveDeadlineChangeRequest(task.Id, deadlineRequest.Id, new ApproveTaskJournalDeadlineChangeRequest
        {
            ApprovedDueAt = originalDueAt.AddDays(1),
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(approveResult.Result);
        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(originalDueAt, storedTask.DueAt);
        var storedRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == deadlineRequest.Id);
        Assert.Equal("pending", storedRequest.Status);
    }

    [Fact]
    public async Task ApproveDeadlineChangeRequest_RejectsAlreadyProcessedRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-approve-processed-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-approve-processed-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент повторного согласования");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var deadlineRequest = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = assignee.Id,
            RequestedByName = "Исполнитель",
            ReviewerUserId = author.Id,
            ReviewerName = "Автор",
            CurrentDueAt = originalDueAt,
            RequestedDueAt = originalDueAt.AddDays(1),
            Reason = "Уже обработано.",
            Status = "approved",
            DecisionDueAt = originalDueAt.AddDays(1),
            DecidedByUserId = author.Id,
            DecidedByName = "Автор",
            DecidedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.TaskJournalDeadlineChangeRequests.Add(deadlineRequest);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ApproveDeadlineChangeRequest(task.Id, deadlineRequest.Id, new ApproveTaskJournalDeadlineChangeRequest
        {
            ApprovedDueAt = originalDueAt.AddDays(2),
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("уже обработан", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(originalDueAt, storedTask.DueAt);
    }

    [Fact]
    public async Task RejectDeadlineChangeRequest_KeepsDueAtAndClosesRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-reject-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-reject-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент отказа переноса");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var requestedDueAt = originalDueAt.AddDays(2);
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = requestedDueAt,
            Reason = "Не хватает документов.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);
        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);
        var notifications = new RecordingNotificationService();
        var authorController = CreateController(db, notifications, author);

        var rejectResult = await authorController.RejectDeadlineChangeRequest(task.Id, deadlineRequest.Id, new RejectTaskJournalDeadlineChangeRequest
        {
            Comment = "Срок менять нельзя.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(rejectResult.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal(DateTime.SpecifyKind(originalDueAt, DateTimeKind.Utc).ToString("O"), dto.DueAt);

        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(originalDueAt, storedTask.DueAt);

        var storedRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == deadlineRequest.Id);
        Assert.Equal("rejected", storedRequest.Status);
        Assert.Null(storedRequest.DecisionDueAt);
        Assert.Equal("Срок менять нельзя.", storedRequest.DecisionComment);
        Assert.Equal(author.Id, storedRequest.DecidedByUserId);
        Assert.Equal("Автор", storedRequest.DecidedByName);
        Assert.NotNull(storedRequest.DecidedAt);
        Assert.NotNull(storedRequest.DecisionCommentId);

        var comments = await db.TaskJournalComments.AsNoTracking()
            .Where(item => item.TaskJournalItemId == task.Id)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, comments.Count);
        Assert.Contains("отказал в переносе срока", comments[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([task.Id], notifications.DeadlineChangeRejectedTaskIds);
        Assert.Empty(notifications.ChangedTasks);
        Assert.Contains("Срок менять нельзя", comments[1].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectDeadlineChangeRequest_RequiresComment()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-reject-comment-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-reject-comment-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент причины отказа");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = originalDueAt.AddDays(1),
            Reason = "Нужно больше времени.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);
        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);
        var authorController = CreateController(db, new RecordingNotificationService(), author);

        var rejectResult = await authorController.RejectDeadlineChangeRequest(task.Id, deadlineRequest.Id, new RejectTaskJournalDeadlineChangeRequest
        {
            Comment = "   ",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(rejectResult.Result);
        Assert.Contains("причину отказа", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(originalDueAt, storedTask.DueAt);
        var storedRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == deadlineRequest.Id);
        Assert.Equal("pending", storedRequest.Status);
        Assert.Equal(1, await db.TaskJournalComments.CountAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task RejectDeadlineChangeRequest_ForbidsExecutor()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "deadline-reject-forbid-author@example.com", "Автор");
        var assignee = await CreateTaskUserAsync(db, "deadline-reject-forbid-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент запрета отказа");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var originalDueAt = task.DueAt;
        var assigneeController = CreateController(db, new RecordingNotificationService(), assignee);

        var createResult = await assigneeController.CreateDeadlineChangeRequest(task.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = originalDueAt.AddDays(1),
            Reason = "Нужно больше времени.",
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(createResult.Result);
        var deadlineRequest = await db.TaskJournalDeadlineChangeRequests.SingleAsync(item => item.TaskJournalItemId == task.Id);

        var rejectResult = await assigneeController.RejectDeadlineChangeRequest(task.Id, deadlineRequest.Id, new RejectTaskJournalDeadlineChangeRequest
        {
            Comment = "Отказываю сам себе.",
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(rejectResult.Result);
        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(originalDueAt, storedTask.DueAt);
        var storedRequest = await db.TaskJournalDeadlineChangeRequests.AsNoTracking().SingleAsync(item => item.Id == deadlineRequest.Id);
        Assert.Equal("pending", storedRequest.Status);
    }

    [Fact]
    public async Task CreateDeadlineChangeRequest_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "deadline-own@example.com", "Ограниченный исполнитель", canEdit: false);
        var other = await CreateTaskUserAsync(db, "deadline-other@example.com", "Другой исполнитель", canEdit: false);
        var author = await CreateAdminUserAsync(db, "deadline-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент переноса срока");
        var hiddenTask = await CreateTaskAsync(db, author.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.CreateDeadlineChangeRequest(hiddenTask.Id, new CreateTaskJournalDeadlineChangeRequest
        {
            RequestedDueAt = hiddenTask.DueAt.AddDays(1),
            Reason = "Попытка переноса скрытой задачи",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await db.TaskJournalComments.AnyAsync(item => item.TaskJournalItemId == hiddenTask.Id));
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
    public async Task TaskReadEndpoints_DoNotTrackEntitiesOrLoadUnusedUserProfiles()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "task-read-author@example.com", "Автор чтения");
        var assignee = await CreateAdminUserAsync(db, "task-read-assignee@example.com", "Исполнитель чтения");
        var observer = await CreateAdminUserAsync(db, "task-read-observer@example.com", "Наблюдатель чтения");
        var client = await CreateClientAsync(db, "Клиент чтения");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = observer.Id,
            Role = "observer",
            DisplayName = observer.FullName,
            Email = observer.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        db.ChangeTracker.Clear();
        interceptor.Commands.Clear();
        var listResult = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);
        var listPage = Assert.IsType<PagedResponse<TaskJournalItemDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var listItem = Assert.Single(listPage.Items);

        Assert.Equal(task.Id, listItem.Id);
        Assert.Equal(assignee.Id, listItem.AssigneeUserId);
        Assert.Equal(observer.Id, Assert.Single(listItem.Participants).UserId);
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalItem>());
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalParticipant>());
        var listDataQueries = interceptor.Commands
            .Where(command => command.Contains("TaskJournalItems", StringComparison.Ordinal) &&
                              command.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(listDataQueries);
        Assert.All(listDataQueries, command => Assert.DoesNotContain("JOIN \"Users\"", command, StringComparison.Ordinal));

        db.ChangeTracker.Clear();
        interceptor.Commands.Clear();
        var detailResult = await controller.GetTask(task.Id, CancellationToken.None);
        var detail = Assert.IsType<TaskJournalItemDto>(Assert.IsType<OkObjectResult>(detailResult.Result).Value);
        Assert.Equal(task.Id, detail.Id);
        Assert.Equal(observer.Id, Assert.Single(detail.Participants).UserId);
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalItem>());
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalParticipant>());

        db.ChangeTracker.Clear();
        interceptor.Commands.Clear();
        var notificationsResult = await controller.GetNotifications(CancellationToken.None);
        var notifications = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalNotificationDto>>(
            Assert.IsType<OkObjectResult>(notificationsResult.Result).Value);
        Assert.Contains(notifications, item => item.TaskId == task.Id);
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalItem>());
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalParticipant>());
        var notificationTaskQueries = interceptor.Commands
            .Where(command => command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, notificationTaskQueries.Count);
        Assert.All(notificationTaskQueries, command =>
        {
            Assert.DoesNotContain("JOIN \"Users\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("JOIN \"Clients\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("JOIN \"TaskJournalParticipants\"", command, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TaskReadEndpoints_ProjectOnlyFieldsRequiredByResponse()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "task-projection-author@example.com", "Projection author");
        var client = await CreateClientAsync(db, "Projection client");
        client.Notes = "unused client notes";
        var legalEntity = await CreateLegalEntityAsync(db, "Projection LLC");
        legalEntity.FullName = "Projection Limited Liability Company";
        legalEntity.LogoDataUrl = "data:image/png;base64,unused";
        var clientCase = await CreateCaseAsync(db, client.Id, "Projection case");
        clientCase.CourtUrl = "https://example.test/unused";
        var contract = new Contract
        {
            Number = "READ-42",
            Date = DateTime.UtcNow.Date,
            TerminationFinancialTerms = "unused contract terms",
        };
        var payment = new Payment
        {
            Account = "INV-42",
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Status = PaymentStatus.Completed,
            SystemNotes = "unused payment notes",
        };
        db.AddRange(contract, payment);
        await db.SaveChangesAsync();
        var task = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: clientCase.Id,
            legalEntityId: legalEntity.Id);
        task.BillingContractId = contract.Id;
        task.BillingInvoiceId = payment.Id;
        task.ContractorPaymentId = payment.Id;
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        db.ChangeTracker.Clear();
        interceptor.Commands.Clear();
        var listResult = await controller.GetTasks(
            search: null,
            status: null,
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);
        var listPage = Assert.IsType<PagedResponse<TaskJournalItemDto>>(
            Assert.IsType<OkObjectResult>(listResult.Result).Value);
        AssertProjectedTask(Assert.Single(listPage.Items));
        AssertTaskReadQueriesAreMinimal(interceptor.Commands);

        db.ChangeTracker.Clear();
        interceptor.Commands.Clear();
        var detailResult = await controller.GetTask(task.Id, CancellationToken.None);
        AssertProjectedTask(Assert.IsType<TaskJournalItemDto>(
            Assert.IsType<OkObjectResult>(detailResult.Result).Value));
        AssertTaskReadQueriesAreMinimal(interceptor.Commands);
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalItem>());
        Assert.Empty(db.ChangeTracker.Entries<TaskJournalParticipant>());

        void AssertProjectedTask(TaskJournalItemDto dto)
        {
            Assert.Equal(task.Id, dto.Id);
            Assert.Equal(client.Name, dto.ClientName);
            Assert.Equal(legalEntity.ShortName, dto.LegalEntityName);
            Assert.Equal(clientCase.Title, dto.ClientCaseTitle);
            Assert.Equal(contract.Number, dto.BillingContractNumber);
            Assert.Equal(payment.Account, dto.BillingInvoiceNumber);
            Assert.Equal(payment.Status.ToString(), dto.ContractorPaymentStatus);
        }
    }

    private static void AssertTaskReadQueriesAreMinimal(IEnumerable<string> commands)
    {
        var taskQueries = commands
            .Where(command => command.Contains("TaskJournalItems", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(taskQueries);
        Assert.All(taskQueries, command =>
        {
            Assert.DoesNotContain("JOIN \"Users\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Notes\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\"LogoDataUrl\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\"CourtUrl\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\"TerminationFinancialTerms\"", command, StringComparison.Ordinal);
            Assert.DoesNotContain("\"SystemNotes\"", command, StringComparison.Ordinal);
        });
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
    public async Task GetTasks_KeepsCompletedUnbilledTasksInBillingQueueBeforePagination()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "billing-filter@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент для счёта");
        var expected = await CreateTaskAsync(db, author.Id, client.Id);
        expected.ClientBillingClassification = TaskClientBillingClassification.Billable;
        expected.BillingModeSnapshot = ContractBillingMode.Actual;
        var fixedTask = await CreateTaskAsync(db, author.Id, client.Id);
        fixedTask.ClientBillingClassification = TaskClientBillingClassification.Billable;
        fixedTask.BillingModeSnapshot = ContractBillingMode.Fixed;
        var completedTask = await CreateTaskAsync(db, author.Id, client.Id);
        completedTask.ClientBillingClassification = TaskClientBillingClassification.Billable;
        completedTask.BillingModeSnapshot = ContractBillingMode.Actual;
        completedTask.Status = "done";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetTasks(
            search: null,
            status: "active",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            page: 1,
            pageSize: 50,
            billingStatus: "unbilled",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<PagedResponse<TaskJournalItemDto>>(ok.Value);
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, item => item.Id == expected.Id);
        Assert.Contains(page.Items, item => item.Id == completedTask.Id && item.Status == "done");
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
        Assert.True(candidate.IsReadyForControl);
        Assert.Equal("ready", candidate.AutomationState);
        Assert.Equal(CaseMeaningfulActivitySources.Task, candidate.LastActivitySource);
        Assert.False(string.IsNullOrWhiteSpace(candidate.LastActivityAt));
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_OverviewIncludesCasesWaitingForThreshold()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "waiting-case-control@example.com", "Ответственный Юрист");
        var client = await CreateClientAsync(db, "Клиент без активной задачи");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "СВЕЖЕЕ ДЕЛО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-3);
        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Novosibirsk",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var dueOnlyResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 100,
            ct: CancellationToken.None);
        var dueOnly = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(dueOnlyResult.Result).Value);
        Assert.Empty(dueOnly);

        var overviewResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 100,
            search: "Ответственный Юрист",
            includeWaiting: true,
            ct: CancellationToken.None);
        var overview = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(overviewResult.Result).Value);
        var candidate = Assert.Single(overview);

        Assert.Equal(clientCase.Id, candidate.ClientCaseId);
        Assert.Equal(author.Id, candidate.ResponsibleUserId);
        Assert.Equal("Ответственный Юрист", candidate.ResponsibleName);
        Assert.Equal(author.Id, candidate.SuggestedAssigneeUserId);
        Assert.Equal("Ответственный Юрист", candidate.SuggestedAssigneeName);
        Assert.Equal("responsible", candidate.SuggestedAssigneeSource);
        Assert.Equal("Asia/Novosibirsk", candidate.SuggestedAssigneeTimeZoneId);
        Assert.False(candidate.IsReadyForControl);
        Assert.Equal("waiting", candidate.AutomationState);
        Assert.Equal(CaseMeaningfulActivitySources.CaseCreated, candidate.LastActivitySource);
        Assert.False(string.IsNullOrWhiteSpace(candidate.LastActivityAt));
        Assert.True(candidate.DaysUntilControl >= 10);
        Assert.NotNull(candidate.NextControlAt);
        Assert.Contains("Первый контроль через", candidate.Reason, StringComparison.Ordinal);
        var suggestedDueAtUtc = DateTime.Parse(candidate.SuggestedDueAt).ToUniversalTime();
        var suggestedDueAtLocal = CalendarTimeZoneHelper.ToUserLocal(
            suggestedDueAtUtc,
            CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk"));
        Assert.Equal(9, suggestedDueAtLocal.Hour);
        Assert.Equal(0, suggestedDueAtLocal.Minute);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_SuggestsConfiguredDefaultAssigneeAndLocalDueTime()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "candidate-default-author@example.com", "Автор просмотра");
        var defaultAssignee = await CreateAdminUserAsync(db, "candidate-default@example.com", "Резервный исполнитель");
        var client = await CreateClientAsync(db, "Клиент без ответственного");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-3);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.UserSettings.Add(new UserSetting
        {
            UserId = defaultAssignee.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Novosibirsk",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 100,
            includeWaiting: true,
            ct: CancellationToken.None);

        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Null(candidate.ResponsibleUserId);
        Assert.Equal(defaultAssignee.Id, candidate.SuggestedAssigneeUserId);
        Assert.Equal(defaultAssignee.FullName, candidate.SuggestedAssigneeName);
        Assert.Equal("configuredDefault", candidate.SuggestedAssigneeSource);
        Assert.Equal("Asia/Novosibirsk", candidate.SuggestedAssigneeTimeZoneId);
        var suggestedDueAtLocal = CalendarTimeZoneHelper.ToUserLocal(
            DateTime.Parse(candidate.SuggestedDueAt).ToUniversalTime(),
            CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk"));
        Assert.Equal(9, suggestedDueAtLocal.Hour);
        Assert.Equal(0, suggestedDueAtLocal.Minute);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_SuggestsConfiguredDefaultWhenResponsibleIsInactive()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "candidate-inactive-author@example.com", "Автор просмотра");
        var inactiveResponsible = await CreateAdminUserAsync(db, "candidate-inactive-responsible@example.com", "Неактивный ответственный");
        var defaultAssignee = await CreateAdminUserAsync(db, "candidate-inactive-fallback@example.com", "Резервный исполнитель");
        inactiveResponsible.IsActive = false;
        var client = await CreateClientAsync(db, "Клиент с неактивным ответственным");
        client.ResponsibleUserId = inactiveResponsible.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С БЕЗОПАСНЫМ РЕЗЕРВОМ");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            includeWaiting: true,
            ct: CancellationToken.None);

        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(inactiveResponsible.Id, candidate.ResponsibleUserId);
        Assert.Equal(defaultAssignee.Id, candidate.SuggestedAssigneeUserId);
        Assert.Equal(defaultAssignee.FullName, candidate.SuggestedAssigneeName);
        Assert.Equal("configuredDefault", candidate.SuggestedAssigneeSource);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_DoesNotSuggestDefaultAssigneeWhoIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "candidate-vacation-author@example.com", "Автор просмотра");
        var defaultAssignee = await CreateAdminUserAsync(db, "candidate-vacation-default@example.com", "Резервный в отпуске");
        var client = await CreateClientAsync(db, "Клиент без ответственного и с резервом в отпуске");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО ДЛЯ РУЧНОГО FALLBACK");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await SetVacationAsync(db, defaultAssignee.Id, now.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            ct: CancellationToken.None);

        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(clientCase.Id, candidate.ClientCaseId);
        Assert.Equal(author.Id, candidate.SuggestedAssigneeUserId);
        Assert.Equal(author.FullName, candidate.SuggestedAssigneeName);
        Assert.Equal("currentUser", candidate.SuggestedAssigneeSource);
        Assert.Equal(CalendarTimeZoneHelper.DefaultTimeZoneId, candidate.SuggestedAssigneeTimeZoneId);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_AppliesSearchBeforeLimit()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-search-candidates@example.com", "Автор");
        var olderClient = await CreateClientAsync(db, "Петров Петр Петрович");
        var olderCase = await CreateCaseAsync(db, olderClient.Id, "ПЕТРОВ - старое дело");
        olderCase.CreatedAt = DateTime.UtcNow.AddDays(-45);
        var targetClient = await CreateClientAsync(db, "Кошелева Елена Юрьевна");
        var targetCase = await CreateCaseAsync(db, targetClient.Id, "КОШЕЛЕВА - уголовное дело");
        targetCase.CreatedAt = DateTime.UtcNow.AddDays(-25);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 1,
            escalationDays: null,
            search: "КОШЕЛЕВА",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        var candidate = Assert.Single(candidates);

        Assert.Equal(targetClient.Id, candidate.ClientId);
        Assert.Equal(targetCase.Id, candidate.ClientCaseId);
        Assert.Equal("КОШЕЛЕВА - уголовное дело", candidate.ClientCaseTitle);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_OrdersByLatestMeaningfulActivityAcrossAllSources()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "candidate-order@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент порядка автоконтроля");
        var recentDocumentCase = await CreateCaseAsync(db, client.Id, "СВЕЖИЙ ДОКУМЕНТ");
        var olderActivityCase = await CreateCaseAsync(db, client.Id, "ДАВНО БЕЗ ДВИЖЕНИЯ");
        var now = DateTime.UtcNow;
        recentDocumentCase.CreatedAt = now.AddDays(-100);
        olderActivityCase.CreatedAt = now.AddDays(-100);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: recentDocumentCase.Id,
            status: "done",
            createdAt: now.AddDays(-50),
            updatedAt: now.AddDays(-50));
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: olderActivityCase.Id,
            status: "done",
            createdAt: now.AddDays(-20),
            updatedAt: now.AddDays(-20));
        db.ClientCaseDocuments.AddRange(
            BuildCaseDocument(recentDocumentCase.Id, author, "recent-document", now.AddDays(-15)),
            BuildCaseDocument(olderActivityCase.Id, author, "older-document", now.AddDays(-40)));
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 1,
            ct: CancellationToken.None);

        var candidate = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(olderActivityCase.Id, candidate.ClientCaseId);
        Assert.Equal(CaseMeaningfulActivitySources.Task, candidate.LastActivitySource);
    }

    [Fact]
    public async Task AutomationCaseCandidates_SummaryAndPagingPrioritizeActionableStatesAcrossTheWholeQueue()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "candidate-pages@example.com", "Администратор");
        var client = await CreateClientAsync(db, "Клиент полной очереди");
        var oldestReadyCase = await CreateCaseAsync(db, client.Id, "ГОТОВО ДАВНО");
        var nextReadyCase = await CreateCaseAsync(db, client.Id, "ГОТОВО ПОЗЖЕ");
        var waitingCase = await CreateCaseAsync(db, client.Id, "ОЖИДАЕТ СРОКА");
        var pausedCase = await CreateCaseAsync(db, client.Id, "ПРИОСТАНОВЛЕНО");
        var now = DateTime.UtcNow;
        oldestReadyCase.CreatedAt = now.AddDays(-40);
        nextReadyCase.CreatedAt = now.AddDays(-20);
        waitingCase.CreatedAt = now.AddDays(-3);
        pausedCase.CreatedAt = now.AddDays(-50);
        oldestReadyCase.InactivityRemindersEnabled = true;
        nextReadyCase.InactivityRemindersEnabled = true;
        waitingCase.InactivityRemindersEnabled = true;
        pausedCase.InactivityRemindersEnabled = false;
        pausedCase.InactivityRemindersDisabledReason = "Ручная пауза";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var summaryResult = await controller.GetAutomationCaseCandidatesSummary(
            inactiveDays: 14,
            escalationDays: 30,
            ct: CancellationToken.None);
        var summary = Assert.IsType<TaskJournalCaseAutomationSummaryDto>(
            Assert.IsType<OkObjectResult>(summaryResult.Result).Value);

        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(2, summary.ReadyCount);
        Assert.Equal(1, summary.CriticalCount);
        Assert.Equal(1, summary.WaitingCount);
        Assert.Equal(1, summary.PausedCount);
        Assert.NotNull(summary.NextStateChangeAt);
        Assert.Equal("critical", summary.NextStateChangeKind);
        var nextStateChangeAt = DateTimeOffset.Parse(summary.NextStateChangeAt).UtcDateTime;
        var expectedCriticalAt = nextReadyCase.CreatedAt.AddDays(30);
        Assert.InRange(nextStateChangeAt, expectedCriticalAt.AddSeconds(-1), expectedCriticalAt.AddSeconds(1));

        var searchedSummaryResult = await controller.GetAutomationCaseCandidatesSummary(
            inactiveDays: 14,
            escalationDays: 30,
            search: "ОЖИДАЕТ СРОКА",
            ct: CancellationToken.None);
        var searchedSummary = Assert.IsType<TaskJournalCaseAutomationSummaryDto>(
            Assert.IsType<OkObjectResult>(searchedSummaryResult.Result).Value);
        Assert.Equal(1, searchedSummary.TotalCount);
        Assert.Equal(0, searchedSummary.ReadyCount);
        Assert.Equal(0, searchedSummary.CriticalCount);
        Assert.Equal(1, searchedSummary.WaitingCount);
        Assert.Equal(0, searchedSummary.PausedCount);
        Assert.NotNull(searchedSummary.NextStateChangeAt);
        Assert.Equal("ready", searchedSummary.NextStateChangeKind);
        var searchedNextStateChangeAt = DateTimeOffset.Parse(searchedSummary.NextStateChangeAt).UtcDateTime;
        var expectedWaitingAt = waitingCase.CreatedAt.AddDays(14);
        Assert.InRange(searchedNextStateChangeAt, expectedWaitingAt.AddSeconds(-1), expectedWaitingAt.AddSeconds(1));

        var overviewFirstPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 2,
            automationState: "all",
            ct: CancellationToken.None);
        var overviewFirstPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(overviewFirstPageResult.Result).Value);
        Assert.Equal(
            [oldestReadyCase.Id, nextReadyCase.Id],
            overviewFirstPage.Select(item => item.ClientCaseId));

        var overviewSecondPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 2,
            offset: 2,
            automationState: "all",
            ct: CancellationToken.None);
        var overviewSecondPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(overviewSecondPageResult.Result).Value);
        Assert.Equal(
            [waitingCase.Id, pausedCase.Id],
            overviewSecondPage.Select(item => item.ClientCaseId));

        var readyPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 1,
            offset: 1,
            automationState: "ready",
            ct: CancellationToken.None);
        var readyPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(readyPageResult.Result).Value);
        Assert.Equal(nextReadyCase.Id, Assert.Single(readyPage).ClientCaseId);

        var criticalPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            escalationDays: 30,
            limit: 100,
            automationState: "critical",
            ct: CancellationToken.None);
        var criticalPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(criticalPageResult.Result).Value);
        var criticalCandidate = Assert.Single(criticalPage);
        Assert.Equal(oldestReadyCase.Id, criticalCandidate.ClientCaseId);
        Assert.True(criticalCandidate.IsEscalated);

        var waitingPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 100,
            automationState: "waiting",
            ct: CancellationToken.None);
        var waitingPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(waitingPageResult.Result).Value);
        Assert.Equal(waitingCase.Id, Assert.Single(waitingPage).ClientCaseId);

        var pausedPageResult = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 100,
            automationState: "paused",
            ct: CancellationToken.None);
        var pausedPage = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(
            Assert.IsType<OkObjectResult>(pausedPageResult.Result).Value);
        Assert.Equal(pausedCase.Id, Assert.Single(pausedPage).ClientCaseId);
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
        var pausedCase = await CreateCaseAsync(db, client.Id, "ПРИОСТАНОВЛЕННОЕ ДЕЛО", ClientCaseStatus.OnHold);
        var closedCase = await CreateCaseAsync(db, client.Id, "ЗАКРЫТОЕ ДЕЛО", ClientCaseStatus.Closed);
        var lostCase = await CreateCaseAsync(db, client.Id, "ПРОИГРАННОЕ ДЕЛО", ClientCaseStatus.Lost);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: activeCase.Id,
            dueAt: DateTime.UtcNow.AddDays(30));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(inactiveDays: 14, limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);

        Assert.DoesNotContain(candidates, item => item.ClientCaseId == activeCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == pausedCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == closedCase.Id);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == lostCase.Id);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_UsesActiveStatusInsteadOfDueDateForFutureTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "future-task-statuses@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент с будущими задачами");
        var now = DateTime.UtcNow;
        var statuses = new[]
        {
            "new", "inProgress", "waiting", "onApproval", "approved", "revision", "overdueApproval", "done", "cancelled",
        };
        var casesByStatus = new Dictionary<string, ClientCase>(StringComparer.OrdinalIgnoreCase);

        foreach (var status in statuses)
        {
            var clientCase = await CreateCaseAsync(db, client.Id, $"ДЕЛО {status}");
            clientCase.CreatedAt = now.AddDays(-30);
            casesByStatus[status] = clientCase;
            await CreateTaskAsync(
                db,
                author.Id,
                client.Id,
                clientCaseId: clientCase.Id,
                status: status,
                createdAt: now.AddDays(-20),
                updatedAt: now.AddDays(-20),
                dueAt: now.AddDays(30));
        }

        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        foreach (var activeStatus in statuses.Take(7))
        {
            Assert.DoesNotContain(candidates, item => item.ClientCaseId == casesByStatus[activeStatus].Id);
        }

        Assert.Contains(candidates, item => item.ClientCaseId == casesByStatus["done"].Id);
        Assert.Contains(candidates, item => item.ClientCaseId == casesByStatus["cancelled"].Id);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_SkipsCaseWhileResponsibleIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-preview@example.com", "Ответственный в отпуске");
        var client = await CreateClientAsync(db, "Клиент отпуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО НА ВРЕМЯ ОТПУСКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await SetVacationAsync(db, author.Id, DateTime.UtcNow.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == clientCase.Id);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_OverviewShowsVacationEndAndSuggestsAvailableFallback()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-overview@example.com", "Ответственный в отпуске");
        var fallbackAssignee = await CreateAdminUserAsync(db, "vacation-overview-fallback@example.com", "Резервный исполнитель");
        var client = await CreateClientAsync(db, "Клиент с паузой автоконтроля");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО ДО ВОЗВРАЩЕНИЯ ОТВЕТСТВЕННОГО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        var manuallyPausedCase = await CreateCaseAsync(db, client.Id, "ПАУЗА ПОЛЬЗОВАТЕЛЯ ВО ВРЕМЯ ОТПУСКА");
        manuallyPausedCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        manuallyPausedCase.InactivityRemindersEnabled = false;
        manuallyPausedCase.InactivityRemindersDisabledReason = "Контроль не требуется";
        var vacationUntil = DateTime.UtcNow.AddDays(5).Date;
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = fallbackAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await SetVacationAsync(db, author.Id, vacationUntil);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            automationState: "all",
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        var candidate = Assert.Single(candidates, item => item.ClientCaseId == clientCase.Id);
        Assert.Equal("paused", candidate.AutomationState);
        Assert.Equal($"{vacationUntil:yyyy-MM-dd}", candidate.ResponsibleVacationUntilDate);
        Assert.Equal(fallbackAssignee.Id, candidate.SuggestedAssigneeUserId);
        Assert.Equal(fallbackAssignee.FullName, candidate.SuggestedAssigneeName);
        Assert.Equal("configuredDefault", candidate.SuggestedAssigneeSource);

        var summaryResult = await controller.GetAutomationCaseCandidatesSummary(
            inactiveDays: 14,
            escalationDays: 30,
            search: "ДЕЛО ДО ВОЗВРАЩЕНИЯ",
            ct: CancellationToken.None);
        var summary = Assert.IsType<TaskJournalCaseAutomationSummaryDto>(
            Assert.IsType<OkObjectResult>(summaryResult.Result).Value);
        Assert.Equal(1, summary.PausedCount);
        Assert.Equal("vacationResume", summary.NextStateChangeKind);
        Assert.NotNull(summary.NextStateChangeAt);
        var expectedResumeAt = CalendarTimeZoneHelper.FromUserLocalToUtc(
            vacationUntil.AddDays(1),
            CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk"));
        var actualResumeAt = DateTimeOffset.Parse(summary.NextStateChangeAt).UtcDateTime;
        Assert.InRange(actualResumeAt, expectedResumeAt.AddSeconds(-1), expectedResumeAt.AddSeconds(1));

        var manuallyPausedSummaryResult = await controller.GetAutomationCaseCandidatesSummary(
            inactiveDays: 14,
            escalationDays: 30,
            search: "ПАУЗА ПОЛЬЗОВАТЕЛЯ",
            ct: CancellationToken.None);
        var manuallyPausedSummary = Assert.IsType<TaskJournalCaseAutomationSummaryDto>(
            Assert.IsType<OkObjectResult>(manuallyPausedSummaryResult.Result).Value);
        Assert.Equal(1, manuallyPausedSummary.PausedCount);
        Assert.Null(manuallyPausedSummary.NextStateChangeAt);
        Assert.Null(manuallyPausedSummary.NextStateChangeKind);
    }

    [Fact]
    public async Task GetAutomationCaseCandidates_ResumesCaseAfterResponsibleVacationEnds()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-ended@example.com", "Вернувшийся ответственный");
        var client = await CreateClientAsync(db, "Клиент после отпуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО ПОСЛЕ ОТПУСКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await SetVacationAsync(db, author.Id, DateTime.UtcNow.AddDays(-1));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        Assert.Contains(candidates, item => item.ClientCaseId == clientCase.Id);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task GetAutomationCaseCandidates_WaitsAfterFinishedControlCycle(string finalStatus)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"preview-{finalStatus}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент preview {finalStatus}");
        var recentCase = await CreateCaseAsync(db, client.Id, "НЕДАВНО ЗАВЕРШЁННЫЙ ЦИКЛ");
        var readyCase = await CreateCaseAsync(db, client.Id, "ЦИКЛ ГОТОВ К ПОВТОРУ");
        var now = DateTime.UtcNow;
        recentCase.CreatedAt = now.AddDays(-40);
        readyCase.CreatedAt = now.AddDays(-40);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: recentCase.Id,
            status: finalStatus,
            createdAt: now.AddDays(-13),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-13));
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: readyCase.Id,
            status: finalStatus,
            createdAt: now.AddDays(-14),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-14));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.GetAutomationCaseCandidates(
            inactiveDays: 14,
            limit: 10,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var candidates = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCaseAutomationCandidateDto>>(ok.Value);
        Assert.DoesNotContain(candidates, item => item.ClientCaseId == recentCase.Id);
        var ready = Assert.Single(candidates, item => item.ClientCaseId == readyCase.Id);
        Assert.Contains("Предыдущий цикл завершён", ready.Reason, StringComparison.Ordinal);
        Assert.True(ready.DaysWithoutActiveTask >= 39);
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

        var task = await db.TaskJournalItems.SingleAsync(item => item.Id == dto.Id);
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);

        var duplicateResult = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicateResult.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));

        var comments = await db.TaskJournalComments
            .AsNoTracking()
            .Where(item => item.TaskJournalItemId == dto.Id)
            .ToListAsync();
        Assert.Contains(comments, item => item.Kind == "system" && item.Text.Contains("автоконтроля дела", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_ConcurrentRequestsCreateSingleControlCycle()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "concurrent-manual-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент параллельной ручной постановки");
        var clientCase = await CreateCaseAsync(db, client.Id, "ПАРАЛЛЕЛЬНАЯ РУЧНАЯ ПОСТАНОВКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var mutationGate = new CaseInactivityAutomationMutationGate();
        var firstController = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            caseInactivityAutomationMutationGate: mutationGate);
        var secondController = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            caseInactivityAutomationMutationGate: mutationGate);

        var results = await Task.WhenAll(
            firstController.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None),
            secondController.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None));

        Assert.Single(results, result => result.Result is CreatedResult);
        Assert.Single(results, result => result.Result is ConflictObjectResult);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
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
    public async Task CreateAutomationTaskForCase_UsesConfiguredDefaultWhenCaseHasNoResponsible()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "manual-default-author@example.com", "Автор запуска");
        var defaultAssignee = await CreateAdminUserAsync(db, "manual-default@example.com", "Резервный исполнитель");
        var client = await CreateClientAsync(db, "Клиент без ответственного");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var dto = Assert.IsType<TaskJournalItemDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        Assert.Equal(defaultAssignee.Id, dto.AssigneeUserId);
        Assert.Equal(defaultAssignee.FullName, dto.AssigneeName);
        Assert.Equal(author.Id, dto.AuthorUserId);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == defaultAssignee.Id);
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_UsesCurrentAuthorWhenDefaultAssigneeIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "manual-vacation-author@example.com", "Автор запуска");
        var defaultAssignee = await CreateAdminUserAsync(db, "manual-vacation-default@example.com", "Резервный в отпуске");
        var client = await CreateClientAsync(db, "Клиент ручного fallback");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ДОСТУПНОГО РЕЗЕРВА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await SetVacationAsync(db, defaultAssignee.Id, now.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var dto = Assert.IsType<TaskJournalItemDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        Assert.Equal(author.Id, dto.AssigneeUserId);
        Assert.Equal(author.FullName, dto.AssigneeName);
        Assert.DoesNotContain(dto.Participants, item => item.UserId == defaultAssignee.Id);
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

    [Theory]
    [InlineData(ClientCaseStatus.OnHold)]
    [InlineData(ClientCaseStatus.Closed)]
    [InlineData(ClientCaseStatus.Lost)]
    public async Task CreateAutomationTaskForCase_SkipsExcludedCaseStatuses(ClientCaseStatus status)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"excluded-manual-{status}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент {status}");
        var clientCase = await CreateCaseAsync(db, client.Id, $"ДЕЛО {status}", status);
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_SkipsCaseWhileResponsibleIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-manual@example.com", "Ответственный в отпуске");
        var client = await CreateClientAsync(db, "Клиент отпуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО НА ВРЕМЯ ОТПУСКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await SetVacationAsync(db, author.Id, DateTime.UtcNow.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("находится в отпуске", conflict.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task CreateAutomationTaskForCase_DoesNotCreateDuplicateWhenActiveTaskExists()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var clientCase = await CreateCaseAsync(db, client.Id, "БАНКРОТСТВО");
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: clientCase.Id,
            dueAt: DateTime.UtcNow.AddDays(30));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task CreateAutomationTaskForCase_WaitsAfterFinishedControlCycle(string finalStatus)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"manual-{finalStatus}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент manual {finalStatus}");
        var clientCase = await CreateCaseAsync(db, client.Id, "НЕДАВНО ЗАВЕРШЁННЫЙ КОНТРОЛЬ");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-40);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: clientCase.Id,
            status: finalStatus,
            createdAt: now.AddDays(-13),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-13));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("Предыдущий цикл автоконтроля недавно завершён", conflict.Value?.ToString(), StringComparison.Ordinal);
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
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: activeCase.Id,
            dueAt: DateTime.UtcNow.AddDays(30));
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

        var secondResult = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);
        var secondOk = Assert.IsType<OkObjectResult>(secondResult.Result);
        var secondDto = Assert.IsType<TaskJournalAutomationCreateResultDto>(secondOk.Value);
        Assert.Empty(secondDto.CreatedTasks);
        Assert.Equal(3, await db.TaskJournalItems.CountAsync());

        var automaticTasks = await db.TaskJournalItems
            .Where(item => item.ClientCaseId == firstCase.Id || item.ClientCaseId == secondCase.Id)
            .ToListAsync();
        Assert.All(automaticTasks, task =>
        {
            Assert.Equal(1, task.InactivityControlCycleNumber);
            Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);
        });
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_UsesConfiguredDefaultForCasesWithoutResponsible()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-default-author@example.com", "Автор запуска");
        var defaultAssignee = await CreateAdminUserAsync(db, "bulk-default@example.com", "Резервный исполнитель");
        var client = await CreateClientAsync(db, "Клиент массового контроля");
        var firstCase = await CreateCaseAsync(db, client.Id, "ПЕРВОЕ ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        var secondCase = await CreateCaseAsync(db, client.Id, "ВТОРОЕ ДЕЛО БЕЗ ОТВЕТСТВЕННОГО");
        firstCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        secondCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, dto.CreatedTasks.Count);
        Assert.All(dto.CreatedTasks, task =>
        {
            Assert.Equal(defaultAssignee.Id, task.AssigneeUserId);
            Assert.Equal(defaultAssignee.FullName, task.AssigneeName);
            Assert.Equal(author.Id, task.AuthorUserId);
        });
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_UsesCurrentAuthorWhenDefaultAssigneeIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-vacation-author@example.com", "Автор массового запуска");
        var defaultAssignee = await CreateAdminUserAsync(db, "bulk-vacation-default@example.com", "Резервный в отпуске");
        var client = await CreateClientAsync(db, "Клиент массового fallback");
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО БЕЗ ДОСТУПНОГО РЕЗЕРВА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        var now = DateTime.UtcNow;
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.automation.caseControl.defaultAssigneeUserId",
            Value = defaultAssignee.Id.ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await SetVacationAsync(db, defaultAssignee.Id, now.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var task = Assert.Single(dto.CreatedTasks);
        Assert.Equal(clientCase.Id, task.ClientCaseId);
        Assert.Equal(author.Id, task.AssigneeUserId);
        Assert.Equal(author.FullName, task.AssigneeName);
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_AppliesSearchBeforeLimit()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-control-search@example.com", "Автор");
        var unrelatedClient = await CreateClientAsync(db, "Старый клиент");
        var unrelatedCase = await CreateCaseAsync(db, unrelatedClient.Id, "ДАВНО БЕЗ ДВИЖЕНИЯ");
        unrelatedCase.CreatedAt = DateTime.UtcNow.AddDays(-60);
        var targetClient = await CreateClientAsync(db, "Кошелева Елена Юрьевна");
        var targetCase = await CreateCaseAsync(db, targetClient.Id, "КОШЕЛЕВА - УГОЛОВНОЕ ДЕЛО");
        targetCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(
            limit: 1,
            search: "КОШЕЛЕВА",
            ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var created = Assert.Single(dto.CreatedTasks);
        Assert.Equal(targetCase.Id, created.ClientCaseId);
        Assert.DoesNotContain(dto.CreatedTasks, item => item.ClientCaseId == unrelatedCase.Id);
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_LoadsAssigneeTimeZonesInOneBatch()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "bulk-control-batch@example.com", "Ответственный пакетного контроля");
        var client = await CreateClientAsync(db, "Клиент пакетного контроля");
        client.ResponsibleUserId = author.Id;
        for (var index = 1; index <= 8; index++)
        {
            var clientCase = await CreateCaseAsync(db, client.Id, $"ПАКЕТНОЕ ДЕЛО {index}");
            clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        }

        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Asia/Novosibirsk",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(8, dto.CreatedTasks.Count);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("SELECT \"u\".\"UserId\", \"u\".\"Value\"", StringComparison.Ordinal)
            && command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("SELECT \"u\".\"Id\", \"u\".\"FullName\", \"u\".\"Email\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("SELECT DISTINCT", StringComparison.OrdinalIgnoreCase)
            && command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)
            && command.Contains("\"ClientCaseId\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_PrioritizesOldestMeaningfulActivityAcrossAllSources()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-control-order@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент порядка массового контроля");
        var recentDocumentCase = await CreateCaseAsync(db, client.Id, "СВЕЖИЙ ДОКУМЕНТ");
        var olderActivityCase = await CreateCaseAsync(db, client.Id, "ДАВНО БЕЗ ДВИЖЕНИЯ");
        var now = DateTime.UtcNow;
        recentDocumentCase.CreatedAt = now.AddDays(-100);
        olderActivityCase.CreatedAt = now.AddDays(-100);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: recentDocumentCase.Id,
            status: "done",
            createdAt: now.AddDays(-50),
            updatedAt: now.AddDays(-50));
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: olderActivityCase.Id,
            status: "done",
            createdAt: now.AddDays(-20),
            updatedAt: now.AddDays(-20));
        db.ClientCaseDocuments.AddRange(
            BuildCaseDocument(recentDocumentCase.Id, author, "recent-bulk-document", now.AddDays(-15)),
            BuildCaseDocument(olderActivityCase.Id, author, "older-bulk-document", now.AddDays(-40)));
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 1, ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var created = Assert.Single(dto.CreatedTasks);
        Assert.Equal(olderActivityCase.Id, created.ClientCaseId);
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_PrioritizesCriticalCaseBeforeRegularReadyCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-critical-order@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент критического порядка");
        var criticalCase = await CreateCaseAsync(db, client.Id, "КРИТИЧЕСКОЕ ДЕЛО");
        var regularReadyCase = await CreateCaseAsync(db, client.Id, "ОБЫЧНЫЙ НАСТУПИВШИЙ КОНТРОЛЬ");
        var now = DateTime.UtcNow;
        criticalCase.CreatedAt = now.AddDays(-100);
        regularReadyCase.CreatedAt = now.AddDays(-20);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: criticalCase.Id,
            status: "done",
            createdAt: now.AddDays(-15),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-15));
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 1, ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var created = Assert.Single(dto.CreatedTasks);
        Assert.Equal(criticalCase.Id, created.ClientCaseId);
        Assert.Equal("urgent", created.Priority);
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_CriticalScopeDoesNotCreateRegularReadyTasks()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "bulk-critical-scope@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент критической области");
        var criticalCase = await CreateCaseAsync(db, client.Id, "КРИТИЧЕСКОЕ ДЕЛО");
        var regularReadyCase = await CreateCaseAsync(db, client.Id, "ОБЫЧНЫЙ НАСТУПИВШИЙ КОНТРОЛЬ");
        var now = DateTime.UtcNow;
        criticalCase.CreatedAt = now.AddDays(-40);
        regularReadyCase.CreatedAt = now.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(
            limit: 10,
            automationState: "critical",
            ct: CancellationToken.None);

        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var created = Assert.Single(dto.CreatedTasks);
        Assert.Equal(criticalCase.Id, created.ClientCaseId);
        Assert.DoesNotContain(dto.CreatedTasks, item => item.ClientCaseId == regularReadyCase.Id);
    }

    [Theory]
    [InlineData(ClientCaseStatus.OnHold)]
    [InlineData(ClientCaseStatus.Closed)]
    [InlineData(ClientCaseStatus.Lost)]
    public async Task CreateAutomationTasksForCandidates_SkipsExcludedCaseStatuses(ClientCaseStatus status)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"excluded-bulk-{status}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент {status}");
        var clientCase = await CreateCaseAsync(db, client.Id, $"ДЕЛО {status}", status);
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(ok.Value);
        Assert.Empty(dto.CreatedTasks);
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_SkipsCaseWhileResponsibleIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-bulk@example.com", "Ответственный в отпуске");
        var client = await CreateClientAsync(db, "Клиент отпуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО НА ВРЕМЯ ОТПУСКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await SetVacationAsync(db, author.Id, DateTime.UtcNow.AddDays(5));
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(ok.Value);
        Assert.Empty(dto.CreatedTasks);
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task CreateAutomationTasksForCandidates_AppliesCaseOwnerVisibility()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "bulk-automation-author@example.com", "Автор", canEdit: false, canCreate: true);
        var otherResponsible = await CreateAdminUserAsync(db, "bulk-automation-other@example.com", "Другой ответственный");
        var visibleClient = await CreateClientAsync(db, "Видимый клиент автоконтроля");
        visibleClient.ResponsibleUserId = author.Id;
        var hiddenClient = await CreateClientAsync(db, "Скрытый клиент автоконтроля");
        hiddenClient.ResponsibleUserId = otherResponsible.Id;
        var visibleCase = await CreateCaseAsync(db, visibleClient.Id, "ВИДИМОЕ ДЕЛО");
        var hiddenCase = await CreateCaseAsync(db, hiddenClient.Id, "СКРЫТОЕ ДЕЛО");
        visibleCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        hiddenCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.CreateAutomationTasksForCandidates(limit: 10, ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalAutomationCreateResultDto>(ok.Value);
        var createdTask = Assert.Single(dto.CreatedTasks);
        Assert.Equal(visibleCase.Id, createdTask.ClientCaseId);
        Assert.DoesNotContain(dto.CreatedTasks, item => item.ClientCaseId == hiddenCase.Id);
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == visibleCase.Id));
        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == hiddenCase.Id));
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
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);
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
    public async Task TaskJournalAutomationScheduler_ConcurrentRunsCreateSingleControlCycle()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "concurrent-cycle-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент параллельного автоконтроля");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ПАРАЛЛЕЛЬНЫЙ КОНТРОЛЬ");
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
            var results = await Task.WhenAll(
                scheduler.RunOnceAsync(CancellationToken.None),
                scheduler.RunOnceAsync(CancellationToken.None));

            Assert.All(results, result => Assert.Null(result.Error));
            Assert.Equal(1, results.Sum(result => result.CreatedCount));
        }

        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);
    }

    [Fact]
    public async Task SharedMutationGate_PreventsDuplicateBetweenManualAndScheduledRuns()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "manual-scheduler-race-author@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент ручного и фонового запуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "РУЧНОЙ И ФОНОВЫЙ ЗАПУСК");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
        await db.SaveChangesAsync();
        var mutationGate = new CaseInactivityAutomationMutationGate();
        var controller = CreateController(
            db,
            new RecordingNotificationService(),
            author,
            caseInactivityAutomationMutationGate: mutationGate);
        var (scheduler, provider) = CreateAutomationScheduler(
            db,
            new Dictionary<string, string?>
            {
                ["TaskJournal:Automation:Enabled"] = "true",
                ["TaskJournal:Automation:InactiveDays"] = "14",
                ["TaskJournal:Automation:Limit"] = "10",
            },
            mutationGate: mutationGate);

        using (provider)
        {
            var manualTask = controller.CreateAutomationTaskForCase(clientCase.Id, CancellationToken.None);
            var schedulerTask = scheduler.RunOnceAsync(CancellationToken.None);
            await Task.WhenAll(manualTask, schedulerTask);
            var manualResult = await manualTask;
            var schedulerResult = await schedulerTask;

            var manualCreated = manualResult.Result is CreatedResult ? 1 : 0;
            Assert.True(manualResult.Result is CreatedResult or ConflictObjectResult);
            Assert.Equal(1, manualCreated + schedulerResult.CreatedCount);
        }

        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_DoesNotDuplicateControlAfterGlobalDefaultsChange()
    {
        await using var db = await CreateContextAsync();
        var initialAssignee = await CreateAdminUserAsync(db, "defaults-initial@example.com", "Первый ответственный");
        var newDefaultAssignee = await CreateAdminUserAsync(db, "defaults-new@example.com", "Новый резервный ответственный");
        var client = await CreateClientAsync(db, "Клиент изменения правил");
        client.ResponsibleUserId = initialAssignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ИЗМЕНЕНИЕ ПРАВИЛ АВТОКОНТРОЛЯ");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-40);
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var firstRun = await scheduler.RunOnceAsync(CancellationToken.None);
            Assert.Null(firstRun.Error);
            Assert.Equal(1, firstRun.CreatedCount);

            var now = DateTime.UtcNow;
            db.SystemSettings.AddRange(
                new SystemSetting
                {
                    Key = "tasks.automation.caseControl.inactiveDays",
                    Value = "2",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new SystemSetting
                {
                    Key = "tasks.automation.caseControl.escalationDays",
                    Value = "4",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new SystemSetting
                {
                    Key = "tasks.automation.caseControl.defaultAssigneeUserId",
                    Value = newDefaultAssignee.Id.ToString(),
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new SystemSetting
                {
                    Key = "tasks.automation.caseControl.limit",
                    Value = "100",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();

            var secondRun = await scheduler.RunOnceAsync(CancellationToken.None);
            Assert.Null(secondRun.Error);
            Assert.Equal(0, secondRun.CreatedCount);
        }

        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == clientCase.Id);
        Assert.Equal(initialAssignee.Id, task.AssigneeUserId);
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_RechecksCandidateCasesInSingleQuery()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var author = await CreateAdminUserAsync(db, "automation-batch@example.com", "Ответственный автоконтроля");
        var client = await CreateClientAsync(db, "Клиент пакетного автоконтроля");
        client.ResponsibleUserId = author.Id;
        var cases = new List<ClientCase>();
        for (var index = 1; index <= 12; index++)
        {
            var clientCase = await CreateCaseAsync(db, client.Id, $"НЕАКТИВНОЕ ДЕЛО {index}");
            clientCase.CreatedAt = DateTime.UtcNow.AddDays(-20);
            cases.Add(clientCase);
        }

        db.UserSettings.Add(new UserSetting
        {
            UserId = author.Id,
            Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
            Value = "Europe/Samara",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "20",
            ["TaskJournal:Automation:DefaultAssigneeUserId"] = "0",
            ["TaskJournal:Automation:EscalationUserId"] = "0",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(cases.Count, result.CreatedCount);
        }

        var candidateRecheckCommands = interceptor.Commands
            .Where(command =>
                command.Contains("SELECT DISTINCT", StringComparison.OrdinalIgnoreCase)
                && command.Contains("FROM \"TaskJournalItems\"", StringComparison.Ordinal)
                && command.Contains("\"ClientCaseId\"", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(candidateRecheckCommands);
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"Users\"", StringComparison.Ordinal));
        Assert.Single(interceptor.Commands, command =>
            command.Contains("FROM \"UserSettings\"", StringComparison.Ordinal));
        var caseIds = cases.Select(clientCase => clientCase.Id).ToArray();
        Assert.Equal(cases.Count, await db.TaskJournalItems.CountAsync(task =>
            task.ClientCaseId.HasValue && caseIds.Contains(task.ClientCaseId.Value)));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_RespectsDailyCreationLimit()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "daily-limit@example.com", "Ответственный автоконтроля");
        var client = await CreateClientAsync(db, "Клиент дневного лимита");
        client.ResponsibleUserId = assignee.Id;
        for (var index = 1; index <= 3; index++)
        {
            var clientCase = await CreateCaseAsync(db, client.Id, $"ДЕЛО ДНЕВНОГО ЛИМИТА {index}");
            clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        }

        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "1",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        Assert.Equal(1, await db.TaskJournalItems.CountAsync());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_PrioritizesOldestMeaningfulActivityAcrossAllSources()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "scheduler-control-order@example.com", "Ответственный автоконтроля");
        var client = await CreateClientAsync(db, "Клиент порядка фонового контроля");
        client.ResponsibleUserId = assignee.Id;
        var recentDocumentCase = await CreateCaseAsync(db, client.Id, "СВЕЖИЙ ДОКУМЕНТ");
        var olderActivityCase = await CreateCaseAsync(db, client.Id, "ДАВНО БЕЗ ДВИЖЕНИЯ");
        var now = DateTime.UtcNow;
        recentDocumentCase.CreatedAt = now.AddDays(-100);
        olderActivityCase.CreatedAt = now.AddDays(-100);
        await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            clientCaseId: recentDocumentCase.Id,
            status: "done",
            createdAt: now.AddDays(-50),
            updatedAt: now.AddDays(-50));
        await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            clientCaseId: olderActivityCase.Id,
            status: "done",
            createdAt: now.AddDays(-20),
            updatedAt: now.AddDays(-20));
        db.ClientCaseDocuments.AddRange(
            BuildCaseDocument(recentDocumentCase.Id, assignee, "recent-scheduler-document", now.AddDays(-15)),
            BuildCaseDocument(olderActivityCase.Id, assignee, "older-scheduler-document", now.AddDays(-40)));
        await db.SaveChangesAsync();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "1",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        var automationTask = await db.TaskJournalItems
            .SingleAsync(task => task.InactivityControlCycleNumber == 1);
        Assert.Equal(olderActivityCase.Id, automationTask.ClientCaseId);
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
        var realtimePublisher = new RecordingRealtimeNotificationPublisher();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:EscalationUserId"] = escalationUser.Id.ToString(),
            ["TaskJournal:Automation:Limit"] = "10",
        }, realtimePublisher: realtimePublisher);

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.True(result.Enabled);
            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
            Assert.Equal(1, realtimePublisher.TaskJournalChangeCount);
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
        Assert.Equal(2, updatedTask.InactivityControlCycleNumber);
        Assert.Equal(updatedTask.CreatedAt.AddDays(4), updatedTask.InactivityControlNextCheckAt);
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
    public async Task TaskJournalAutomationScheduler_RebasesLongMissedEscalationWithoutNotification()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "downtime-recovery@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент после простоя");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ВОССТАНОВЛЕНИЕ ПОСЛЕ ПРОСТОЯ");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-40);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            assigneeUserId: assignee.Id,
            clientCaseId: clientCase.Id,
            status: "new",
            createdAt: now.AddDays(-10));
        task.Type = "control";
        task.Title = "Проверить движение по делу";
        task.Priority = "normal";
        task.TagsJson = "[\"автоконтроль\"]";
        task.InactivityControlCycleNumber = 1;
        task.InactivityControlNextCheckAt = now.AddDays(-8);
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:Limit"] = "10",
        }, notifications);
        var beforeRun = DateTime.UtcNow;

        using (provider)
        {
            var firstRun = await scheduler.RunOnceAsync(CancellationToken.None);
            var secondRun = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(firstRun.Error);
            Assert.Null(secondRun.Error);
            Assert.Equal(0, firstRun.EscalatedCount);
            Assert.Equal(0, secondRun.EscalatedCount);
        }

        var afterRun = DateTime.UtcNow;
        var updatedTask = await db.TaskJournalItems
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal(1, updatedTask.InactivityControlCycleNumber);
        Assert.InRange(
            updatedTask.InactivityControlNextCheckAt!.Value,
            beforeRun.AddDays(2),
            afterRun.AddDays(2));
        Assert.DoesNotContain(updatedTask.Comments, item =>
            item.MetadataJson.Contains("caseInactivityControlEscalation"));
        Assert.Empty(notifications.ChangedTasks);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_PausesRepeatEscalationWhileResponsibleIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "vacation-repeat@example.com", "Ответственный в отпуске");
        var client = await CreateClientAsync(db, "Клиент отпуска");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "КОНТРОЛЬ НА ВРЕМЯ ОТПУСКА");
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
        task.TagsJson = "[\"автоконтроль\"]";
        task.InactivityControlCycleNumber = 1;
        task.InactivityControlNextCheckAt = now.AddDays(-1);
        await SetVacationAsync(db, assignee.Id, now.AddDays(5));
        var nextCheckAt = task.InactivityControlNextCheckAt;
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);
            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.EscalatedCount);
        }

        await db.Entry(task).ReloadAsync();
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(nextCheckAt, task.InactivityControlNextCheckAt);
        Assert.Empty(await db.TaskJournalComments
            .Where(item => item.TaskJournalItemId == task.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_InitializesLegacyControlCycleOnce()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "legacy-cycle-assignee@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент старого автоконтроля");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "СТАРЫЙ ЦИКЛ");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-20);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            assigneeUserId: assignee.Id,
            clientCaseId: clientCase.Id,
            status: "new",
            createdAt: now.AddDays(-1));
        task.Type = "control";
        task.Title = "Проверить движение по делу";
        task.TagsJson = "[\"автоконтроль\"]";
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

            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.EscalatedCount);
        }

        await db.Entry(task).ReloadAsync();
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(2), task.InactivityControlNextCheckAt);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_RestoresAllLegacyCycleLevelsIdempotently()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "legacy-levels-assignee@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент уровней автоконтроля");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "УРОВНИ СТАРОГО ЦИКЛА");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-20);

        var tasks = new List<TaskJournalItem>();
        for (var index = 0; index < 3; index++)
        {
            var task = await CreateTaskAsync(
                db,
                assignee.Id,
                client.Id,
                assigneeUserId: assignee.Id,
                clientCaseId: clientCase.Id,
                status: "new",
                createdAt: now);
            task.Type = "control";
            task.Title = $"Старый автоконтроль {index + 1}";
            task.TagsJson = "[\"автоконтроль\"]";
            tasks.Add(task);
        }

        db.TaskJournalComments.AddRange(
            new TaskJournalComment
            {
                TaskJournalItemId = tasks[1].Id,
                AuthorName = "Автоконтроль",
                Kind = "system",
                Text = "Первый повтор.",
                MetadataJson = "{\"eventType\":\"caseInactivityControlEscalation\",\"level\":1,\"offsetDays\":2}",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new TaskJournalComment
            {
                TaskJournalItemId = tasks[2].Id,
                AuthorName = "Автоконтроль",
                Kind = "system",
                Text = "Первый повтор.",
                MetadataJson = "{\"eventType\":\"caseInactivityControlEscalation\",\"level\":1,\"offsetDays\":2}",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new TaskJournalComment
            {
                TaskJournalItemId = tasks[2].Id,
                AuthorName = "Автоконтроль",
                Kind = "system",
                Text = "Критический повтор.",
                MetadataJson = "{\"eventType\":\"caseInactivityControlEscalation\",\"level\":2,\"offsetDays\":4}",
                CreatedAt = now,
                UpdatedAt = now,
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
            var firstRun = await scheduler.RunOnceAsync(CancellationToken.None);
            var secondRun = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(firstRun.Error);
            Assert.Null(secondRun.Error);
            Assert.Equal(0, firstRun.CreatedCount);
            Assert.Equal(0, secondRun.CreatedCount);
            Assert.Equal(0, firstRun.EscalatedCount);
            Assert.Equal(0, secondRun.EscalatedCount);
        }

        foreach (var task in tasks)
        {
            await db.Entry(task).ReloadAsync();
        }

        Assert.Equal(1, tasks[0].InactivityControlCycleNumber);
        Assert.Equal(tasks[0].CreatedAt.AddDays(2), tasks[0].InactivityControlNextCheckAt);
        Assert.Equal(2, tasks[1].InactivityControlCycleNumber);
        Assert.Equal(tasks[1].CreatedAt.AddDays(4), tasks[1].InactivityControlNextCheckAt);
        Assert.Equal(3, tasks[2].InactivityControlCycleNumber);
        Assert.Null(tasks[2].InactivityControlNextCheckAt);
        Assert.Equal(3, await db.TaskJournalComments.CountAsync());
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_RestoresLegacyCycleOnFullyMigratedDatabase()
    {
        await using var db = await CreateMigratedContextAsync();
        var assignee = await CreateAdminUserAsync(db, "migrated-cycle@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент миграционной проверки");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "МИГРИРОВАННЫЙ СТАРЫЙ ЦИКЛ");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-30);
        var task = await CreateTaskAsync(
            db,
            assignee.Id,
            client.Id,
            assigneeUserId: assignee.Id,
            clientCaseId: clientCase.Id,
            status: "new",
            createdAt: now);
        task.Type = "control";
        task.Title = "Старый автоконтроль после миграции";
        task.TagsJson = "[\"автоконтроль\"]";
        task.InactivityControlCycleNumber = null;
        task.InactivityControlNextCheckAt = null;
        db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = task.Id,
            AuthorName = "Автоконтроль",
            Kind = "system",
            Text = "Первый повтор до обновления.",
            MetadataJson = "{\"eventType\":\"caseInactivityControlEscalation\",\"level\":1,\"offsetDays\":2}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        var appliedMigrations = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(appliedMigrations, migration =>
            migration.EndsWith("AddTaskInactivityControlCycleState", StringComparison.Ordinal));
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

            Assert.Null(firstRun.Error);
            Assert.Null(secondRun.Error);
            Assert.Equal(0, firstRun.CreatedCount);
            Assert.Equal(0, secondRun.CreatedCount);
        }

        await db.Entry(task).ReloadAsync();
        Assert.Equal(2, task.InactivityControlCycleNumber);
        Assert.Equal(task.CreatedAt.AddDays(4), task.InactivityControlNextCheckAt);
        Assert.Equal(1, await db.TaskJournalComments.CountAsync(item =>
            item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_StopsStoredCycleAfterMeaningfulActivity()
    {
        await using var db = await CreateContextAsync();
        var assignee = await CreateAdminUserAsync(db, "reset-cycle-assignee@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент со сброшенным автоконтролем");
        client.ResponsibleUserId = assignee.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "СБРОС ЦИКЛА");
        var now = DateTime.UtcNow;
        clientCase.CreatedAt = now.AddDays(-20);
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
        task.TagsJson = "[\"автоконтроль\"]";
        task.InactivityControlCycleNumber = 1;
        task.InactivityControlNextCheckAt = now.AddDays(-1);
        db.ClientCaseDocuments.Add(new ClientCaseDocument
        {
            ClientCaseId = clientCase.Id,
            DocumentKey = "reset-cycle-activity",
            DocumentType = "other",
            Status = "draft",
            OriginalFileName = "новое движение.pdf",
            StoredFileName = "new-activity.pdf",
            StoragePath = "tests/new-activity.pdf",
            ContentType = "application/pdf",
            SizeBytes = 12,
            UploadedByUserId = assignee.Id,
            UploadedByName = assignee.FullName,
            UploadedAt = now.AddHours(-1),
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

            Assert.Null(result.Error);
            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(0, result.EscalatedCount);
        }

        await db.Entry(task).ReloadAsync();
        Assert.Equal(1, task.InactivityControlCycleNumber);
        Assert.Null(task.InactivityControlNextCheckAt);
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_LoadsRepeatEscalationSignalsPerBatch()
    {
        var interceptor = new RecordingCommandInterceptor();
        await using var db = await CreateContextAsync(interceptor);
        var assignee = await CreateAdminUserAsync(db, "batch-repeat-assignee@example.com", "Ответственный дела");
        var client = await CreateClientAsync(db, "Клиент пакетной эскалации");
        client.ResponsibleUserId = assignee.Id;
        var now = DateTime.UtcNow;
        const int caseCount = 8;
        for (var index = 1; index <= caseCount; index++)
        {
            var clientCase = await CreateCaseAsync(db, client.Id, $"ПАКЕТНАЯ ЭСКАЛАЦИЯ {index}");
            clientCase.CreatedAt = now.AddDays(-20);
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
            task.TagsJson = "[\"автоконтроль\"]";
        }

        await db.SaveChangesAsync();
        interceptor.Commands.Clear();
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:EscalationDays"] = "30",
            ["TaskJournal:Automation:Limit"] = "20",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(caseCount, result.EscalatedCount);
        }

        var aggregateSignalCommands = interceptor.Commands
            .Where(command =>
                command.Contains("TaskJournalComments", StringComparison.Ordinal) &&
                command.Contains("ClientCaseDocuments", StringComparison.Ordinal) &&
                command.Contains("ClientCaseCourtEvents", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, aggregateSignalCommands.Length);
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
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: approvedTaskCase.Id,
            status: "approved",
            createdAt: DateTime.UtcNow.AddDays(-5),
            dueAt: DateTime.UtcNow.AddDays(30));
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

    [Theory]
    [InlineData(ClientCaseStatus.OnHold)]
    [InlineData(ClientCaseStatus.Closed)]
    [InlineData(ClientCaseStatus.Lost)]
    public async Task TaskJournalAutomationScheduler_SkipsExcludedCaseStatuses(ClientCaseStatus status)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"excluded-scheduler-{status}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент {status}");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, $"ДЕЛО {status}", status);
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
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

        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsCaseWhileResponsibleIsOnVacation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "vacation-scheduler@example.com", "Ответственный в отпуске");
        var client = await CreateClientAsync(db, "Клиент отпуска");
        client.ResponsibleUserId = author.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО НА ВРЕМЯ ОТПУСКА");
        clientCase.CreatedAt = DateTime.UtcNow.AddDays(-30);
        await SetVacationAsync(db, author.Id, DateTime.UtcNow.AddDays(5));
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

        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == clientCase.Id));
    }

    [Fact]
    public async Task TaskJournalAutomationScheduler_SkipsUnavailableFallbackBeforeApplyingDailyLimit()
    {
        await using var db = await CreateContextAsync();
        var defaultAssignee = await CreateAdminUserAsync(db, "scheduler-vacation-default@example.com", "Резервный в отпуске");
        var availableResponsible = await CreateAdminUserAsync(db, "scheduler-available@example.com", "Доступный ответственный");
        var unassignedClient = await CreateClientAsync(db, "Клиент без ответственного");
        var assignedClient = await CreateClientAsync(db, "Клиент с ответственным");
        assignedClient.ResponsibleUserId = availableResponsible.Id;
        var unassignedCase = await CreateCaseAsync(db, unassignedClient.Id, "САМОЕ СТАРОЕ ДЕЛО БЕЗ ИСПОЛНИТЕЛЯ");
        var assignedCase = await CreateCaseAsync(db, assignedClient.Id, "ДЕЛО С ДОСТУПНЫМ ИСПОЛНИТЕЛЕМ");
        var now = DateTime.UtcNow;
        unassignedCase.CreatedAt = now.AddDays(-40);
        assignedCase.CreatedAt = now.AddDays(-30);
        await SetVacationAsync(db, defaultAssignee.Id, now.AddDays(5));
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "1",
            ["TaskJournal:Automation:DefaultAssigneeUserId"] = defaultAssignee.Id.ToString(),
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CreatedCount);
            Assert.Null(result.Error);
        }

        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.ClientCaseId == unassignedCase.Id));
        var task = await db.TaskJournalItems.SingleAsync(item => item.ClientCaseId == assignedCase.Id);
        Assert.Equal(availableResponsible.Id, task.AssigneeUserId);
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
    public async Task TaskJournalAutomationScheduler_UsesDefaultAssigneeWhenResponsibleIsUnavailable()
    {
        await using var db = await CreateContextAsync();
        var defaultAssignee = await CreateAdminUserAsync(db, "scheduler-default@example.com", "Резервный исполнитель");
        var unavailableResponsible = await CreateAdminUserAsync(db, "scheduler-inactive@example.com", "Неактивный ответственный");
        unavailableResponsible.IsActive = false;
        var client = await CreateClientAsync(db, "Клиент с неактивным ответственным");
        client.ResponsibleUserId = unavailableResponsible.Id;
        var clientCase = await CreateCaseAsync(db, client.Id, "ДЕЛО С РЕЗЕРВНЫМ ИСПОЛНИТЕЛЕМ");
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
        Assert.Equal(defaultAssignee.Id, task.AssigneeUserId);
        Assert.Equal(defaultAssignee.FullName, task.AssigneeName);
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

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task TaskJournalAutomationScheduler_StartsNewCycleOnlyAfterFinishedControlCooldown(string finalStatus)
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, $"scheduler-{finalStatus}@example.com", "Автор");
        var client = await CreateClientAsync(db, $"Клиент scheduler {finalStatus}");
        client.ResponsibleUserId = author.Id;
        var recentCase = await CreateCaseAsync(db, client.Id, "НЕДАВНО ЗАВЕРШЁННЫЙ ЦИКЛ");
        var readyCase = await CreateCaseAsync(db, client.Id, "ПОВТОРНЫЙ ЦИКЛ");
        var now = DateTime.UtcNow;
        recentCase.CreatedAt = now.AddDays(-40);
        readyCase.CreatedAt = now.AddDays(-40);
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: recentCase.Id,
            status: finalStatus,
            createdAt: now.AddDays(-13),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-13));
        await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            clientCaseId: readyCase.Id,
            status: finalStatus,
            createdAt: now.AddDays(-14),
            tagsJson: "[\"автоконтроль\"]",
            updatedAt: now.AddDays(-14));
        var (scheduler, provider) = CreateAutomationScheduler(db, new Dictionary<string, string?>
        {
            ["TaskJournal:Automation:Enabled"] = "true",
            ["TaskJournal:Automation:InactiveDays"] = "14",
            ["TaskJournal:Automation:Limit"] = "10",
        });

        using (provider)
        {
            var result = await scheduler.RunOnceAsync(CancellationToken.None);

            Assert.Null(result.Error);
            Assert.Equal(1, result.CreatedCount);
        }

        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == recentCase.Id));
        Assert.Equal(0, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == recentCase.Id &&
            item.Status == "new"));
        Assert.Equal(2, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == readyCase.Id));
        Assert.Equal(1, await db.TaskJournalItems.CountAsync(item =>
            item.ClientCaseId == readyCase.Id &&
            item.Status == "new"));
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
            clientCaseId: null,
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
            clientCaseId: null,
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
    public async Task ExportCalendar_FiltersTasksByClientCase()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-case-ics@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент календаря дел");
        var selectedCase = await CreateCaseAsync(db, client.Id, "Выбранное дело");
        var otherCase = await CreateCaseAsync(db, client.Id, "Другое дело");
        var selectedTask = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: selectedCase.Id);
        selectedTask.Title = "Задача выбранного дела";
        selectedTask.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var otherTask = await CreateTaskAsync(db, author.Id, client.Id, clientCaseId: otherCase.Id);
        otherTask.Title = "Задача другого дела";
        otherTask.DueAt = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientCaseId: selectedCase.Id,
            clientId: null,
            legalEntityId: null,
            reminderMinutes: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("SUMMARY:Задача выбранного дела", content);
        Assert.DoesNotContain("SUMMARY:Задача другого дела", content);

        var auditEvent = await db.ObjectAuditEvents.SingleAsync(item => item.ObjectType == CalendarExportAudit.ObjectType);
        using var metadata = JsonDocument.Parse(auditEvent.MetadataJson!);
        Assert.Equal(selectedCase.Id, metadata.RootElement.GetProperty("filters").GetProperty("clientCaseId").GetInt32());
        Assert.Equal(1, metadata.RootElement.GetProperty("exportedEventCount").GetInt32());
    }

    [Fact]
    public async Task ExportCalendar_AppliesTaskVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-ics-own@example.com", "Ограниченный пользователь", canEdit: false);
        var other = await CreateTaskUserAsync(db, "task-ics-other@example.com", "Другой автор", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент календаря с ограничением");
        var visibleTask = await CreateTaskAsync(db, current.Id, client.Id);
        visibleTask.Title = "Своя задача для календаря";
        visibleTask.DueAt = new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc);
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id);
        hiddenTask.Title = "Чужая задача для календаря";
        hiddenTask.DueAt = new DateTime(2026, 5, 11, 9, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.ExportCalendar(
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            reminderMinutes: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        var content = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("SUMMARY:Своя задача для календаря", content);
        Assert.DoesNotContain("SUMMARY:Чужая задача для календаря", content);

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
            clientCaseId: null,
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
            clientCaseId: null,
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
    public async Task ExportPlanningReport_AppliesTaskVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "planning-own@example.com", "Ограниченный планировщик", canEdit: false);
        var other = await CreateTaskUserAsync(db, "planning-other@example.com", "Другой планировщик", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент планинга с ограничением");
        var visibleTask = await CreateTaskAsync(db, current.Id, client.Id, result: "Видимый результат");
        visibleTask.Title = "Своя задача для планинга";
        visibleTask.Status = "waiting";
        visibleTask.DueAt = DateTime.UtcNow.AddDays(-1);
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, result: "Скрытый результат");
        hiddenTask.Title = "Чужая задача для планинга";
        hiddenTask.Status = "waiting";
        hiddenTask.DueAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

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

        Assert.Contains("Своя задача для планинга", content);
        Assert.Contains("Видимый результат", content);
        Assert.DoesNotContain("Чужая задача для планинга", content);
        Assert.DoesNotContain("Скрытый результат", content);
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
        await CreateTaskAsync(db, author.Id, client.Id, result: "Комментарий & проверка <готово>");
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

        Assert.Equal(HtmlWordDocumentBuilder.ContentType, file.ContentType);
        Assert.EndsWith(".docx", file.FileDownloadName);
        Assert.True(file.FileContents.Length > 1_000);
        Assert.Equal((byte)'P', file.FileContents[0]);
        Assert.Equal((byte)'K', file.FileContents[1]);

        using var archive = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        var documentXml = reader.ReadToEnd();

        Assert.Contains("<w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/>", documentXml, StringComparison.Ordinal);
        Assert.Contains("<w:tblW w:w=\"15478\" w:type=\"dxa\"/>", documentXml, StringComparison.Ordinal);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(documentXml, "<w:gridCol ").Count);
        Assert.Contains("КОММЕНТАРИЙ", documentXml, StringComparison.Ordinal);
        Assert.Contains("Иванов Иван Иванович", documentXml, StringComparison.Ordinal);
        Assert.Contains("Комментарий &amp; проверка &lt;готово&gt;", documentXml, StringComparison.Ordinal);
        Assert.DoesNotContain("w:altChunk", documentXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPlanningReport_WordAllowsLongCompletedTaskRowsToContinueOnNextPage()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "planning-long-row@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент с длинным результатом");
        var task = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            result: string.Join(' ', Enumerable.Repeat("Подробный комментарий к завершённой задаче.", 600)),
            status: "done");
        task.Title = "Завершённая задача с длинным результатом";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var result = await controller.ExportPlanningReport(
            format: "word",
            search: null,
            status: "all",
            type: null,
            assigneeUserId: null,
            from: null,
            to: null,
            clientCaseId: null,
            clientId: null,
            legalEntityId: null,
            ct: CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        using var archive = new ZipArchive(new MemoryStream(file.FileContents), ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open(), Encoding.UTF8);
        var document = XDocument.Parse(reader.ReadToEnd());
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var taskRow = Assert.Single(
            document.Descendants(word + "tr"),
            row => row
                .Descendants(word + "t")
                .Any(text => text.Value == task.Title));

        Assert.Null(taskRow.Element(word + "trPr")?.Element(word + "cantSplit"));
        Assert.Contains("Все доступные задачи", document.Root!.Value, StringComparison.Ordinal);
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
    public async Task GetNotifications_ExcludesHiddenTasks()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "notifications-own@example.com", "Ограниченный пользователь", canEdit: false);
        var other = await CreateTaskUserAsync(db, "notifications-other@example.com", "Другой пользователь", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент уведомлений задач");
        var visibleTask = await CreateTaskAsync(db, current.Id, client.Id, current.Id);
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        visibleTask.DueAt = DateTime.UtcNow.AddMinutes(20);
        hiddenTask.DueAt = DateTime.UtcNow.AddMinutes(20);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.GetNotifications(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var notifications = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalNotificationDto>>(ok.Value);
        Assert.Contains(notifications, notification => notification.TaskId == visibleTask.Id);
        Assert.DoesNotContain(notifications, notification => notification.TaskId == hiddenTask.Id);
    }

    [Fact]
    public async Task CreateTask_CreatesParticipantsAndPrimaryAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author@example.com", "Автор");
        var assignee = await CreateAdminUserAsync(db, "assignee@example.com", "Исполнитель");
        var additionalAssignee = await CreateAdminUserAsync(db, "additional-assignee@example.com", "Дополнительный исполнитель");
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
                new() { UserId = additionalAssignee.Id, Role = "assignee", SortOrder = 1 },
                new() { UserId = approver.Id, Role = "approver", SortOrder = 0 },
                new() { UserId = observer.Id, Role = "observer", SortOrder = 0 },
            ],
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(created.Value);

        Assert.Equal(assignee.Id, dto.AssigneeUserId);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == assignee.Id);
        Assert.Contains(dto.Participants, item => item.Role == "assignee" && item.UserId == additionalAssignee.Id);
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
        Assert.Equal(GetDefaultProfileToday().AddDays(5), payment.Date);
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
        Assert.Equal(GetDefaultProfileToday().AddDays(5), payment.Date);
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
        Assert.Equal(GetDefaultProfileToday().AddDays(5), updatedPayment.Date);
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
            IsExternalCalendarVisible = true,
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var comment = Assert.IsType<TaskJournalCommentDto>(created.Value);

        Assert.Equal("comment", comment.Kind);
        Assert.Equal("Клиент прислал документы.", comment.Text);
        Assert.Equal(author.Id, comment.AuthorUserId);
        Assert.True(comment.IsExternalCalendarVisible);
        Assert.Equal("crm", comment.ExternalSource);
        Assert.Equal("verified-crm-user", comment.ExternalAuthorTrust);
        Assert.Single(notificationService.CommentTaskIds, task.Id);
    }

    [Fact]
    public async Task CommentEndpoints_ReturnNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-comments-own@example.com", "Ограниченный пользователь", canEdit: true);
        var other = await CreateTaskUserAsync(db, "task-comments-other@example.com", "Другой автор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент комментариев задач");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        db.TaskJournalComments.Add(new TaskJournalComment
        {
            TaskJournalItemId = hiddenTask.Id,
            AuthorUserId = other.Id,
            AuthorName = other.FullName,
            Kind = "comment",
            Text = "Скрытый комментарий",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var commentsResult = await controller.GetComments(hiddenTask.Id, CancellationToken.None);
        var createResult = await controller.CreateComment(hiddenTask.Id, new CreateTaskJournalCommentRequest
        {
            Text = "Попытка добавить комментарий",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(commentsResult.Result);
        Assert.IsType<NotFoundResult>(createResult.Result);
        Assert.Equal(1, await db.TaskJournalComments.CountAsync(item => item.TaskJournalItemId == hiddenTask.Id));
    }

    [Fact]
    public async Task UploadFile_AllowsExecutorWithPersonalCompletionAccess()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "upload-result-author@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "upload-result-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент с итоговым файлом");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        db.SystemSettings.Add(new SystemSetting
        {
            Key = "tasks.executorCompletionAllowedUserIds",
            Value = $"[{assignee.Id}]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFile("Итоговый документ", "результат.txt"),
            null,
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var file = Assert.IsType<TaskJournalFileDto>(created.Value);
        Assert.Equal(assignee.Id, file.UploadedByUserId);
        Assert.Equal("результат.txt", file.OriginalFileName);

        var adminController = CreateController(db, new RecordingNotificationService(), author);
        Assert.IsType<NoContentResult>(await adminController.DeleteFile(task.Id, file.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFile_AllowsExecutorToAttachCorrectedResultAfterRevision()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "revision-file-author@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "revision-file-assignee@example.com", "Исполнитель", canEdit: false);
        var approver = await CreateAdminUserAsync(db, "revision-file-approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент исправленного документа");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id, status: "revision");
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "revisionRequested",
            Resolution = "Приложите исправленный документ.",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFile("Исправленный документ", "исправлено.txt"),
            null,
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var file = Assert.IsType<TaskJournalFileDto>(created.Value);
        Assert.Equal(assignee.Id, file.UploadedByUserId);
        Assert.Equal("исправлено.txt", file.OriginalFileName);

        var adminController = CreateController(db, new RecordingNotificationService(), author);
        Assert.IsType<NoContentResult>(await adminController.DeleteFile(task.Id, file.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFile_AllowsAssignedExecutorWithoutPersonalCompletionAccess()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "upload-forbidden-author@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "upload-forbidden-assignee@example.com", "Исполнитель", canEdit: false);
        var client = await CreateClientAsync(db, "Клиент без доступа к итоговым файлам");
        var task = await CreateTaskAsync(db, author.Id, client.Id, assignee.Id);
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFile("Рабочий документ", "результат.txt"),
            null,
            CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var file = Assert.IsType<TaskJournalFileDto>(created.Value);
        Assert.Equal(assignee.Id, file.UploadedByUserId);
        Assert.Single(await db.TaskJournalFiles.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());

        Assert.IsType<ForbidResult>(await controller.DeleteFile(task.Id, file.Id, CancellationToken.None));
        Assert.Single(await db.TaskJournalFiles.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());

        var adminController = CreateController(db, new RecordingNotificationService(), author);
        Assert.IsType<NoContentResult>(await adminController.DeleteFile(task.Id, file.Id, CancellationToken.None));
    }

    [Fact]
    public async Task UploadFile_ForbidsObserverEvenWhenTaskIsVisible()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "upload-observer-author@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "upload-observer-assignee@example.com", "Исполнитель", canEdit: false);
        var observer = await CreateTaskUserAsync(db, "upload-observer@example.com", "Наблюдатель", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент задачи с наблюдателем");
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

        var result = await controller.UploadFile(
            task.Id,
            CreateFormFile("Недопустимая загрузка", "observer.txt"),
            null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.TaskJournalFiles.Where(item => item.TaskJournalItemId == task.Id).ToListAsync());
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

        var latestFilesResult = await controller.GetFiles(task.Id, CancellationToken.None, latestOnly: true);
        var latestFilesOk = Assert.IsType<OkObjectResult>(latestFilesResult.Result);
        var latestFiles = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalFileDto>>(latestFilesOk.Value);
        var latestFile = Assert.Single(latestFiles);
        Assert.Equal(second.Id, latestFile.Id);
        Assert.True(latestFile.IsLatestVersion);

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
    public async Task GetFiles_HidesMissingFilesAndFallsBackToLatestAvailableVersion()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-files@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var firstResult = await controller.UploadFile(
            task.Id,
            CreateFormFile("первая версия", "Документ.txt"),
            null,
            CancellationToken.None);
        var first = Assert.IsType<TaskJournalFileDto>(
            Assert.IsType<CreatedResult>(firstResult.Result).Value);
        var secondResult = await controller.UploadFile(
            task.Id,
            CreateFormFile("вторая версия", "Документ.txt"),
            first.DocumentKey,
            CancellationToken.None);
        var second = Assert.IsType<TaskJournalFileDto>(
            Assert.IsType<CreatedResult>(secondResult.Result).Value);
        var secondStored = await db.TaskJournalFiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == second.Id);
        var secondPath = Path.Combine(
            AppContext.BaseDirectory,
            "App_Data",
            "task-files",
            secondStored.StoragePath);
        File.Delete(secondPath);

        var allResult = await controller.GetFiles(task.Id, CancellationToken.None);
        var allFiles = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalFileDto>>(
            Assert.IsType<OkObjectResult>(allResult.Result).Value);
        var availableFile = Assert.Single(allFiles);
        Assert.Equal(first.Id, availableFile.Id);
        Assert.True(availableFile.IsLatestVersion);

        var latestResult = await controller.GetFiles(task.Id, CancellationToken.None, latestOnly: true);
        var latestFiles = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalFileDto>>(
            Assert.IsType<OkObjectResult>(latestResult.Result).Value);
        Assert.Equal(first.Id, Assert.Single(latestFiles).Id);

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
    public async Task FileEndpoints_ReturnNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-files-own@example.com", "Ограниченный пользователь", canEdit: true);
        var other = await CreateTaskUserAsync(db, "task-files-other@example.com", "Другой автор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент файлов задач");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var hiddenFile = new TaskJournalFile
        {
            TaskJournalItemId = hiddenTask.Id,
            DocumentKey = Guid.NewGuid().ToString("N"),
            Version = 1,
            OriginalFileName = "hidden.txt",
            StoredFileName = "hidden.txt",
            StoragePath = Path.Combine(hiddenTask.Id.ToString(), "hidden.txt"),
            ContentType = "text/plain",
            SizeBytes = 10,
            UploadedByUserId = other.Id,
            UploadedByName = other.FullName,
            UploadedAt = DateTime.UtcNow,
        };
        db.TaskJournalFiles.Add(hiddenFile);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var filesResult = await controller.GetFiles(hiddenTask.Id, CancellationToken.None);
        var uploadResult = await controller.UploadFile(
            hiddenTask.Id,
            CreateFormFile("hidden update", "hidden-update.txt"),
            null,
            CancellationToken.None);
        var downloadResult = await controller.DownloadFile(hiddenTask.Id, hiddenFile.Id, CancellationToken.None);
        var deleteResult = await controller.DeleteFile(hiddenTask.Id, hiddenFile.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(filesResult.Result);
        Assert.IsType<NotFoundResult>(uploadResult.Result);
        Assert.IsType<NotFoundResult>(downloadResult);
        Assert.IsType<NotFoundResult>(deleteResult);
        Assert.True(await db.TaskJournalFiles.AnyAsync(item => item.Id == hiddenFile.Id));
    }

    [Fact]
    public async Task UpdateStatus_DoneRequiresApprovedApprovers()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateTaskUserAsync(db, "author@example.com", "Автор", canEdit: false);
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
    public async Task UpdateStatus_AdminCanCompleteTaskWithPendingApproversWhenSettingEnabled()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateAdminUserAsync(db, "admin-force-complete@example.com", "Администратор");
        var approver = await CreateAdminUserAsync(db, "approver-force-complete@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, admin.Id, client.Id, admin.Id, result: null);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, admin);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "done",
            Result = "Проверено администратором",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("done", dto.Status);
        Assert.Equal("Проверено администратором", dto.Result);
        Assert.Contains(task.Id, notifications.CompletedTaskIds);
    }

    [Fact]
    public async Task UpdateStatus_DoneRequiresResultByDefault()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-result-default@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
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
        var author = await CreateAdminUserAsync(db, "author-result-disabled@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
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
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: "Документы подготовлены и приложены.");
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
        var revisionDueAt = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2).AddHours(10), DateTimeKind.Utc);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "revisionRequested",
            Resolution = "Нужно исправить дату.",
            RevisionDueAt = revisionDueAt,
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);

        Assert.Equal("revision", dto.Status);
        var storedTask = await db.TaskJournalItems.AsNoTracking().SingleAsync(item => item.Id == task.Id);
        Assert.Equal(revisionDueAt, storedTask.DueAt);
        Assert.Contains(dto.Participants, item =>
            item.Role == "approver"
            && item.UserId == approver.Id
            && item.ApprovalStatus == "revisionRequested"
            && item.Resolution == "Нужно исправить дату.");

        var commentsResult = await controller.GetComments(task.Id, CancellationToken.None);
        var commentsOk = Assert.IsType<OkObjectResult>(commentsResult.Result);
        var comments = Assert.IsAssignableFrom<IReadOnlyList<TaskJournalCommentDto>>(commentsOk.Value);
        var history = Assert.Single(comments, item => item.Kind == "system" && item.Text.Contains("Возвращено на доработку", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Документы подготовлены и приложены.", history.Text, StringComparison.OrdinalIgnoreCase);
        using var metadata = JsonDocument.Parse(history.MetadataJson);
        Assert.Equal("approvalDecision", metadata.RootElement.GetProperty("kind").GetString());
        Assert.Equal(approver.Id, metadata.RootElement.GetProperty("actorUserId").GetInt32());
        Assert.Equal(approver.FullName, metadata.RootElement.GetProperty("actorName").GetString());
        Assert.Equal("revisionRequested", metadata.RootElement.GetProperty("decision").GetString());
        Assert.Equal("Документы подготовлены и приложены.", metadata.RootElement.GetProperty("taskResult").GetString());
        Assert.Equal("revision", metadata.RootElement.GetProperty("resultingStatus").GetString());
        Assert.Equal(revisionDueAt, metadata.RootElement.GetProperty("revisionDueAt").GetDateTime());
        Assert.Equal(approver.Id, metadata.RootElement.GetProperty("approverUserId").GetInt32());
    }

    [Fact]
    public async Task UpdateApproval_RevisionRequiresReviewText()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-revision-review@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-revision-review@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент рецензии");
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
            Resolution = "   ",
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("рецензию", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal("onApproval", stored.Status);
        Assert.Contains(stored.Participants, item =>
            item.UserId == approver.Id
            && item.ApprovalStatus == "pending"
            && item.RespondedAt == null);
        Assert.False(await db.TaskJournalComments.AnyAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task UpdateApproval_RevisionKeepsDueAtWhenDueAtIsEmpty()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-revision-due@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-revision-due@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент срока доработки");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id, result: null);
        var originalDueAt = task.DueAt;
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
            Resolution = "Нужно доработать документы.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("revision", dto.Status);

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .SingleAsync(item => item.Id == task.Id);
        Assert.Equal("revision", stored.Status);
        Assert.Equal(originalDueAt, stored.DueAt);
        Assert.Contains(stored.Participants, item =>
            item.UserId == approver.Id
            && item.ApprovalStatus == "revisionRequested"
            && item.RespondedAt != null);
        Assert.True(await db.TaskJournalComments.AnyAsync(item => item.TaskJournalItemId == task.Id));
    }

    [Fact]
    public async Task UpdateApproval_FinalApproverCanApproveAndCompleteTask()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-approval-complete@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-approval-complete@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент финального согласования");
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
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, approver);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
            FinalAction = "complete",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("done", dto.Status);
        Assert.Equal("Задача выполнена.", dto.Result);
        Assert.Contains(dto.Participants, item =>
            item.Role == "approver"
            && item.UserId == approver.Id
            && item.ApprovalStatus == "approved");
        Assert.Contains(task.Id, notifications.CompletedTaskIds);
    }

    [Fact]
    public async Task UpdateApproval_FinalApproverCanApproveAndReturnTaskToAssignee()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-approval-return@example.com", "Автор");
        var approver = await CreateAdminUserAsync(db, "approver-approval-return@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент возврата исполнителю");
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
        var notifications = new RecordingNotificationService();
        var controller = CreateController(db, notifications, approver);

        var result = await controller.UpdateApproval(task.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
            FinalAction = "returnToAssignee",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("approved", dto.Status);
        Assert.Equal(string.Empty, dto.Result);
        Assert.Contains(dto.Participants, item =>
            item.Role == "approver"
            && item.UserId == approver.Id
            && item.ApprovalStatus == "approved");
        Assert.Empty(notifications.CompletedTaskIds);
        Assert.Contains(notifications.ApprovalDecisionTaskIds, item => item.TaskId == task.Id && item.Decision == "approved");
    }

    [Fact]
    public async Task UpdateApproval_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-approval-own@example.com", "Ограниченный пользователь", canEdit: false);
        var other = await CreateTaskUserAsync(db, "task-approval-other@example.com", "Другой согласующий", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент согласования задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        hiddenTask.Status = "onApproval";
        hiddenTask.Participants.Add(new TaskJournalParticipant
        {
            UserId = other.Id,
            Role = "approver",
            DisplayName = other.FullName,
            Email = other.Email,
            ApprovalStatus = "pending",
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.UpdateApproval(hiddenTask.Id, new UpdateTaskJournalApprovalRequest
        {
            Decision = "approved",
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        var stored = await db.TaskJournalItems
            .Include(item => item.Participants)
            .Include(item => item.Comments)
            .SingleAsync(item => item.Id == hiddenTask.Id);
        Assert.Equal("onApproval", stored.Status);
        var participant = Assert.Single(stored.Participants, item => item.Role == "approver");
        Assert.Equal("pending", participant.ApprovalStatus);
        Assert.Empty(stored.Comments);
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
        Assert.Empty(notificationService.ChangedTasks);
        Assert.Equal(2, notificationService.ReassignedTasks.Count);
        Assert.All(notificationService.ReassignedTasks, notification =>
        {
            Assert.Equal(oldAssignee.Id, notification.PreviousAssigneeUserId);
            Assert.Equal("Исполнитель", notification.PreviousAssigneeName);
            Assert.Contains(notification.ChangeLabels, item =>
                item.Contains("Исполнитель:", StringComparison.OrdinalIgnoreCase)
                && item.Contains("Исполнитель", StringComparison.OrdinalIgnoreCase)
                && item.Contains("Новый исполнитель", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(notification.ChangeLabels, item =>
                item.Contains("Срок:", StringComparison.OrdinalIgnoreCase)
                && item.Contains("->", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(notification.ChangeLabels, item =>
                item.Contains("Приоритет:", StringComparison.OrdinalIgnoreCase)
                && item.Contains("Обычный", StringComparison.OrdinalIgnoreCase)
                && item.Contains("Срочно", StringComparison.OrdinalIgnoreCase));
        });
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
    public async Task BulkUpdateTasks_AppliesTaskVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "bulk-update-own@example.com", "Ограниченный редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "bulk-update-other@example.com", "Другой редактор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент массового изменения задач");
        var visibleTask = await CreateTaskAsync(db, current.Id, client.Id, current.Id);
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        visibleTask.Priority = "normal";
        hiddenTask.Priority = "normal";
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.BulkUpdateTasks(new BulkUpdateTaskJournalItemsRequest
        {
            Ids = [visibleTask.Id, hiddenTask.Id],
            Priority = "urgent",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(2, GetAnonymousInt(ok.Value, "requested"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "skipped"));

        var visiblePriority = await db.TaskJournalItems
            .Where(item => item.Id == visibleTask.Id)
            .Select(item => item.Priority)
            .SingleAsync();
        var hiddenPriority = await db.TaskJournalItems
            .Where(item => item.Id == hiddenTask.Id)
            .Select(item => item.Priority)
            .SingleAsync();
        Assert.Equal("urgent", visiblePriority);
        Assert.Equal("normal", hiddenPriority);
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
    public async Task BulkDeleteTasks_AppliesTaskVisibilityScope()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "bulk-delete-own@example.com", "Ограниченный редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "bulk-delete-other@example.com", "Другой редактор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент массового удаления задач");
        var visibleTask = await CreateTaskAsync(db, current.Id, client.Id, current.Id);
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.BulkDeleteTasks(new BulkDeleteTaskJournalItemsRequest
        {
            Ids = [visibleTask.Id, hiddenTask.Id],
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, GetAnonymousInt(ok.Value, "updated"));
        Assert.Equal(2, GetAnonymousInt(ok.Value, "requested"));
        Assert.Equal(1, GetAnonymousInt(ok.Value, "skipped"));

        Assert.False(await db.TaskJournalItems.AnyAsync(item => item.Id == visibleTask.Id));
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.Id == hiddenTask.Id));
        var visibleTaskIsDeleted = await db.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(item => item.Id == visibleTask.Id)
            .Select(item => EF.Property<bool>(item, "IsDeleted"))
            .SingleAsync();
        var hiddenTaskIsDeleted = await db.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(item => item.Id == hiddenTask.Id)
            .Select(item => EF.Property<bool>(item, "IsDeleted"))
            .SingleAsync();
        Assert.True(visibleTaskIsDeleted);
        Assert.False(hiddenTaskIsDeleted);
    }

    [Fact]
    public async Task DeleteTask_NotifiesCalendarCancellation()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-delete-cancel@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.DeleteTask(task.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var cancelled = Assert.Single(notificationService.CancelledTaskIds);
        Assert.Equal(task.Id, cancelled.TaskId);
        Assert.Equal("Удалена", cancelled.Action);
    }

    [Fact]
    public async Task DeleteTask_StopsPendingDeadlineChangeRequest()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-delete-deadline-request@example.com", "Автор");
        var client = await CreateClientAsync(db, "Клиент удалённой задачи");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var deadlineRequest = new TaskJournalDeadlineChangeRequest
        {
            TaskJournalItemId = task.Id,
            RequestedByUserId = author.Id,
            RequestedByName = author.FullName,
            ReviewerUserId = author.Id,
            ReviewerName = author.FullName,
            CurrentDueAt = task.DueAt,
            RequestedDueAt = task.DueAt.AddDays(1),
            Reason = "Нужно больше времени.",
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.TaskJournalDeadlineChangeRequests.Add(deadlineRequest);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), author);

        var deletion = await controller.DeleteTask(task.Id, CancellationToken.None);
        var approval = await controller.ApproveDeadlineChangeRequest(task.Id, deadlineRequest.Id, new ApproveTaskJournalDeadlineChangeRequest
        {
            ApprovedDueAt = deadlineRequest.RequestedDueAt,
        }, CancellationToken.None);

        Assert.IsType<NoContentResult>(deletion);
        Assert.False(await db.TaskJournalDeadlineChangeRequests.AnyAsync(item => item.Id == deadlineRequest.Id));
        var notFound = Assert.IsType<NotFoundObjectResult>(approval.Result);
        var message = notFound.Value?.GetType().GetProperty("message")?.GetValue(notFound.Value) as string;
        Assert.Contains("Задача удалена", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteTask_ReturnsNotFoundForHiddenTask()
    {
        await using var db = await CreateContextAsync();
        var current = await CreateTaskUserAsync(db, "task-delete-own@example.com", "Ограниченный редактор", canEdit: true);
        var other = await CreateTaskUserAsync(db, "task-delete-other@example.com", "Другой редактор", canEdit: true);
        var client = await CreateClientAsync(db, "Клиент удаления задачи");
        var hiddenTask = await CreateTaskAsync(db, other.Id, client.Id, other.Id);
        var controller = CreateController(db, new RecordingNotificationService(), current);

        var result = await controller.DeleteTask(hiddenTask.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(await db.TaskJournalItems.AnyAsync(item => item.Id == hiddenTask.Id));
        var hiddenTaskIsDeleted = await db.TaskJournalItems
            .IgnoreQueryFilters()
            .Where(item => item.Id == hiddenTask.Id)
            .Select(item => EF.Property<bool>(item, "IsDeleted"))
            .SingleAsync();
        Assert.False(hiddenTaskIsDeleted);
    }

    [Fact]
    public async Task UpdateStatus_CancelledSendsCancellationNotification()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "author-status-cancel@example.com", "Автор");
        var client = await CreateClientAsync(db, "Иванов Иван Иванович");
        var task = await CreateTaskAsync(db, author.Id, client.Id, author.Id);
        var notificationService = new RecordingNotificationService();
        var controller = CreateController(db, notificationService, author);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "cancelled",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("cancelled", dto.Status);
        Assert.Empty(notificationService.ChangedTasks);
        var cancelled = Assert.Single(notificationService.CancelledTaskIds);
        Assert.Equal(task.Id, cancelled.TaskId);
        Assert.Equal("Отменена", cancelled.Action);
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
        Assert.Contains(dto.Participants, participant =>
            participant.Role == "approver"
            && participant.UserId == firstApprover.Id
            && participant.ApprovalStatus == "approved"
            && participant.Resolution == "Первый шаг согласован.");
        Assert.Contains(dto.Participants, participant =>
            participant.Role == "approver"
            && participant.UserId == secondApprover.Id
            && participant.ApprovalStatus == "pending"
            && string.IsNullOrEmpty(participant.Resolution)
            && participant.RespondedAt == null);
    }

    [Fact]
    public async Task UpdateStatus_ReturnedTaskAllowsAssignedExecutorToResubmitWithoutCompletionAccess()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateAdminUserAsync(db, "returned-author@example.com", "Постановщик");
        var assignee = await CreateTaskUserAsync(db, "returned-assignee@example.com", "Исполнитель", canEdit: false);
        var approver = await CreateAdminUserAsync(db, "returned-approver@example.com", "Согласующий");
        var client = await CreateClientAsync(db, "Клиент возвращённой задачи");
        var task = await CreateTaskAsync(
            db,
            author.Id,
            client.Id,
            assignee.Id,
            result: null,
            status: "inProgress");
        task.DueAt = DateTime.UtcNow.AddDays(-30);
        task.Participants.Add(new TaskJournalParticipant
        {
            UserId = approver.Id,
            Role = "approver",
            DisplayName = approver.FullName,
            Email = approver.Email,
            ApprovalStatus = "revisionRequested",
            Resolution = "Исправьте результат.",
            RespondedAt = DateTime.UtcNow.AddDays(-1),
            SortOrder = 0,
        });
        await db.SaveChangesAsync();
        var controller = CreateController(db, new RecordingNotificationService(), assignee);

        var result = await controller.UpdateStatus(task.Id, new UpdateTaskJournalStatusRequest
        {
            Status = "onApproval",
            Result = "Исправленный результат приложен.",
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TaskJournalItemDto>(ok.Value);
        Assert.Equal("onApproval", dto.Status);
        Assert.Equal("Исправленный результат приложен.", dto.Result);
        var resetApprover = Assert.Single(dto.Participants, participant => participant.Role == "approver");
        Assert.Equal("pending", resetApprover.ApprovalStatus);
        Assert.True(string.IsNullOrEmpty(resetApprover.Resolution));
        Assert.Null(resetApprover.RespondedAt);
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
        IInvoiceDocumentPdfService? pdfService = null,
        IExternalCalendarSyncCoordinator? externalCalendarSyncCoordinator = null,
        IExternalCalendarTaskSyncOutbox? externalCalendarTaskSyncOutbox = null,
        CaseInactivityAutomationMutationGate? caseInactivityAutomationMutationGate = null,
        IRealtimeNotificationPublisher? realtimePublisher = null)
        => new(
            db,
            notificationService,
            pdfService: pdfService,
            externalCalendarSyncCoordinator: externalCalendarSyncCoordinator,
            externalCalendarTaskSyncOutbox: externalCalendarTaskSyncOutbox,
            caseInactivityAutomationMutationGate: caseInactivityAutomationMutationGate,
            realtimePublisher: realtimePublisher)
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

    private static async Task<PaymentContext> CreateContextAsync(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var optionsBuilder = new DbContextOptionsBuilder<PaymentContext>();
        if (interceptor is null)
        {
            optionsBuilder.UseSqlite(connection);
        }
        else
        {
            optionsBuilder
                .UseSqlite(connection, sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .AddInterceptors(interceptor);
        }

        var options = optionsBuilder.Options;
        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<PaymentContext> CreateMigratedContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;
        var db = new PaymentContext(options);
        await db.Database.MigrateAsync();
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

    private static DateTime GetDefaultProfileToday()
    {
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(null);
        return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date;
    }

    private static (TaskJournalAutomationSchedulerService Scheduler, ServiceProvider Provider) CreateAutomationScheduler(
        PaymentContext db,
        IReadOnlyDictionary<string, string?> values,
        INotificationService? notificationService = null,
        CaseInactivityAutomationMutationGate? mutationGate = null,
        IRealtimeNotificationPublisher? realtimePublisher = null)
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
        if (realtimePublisher is not null)
        {
            services.AddSingleton(realtimePublisher);
        }
        var provider = services.BuildServiceProvider();

        return (
            new TaskJournalAutomationSchedulerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                configuration,
                NullLogger<TaskJournalAutomationSchedulerService>.Instance,
                mutationGate: mutationGate),
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
        bool canExport = true,
        string viewScope = DataVisibilityScope.Own,
        bool canEditOthers = false,
        bool canCompleteOthers = false,
        bool canDeleteOthers = false)
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
                    CanEditOthers = canEditOthers,
                    CanCompleteOthers = canCompleteOthers,
                    CanDeleteOthers = canDeleteOthers,
                    ViewScope = viewScope,
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

    private static ClientCaseDocument BuildCaseDocument(
        int clientCaseId,
        User author,
        string documentKey,
        DateTime uploadedAt)
        => new()
        {
            ClientCaseId = clientCaseId,
            DocumentKey = documentKey,
            DocumentType = "other",
            Status = "draft",
            OriginalFileName = $"{documentKey}.pdf",
            StoredFileName = $"{documentKey}.pdf",
            StoragePath = $"tests/{documentKey}.pdf",
            ContentType = "application/pdf",
            SizeBytes = 12,
            UploadedByUserId = author.Id,
            UploadedByName = author.FullName,
            UploadedAt = uploadedAt,
        };

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
        DateTime? approvalDueAt = null,
        string tagsJson = "[]",
        DateTime? updatedAt = null,
        DateTime? dueAt = null)
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
            DueAt = dueAt ?? DateTime.UtcNow.AddHours(1),
            ApprovalDueAt = approvalDueAt,
            PlannedDurationMinutes = 60,
            Status = status,
            Result = result,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = tagsJson,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = updatedAt,
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        if (updatedAt.HasValue)
        {
            await db.TaskJournalItems
                .Where(item => item.Id == task.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.UpdatedAt, updatedAt.Value));
        }

        return task;
    }

    private static async Task SetVacationAsync(
        PaymentContext db,
        int userId,
        DateTime vacationUntil)
    {
        var now = DateTime.UtcNow;
        db.UserSettings.AddRange(
            new UserSetting
            {
                UserId = userId,
                Key = UserNotificationDoNotDisturbSettings.SettingKey,
                Value = UserNotificationDoNotDisturbSettings.Serialize(
                    new UserNotificationDoNotDisturbSnapshot(
                        Enabled: true,
                        WorkStart: "09:00",
                        WorkEnd: "18:00",
                        MuteWeekends: true,
                        VacationUntil: vacationUntil.ToString("yyyy-MM-dd"),
                        AllowCritical: true,
                        WorkDays: [1, 2, 3, 4, 5])),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new UserSetting
            {
                UserId = userId,
                Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey,
                Value = "Asia/Novosibirsk",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await db.SaveChangesAsync();
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

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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

    private sealed class RecordingExternalCalendarTaskSyncOutbox : IExternalCalendarTaskSyncOutbox
    {
        public List<int> TaskIds { get; } = [];

        public Task EnqueueAsync(
            int taskId,
            string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
            CancellationToken cancellationToken = default)
        {
            TaskIds.Add(taskId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExternalCalendarTaskSyncOutbox : IExternalCalendarTaskSyncOutbox
    {
        public Task EnqueueAsync(
            int taskId,
            string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Хранилище очереди временно недоступно.");
    }

    private sealed class ThrowingExternalCalendarSyncCoordinator : IExternalCalendarSyncCoordinator
    {
        public int SyncCalls { get; private set; }

        public Task SyncTaskForConnectedUsersAsync(
            int taskId,
            string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
            CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            throw new InvalidOperationException("Сетевой вызов не должен выполняться в HTTP-запросе создания задачи.");
        }

        public Task DeleteTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task HandleUserAccessRevokedAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingRealtimeNotificationPublisher : IRealtimeNotificationPublisher
    {
        public int TaskJournalChangeCount { get; private set; }

        public Task PublishUserNotificationAsync(
            UserNotification notification,
            bool toastEnabled = true,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            TaskJournalChangeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<int> CompletedTaskIds { get; } = [];
        public List<(int TaskId, string Action)> CancelledTaskIds { get; } = [];
        public List<int> AssignedTaskIds { get; } = [];
        public List<int> ApprovalRequestedTaskIds { get; } = [];
        public List<(int TaskId, string Decision)> ApprovalDecisionTaskIds { get; } = [];
        public List<int> ApprovalOverdueTaskIds { get; } = [];
        public List<int> DeadlineChangeRequestedTaskIds { get; } = [];
        public List<int> DeadlineChangeApprovedTaskIds { get; } = [];
        public List<int> DeadlineChangeRejectedTaskIds { get; } = [];
        public List<(int TaskId, IReadOnlyList<string> ChangeLabels, string? Comment, IReadOnlyCollection<int> AdditionalRecipientUserIds)> ChangedTasks { get; } = [];
        public List<(int TaskId, int? PreviousAssigneeUserId, string? PreviousAssigneeName, IReadOnlyList<string> ChangeLabels, string? Comment, IReadOnlyCollection<int> AdditionalRecipientUserIds)> ReassignedTasks { get; } = [];
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

        public Task<NotificationSettingsDto> UpdateSmsSettingsAsync(UpdateSmsNotificationSettingsRequest request, CancellationToken cancellationToken = default)
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

        public Task<bool> NotifyAsync(NotificationEventEnvelope notificationEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

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

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(TaskJournalItem task, string actorName, int actorUserId, string action, CancellationToken cancellationToken = default)
        {
            CancelledTaskIds.Add((task.Id, action));
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

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRequestedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
        {
            DeadlineChangeRequestedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeApprovedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
        {
            DeadlineChangeApprovedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRejectedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
        {
            DeadlineChangeRejectedTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            ApprovalOverdueTaskIds.Add(task.Id);
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
        {
            ChangedTasks.Add((task.Id, changeLabels, comment, additionalRecipientUserIds ?? Array.Empty<int>()));
            return Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());
        }

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
        {
            ReassignedTasks.Add((task.Id, previousAssigneeUserId, previousAssigneeName, changeLabels, comment, additionalRecipientUserIds ?? Array.Empty<int>()));
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

        public Task NotifyCalendarAccessChangedAsync(int roleId, IReadOnlyCollection<int> addedVisibleUserIds, IReadOnlyCollection<int> removedVisibleUserIds, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyExternalCalendarSyncWarningAsync(string providerName, string operation, string summary, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteMentionAsync(int noteId, int boardId, string boardTitle, string noteText, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteAssignedAsync(int noteId, int boardId, string boardTitle, string noteText, int assignedUserId, string actorName, int actorUserId, DateTime? reminderAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteReminderAsync(StickyNote note, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteImportantChangeAsync(int noteId, int boardId, string boardTitle, string noteText, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, int noteVersion, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentAssignedAsync(Payment payment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentChangedAsync(Payment payment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceCreatedAsync(Payment invoicePayment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceChangedAsync(Payment invoicePayment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActCreatedAsync(Act act, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActChangedAsync(Act act, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractCreatedAsync(Contract contract, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractExpiringAsync(Contract contract, int daysLeft, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseCourtEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseProceduralDeadlineAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseConversationAsync(ClientCase clientCase, string messageText, int attachmentCount, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessRequestedAsync(ClientCase clientCase, string requesterName, int requesterUserId, string? reason, DateTime requestedAt, IReadOnlyCollection<int> managerUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessResolvedAsync(ClientCase clientCase, bool granted, string actorName, int actorUserId, int requestedByUserId, string requestedByUserName, string? requestReason, string? comment, DateTime resolvedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

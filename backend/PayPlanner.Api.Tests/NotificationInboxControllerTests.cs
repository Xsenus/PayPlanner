using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class NotificationInboxControllerTests
{
    [Fact]
    public async Task GetTypes_IncludesRoutingMetadata()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "types@example.com");
        var controller = CreateController(db, user.Id);

        var result = controller.GetTypes();

        var types = Assert.IsAssignableFrom<IReadOnlyList<UserNotificationTypeDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var approval = Assert.Single(types, item => item.Key == UserNotificationTypes.TaskApprovalRequested);
        Assert.Equal("high", approval.Priority);
        Assert.Contains(NotificationChannels.InApp, approval.Channels);
        Assert.True(approval.Deduplicate);
        Assert.False(approval.Digestible);

        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtHearing && item.Priority == "high");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtStatusChanged && item.Priority == "high");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtDecisionPublished && item.Priority == "high");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CourtSyncFailed && item.Tone == "rose");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.CaseInactivityReminder && item.Priority == "high");
        Assert.Contains(types, item => item.Key == UserNotificationTypes.SystemAdminWarning && item.Tone == "rose");
    }

    [Fact]
    public async Task InboxActions_UpdateOnlyCurrentUserNotification()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current@example.com");
        var otherUser = await CreateUserAsync(db, "other@example.com");
        var currentNotification = CreateNotification(currentUser.Id, "task:1:changed");
        var otherNotification = CreateNotification(otherUser.Id, "task:2:changed");
        db.UserNotifications.AddRange(currentNotification, otherNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        Assert.Single(page.Items);
        Assert.Equal(currentNotification.Id, page.Items[0].Id);

        var readResult = await controller.MarkRead(currentNotification.Id, CancellationToken.None);
        var readDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(readResult.Result).Value);
        Assert.True(readDto.IsRead);

        var archiveResult = await controller.Archive(currentNotification.Id, CancellationToken.None);
        var archivedDto = Assert.IsType<UserNotificationDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        Assert.True(archivedDto.IsArchived);

        var deleteResult = await controller.Delete(currentNotification.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);

        await db.Entry(currentNotification).ReloadAsync();
        await db.Entry(otherNotification).ReloadAsync();
        Assert.NotNull(currentNotification.DeletedAt);
        Assert.Null(otherNotification.DeletedAt);
    }

    [Fact]
    public async Task GetUnreadCount_IgnoresArchivedReadAndDeletedNotifications()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "unread@example.com");
        var unread = CreateNotification(user.Id, "task:1:changed");
        var read = CreateNotification(user.Id, "task:2:changed");
        read.IsRead = true;
        read.ReadAt = DateTime.UtcNow;
        var archived = CreateNotification(user.Id, "task:3:changed");
        archived.IsArchived = true;
        archived.ArchivedAt = DateTime.UtcNow;
        var deleted = CreateNotification(user.Id, "task:4:changed");
        deleted.DeletedAt = DateTime.UtcNow;
        db.UserNotifications.AddRange(
            unread,
            read,
            archived,
            deleted);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var result = await controller.GetUnreadCount(CancellationToken.None);
        var count = Assert.IsType<int>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetNotificationsAndUnreadCount_IgnoreDisabledUserNotificationTypes()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "disabled-types@example.com");
        var visible = CreateNotification(
            user.Id,
            "task:1:assigned",
            UserNotificationTypes.TaskAssigned,
            title: "Новая задача");
        var disabled = CreateNotification(
            user.Id,
            "task:2:changed",
            UserNotificationTypes.TaskChanged,
            title: "Изменение задачи");
        db.UserNotifications.AddRange(visible, disabled);
        db.UserSettings.Add(new UserSetting
        {
            UserId = user.Id,
            Key = UserNotificationPreferenceSettings.SettingKey,
            Value = """{"disabledTypes":["taskChanged"]}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);

        Assert.Single(page.Items);
        Assert.Equal(visible.Id, page.Items[0].Id);
        Assert.Equal(1, unreadCount);
    }

    [Fact]
    public async Task GetNotifications_HidesTaskNotificationWhenTaskIsNotVisible()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "visible-notifications@example.com");
        var otherUser = await CreateUserAsync(db, "hidden-task-owner@example.com");
        var visibleTask = CreateTask(currentUser.Id, "Видимая задача");
        var hiddenTask = CreateTask(otherUser.Id, "Чужая задача");
        db.TaskJournalItems.AddRange(visibleTask, hiddenTask);
        await db.SaveChangesAsync();
        var visible = CreateNotification(
            currentUser.Id,
            "task:visible:changed",
            UserNotificationTypes.TaskChanged,
            title: "Видимое уведомление");
        visible.TaskJournalItemId = visibleTask.Id;
        visible.ObjectId = visibleTask.Id;
        var hidden = CreateNotification(
            currentUser.Id,
            "task:hidden:changed",
            UserNotificationTypes.TaskChanged,
            title: "Скрытое уведомление");
        hidden.TaskJournalItemId = hiddenTask.Id;
        hidden.ObjectId = hiddenTask.Id;
        db.UserNotifications.AddRange(visible, hidden);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);

        Assert.Single(page.Items);
        Assert.Equal(visible.Id, page.Items[0].Id);
        Assert.Equal(1, unreadCount);
    }

    [Fact]
    public async Task GetNotifications_HidesObjectNotificationsWhenTargetObjectIsNotVisible()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "visible-object-notifications@example.com");
        var otherUser = await CreateUserAsync(db, "hidden-object-owner@example.com");

        var visibleClient = CreateClient("Visible client");
        visibleClient.ResponsibleUserId = currentUser.Id;
        var hiddenClient = CreateClient("Hidden client");
        hiddenClient.ResponsibleUserId = otherUser.Id;
        db.Clients.AddRange(visibleClient, hiddenClient);
        await db.SaveChangesAsync();

        var visibleCase = CreateCase(visibleClient, "Visible case");
        var hiddenCase = CreateCase(hiddenClient, "Hidden case");
        var visiblePayment = CreatePayment(visibleClient, DocumentKind.Payment, "visible-payment");
        var hiddenPayment = CreatePayment(hiddenClient, DocumentKind.Payment, "hidden-payment");
        var visibleInvoice = CreatePayment(visibleClient, DocumentKind.Invoice, "visible-invoice");
        var hiddenInvoice = CreatePayment(hiddenClient, DocumentKind.Invoice, "hidden-invoice");
        var visibleAct = CreateAct(visibleClient, "visible-act");
        var hiddenAct = CreateAct(hiddenClient, "hidden-act");
        var visibleContract = CreateContract(visibleClient, "visible-contract");
        var hiddenContract = CreateContract(hiddenClient, "hidden-contract");
        db.ClientCases.AddRange(visibleCase, hiddenCase);
        db.Payments.AddRange(visiblePayment, hiddenPayment, visibleInvoice, hiddenInvoice);
        db.Acts.AddRange(visibleAct, hiddenAct);
        db.Contracts.AddRange(visibleContract, hiddenContract);
        await db.SaveChangesAsync();

        var visibleDocument = CreateCaseDocument(visibleCase, "visible-document");
        var hiddenDocument = CreateCaseDocument(hiddenCase, "hidden-document");
        db.ClientCaseDocuments.AddRange(visibleDocument, hiddenDocument);
        await db.SaveChangesAsync();

        var visibleNotifications = new[]
        {
            CreateObjectNotification(currentUser.Id, "case", visibleCase.Id, "visible-case"),
            CreateObjectNotification(currentUser.Id, "client-case-document", visibleDocument.Id, "visible-document"),
            CreateObjectNotification(currentUser.Id, "payment", visiblePayment.Id, "visible-payment"),
            CreateObjectNotification(currentUser.Id, "invoice", visibleInvoice.Id, "visible-invoice"),
            CreateObjectNotification(currentUser.Id, "act", visibleAct.Id, "visible-act"),
            CreateObjectNotification(currentUser.Id, "contract", visibleContract.Id, "visible-contract"),
        };
        var hiddenNotifications = new[]
        {
            CreateObjectNotification(currentUser.Id, "case", hiddenCase.Id, "hidden-case"),
            CreateObjectNotification(currentUser.Id, "client-case-document", hiddenDocument.Id, "hidden-document"),
            CreateObjectNotification(currentUser.Id, "payment", hiddenPayment.Id, "hidden-payment"),
            CreateObjectNotification(currentUser.Id, "invoice", hiddenInvoice.Id, "hidden-invoice"),
            CreateObjectNotification(currentUser.Id, "act", hiddenAct.Id, "hidden-act"),
            CreateObjectNotification(currentUser.Id, "contract", hiddenContract.Id, "hidden-contract"),
        };
        db.UserNotifications.AddRange(visibleNotifications);
        db.UserNotifications.AddRange(hiddenNotifications);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications("active", null, null);
        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var unreadResult = await controller.GetUnreadCount(CancellationToken.None);
        var unreadCount = Assert.IsType<int>(Assert.IsType<OkObjectResult>(unreadResult.Result).Value);
        var hiddenReadResult = await controller.MarkRead(hiddenNotifications[0].Id, CancellationToken.None);
        var archiveResult = await controller.ArchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [visibleNotifications[0].Id, hiddenNotifications[1].Id],
        }, CancellationToken.None);
        var archiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);

        Assert.Equal(visibleNotifications.Length, page.Items.Count);
        Assert.Equal(visibleNotifications.Length, unreadCount);
        Assert.All(visibleNotifications, item => Assert.Contains(page.Items, dto => dto.Id == item.Id));
        Assert.All(hiddenNotifications, item => Assert.DoesNotContain(page.Items, dto => dto.Id == item.Id));
        Assert.IsType<NotFoundResult>(hiddenReadResult.Result);
        Assert.Equal(1, archiveDto.UpdatedCount);
    }

    [Fact]
    public async Task GetNotifications_FiltersTaskNotificationsByDateEmployeeClientAndRole()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "notification-filter-owner@example.com");
        var assignee = await CreateUserAsync(db, "notification-filter-assignee@example.com");
        var otherAssignee = await CreateUserAsync(db, "notification-filter-other@example.com");
        var targetClient = CreateClient("Target client");
        var otherClient = CreateClient("Other client");
        db.Clients.AddRange(targetClient, otherClient);
        await db.SaveChangesAsync();

        var targetTask = CreateTask(currentUser.Id, "Target task");
        targetTask.ClientId = targetClient.Id;
        targetTask.AssigneeUserId = assignee.Id;
        targetTask.AssigneeName = assignee.FullName;
        var otherTask = CreateTask(currentUser.Id, "Other task");
        otherTask.ClientId = otherClient.Id;
        otherTask.AssigneeUserId = otherAssignee.Id;
        otherTask.AssigneeName = otherAssignee.FullName;
        db.TaskJournalItems.AddRange(targetTask, otherTask);
        await db.SaveChangesAsync();

        var target = CreateNotification(currentUser.Id, "task:target:changed", title: "Target");
        target.TaskJournalItemId = targetTask.Id;
        target.ObjectId = targetTask.Id;
        target.CreatedAt = new DateTime(2026, 5, 20, 8, 30, 0, DateTimeKind.Utc);
        var wrongDate = CreateNotification(currentUser.Id, "task:target:old", title: "Old");
        wrongDate.TaskJournalItemId = targetTask.Id;
        wrongDate.ObjectId = targetTask.Id;
        wrongDate.CreatedAt = new DateTime(2026, 5, 19, 8, 30, 0, DateTimeKind.Utc);
        var wrongClientAndEmployee = CreateNotification(currentUser.Id, "task:other:changed", title: "Other");
        wrongClientAndEmployee.TaskJournalItemId = otherTask.Id;
        wrongClientAndEmployee.ObjectId = otherTask.Id;
        wrongClientAndEmployee.CreatedAt = new DateTime(2026, 5, 20, 8, 30, 0, DateTimeKind.Utc);
        db.UserNotifications.AddRange(target, wrongDate, wrongClientAndEmployee);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var listResult = await controller.GetNotifications(
            "active",
            null,
            null,
            dateFrom: "2026-05-20",
            dateTo: "2026-05-20",
            employeeId: assignee.Id,
            clientId: targetClient.Id,
            taskRole: "assignee");

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(target.Id, item.Id);
    }

    [Fact]
    public async Task MarkAllRead_UpdatesOnlyFilteredCurrentUserNotifications()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current-bulk@example.com");
        var otherUser = await CreateUserAsync(db, "other-bulk@example.com");
        var target = CreateNotification(
            currentUser.Id,
            "task:1:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок по задаче",
            body: "Нужно проверить срок");
        var otherType = CreateNotification(
            currentUser.Id,
            "task:2:changed",
            UserNotificationTypes.TaskChanged,
            title: "Срок изменен",
            body: "Нужно проверить срок");
        var otherSearch = CreateNotification(
            currentUser.Id,
            "task:3:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Другое событие",
            body: "Без совпадения");
        var archived = CreateNotification(
            currentUser.Id,
            "task:4:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок в архиве",
            body: "Нужно проверить срок");
        archived.IsArchived = true;
        archived.ArchivedAt = DateTime.UtcNow;
        var otherUserNotification = CreateNotification(
            otherUser.Id,
            "task:5:reminder",
            UserNotificationTypes.TaskReminder,
            title: "Срок по чужой задаче",
            body: "Нужно проверить срок");
        db.UserNotifications.AddRange(target, otherType, otherSearch, archived, otherUserNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var result = await controller.MarkAllRead(
            "active",
            UserNotificationTypes.TaskReminder,
            "срок",
            ct: CancellationToken.None);

        var dto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, dto.UpdatedCount);

        await db.Entry(target).ReloadAsync();
        await db.Entry(otherType).ReloadAsync();
        await db.Entry(otherSearch).ReloadAsync();
        await db.Entry(archived).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.True(target.IsRead);
        Assert.False(otherType.IsRead);
        Assert.False(otherSearch.IsRead);
        Assert.False(archived.IsRead);
        Assert.False(otherUserNotification.IsRead);
    }

    [Fact]
    public async Task DomainAndActionFilters_LimitInboxAndBulkRead()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "domain-action@example.com");
        var client = CreateClient("Domain action client");
        client.ResponsibleUserId = user.Id;
        db.Clients.Add(client);
        await db.SaveChangesAsync();

        var payment = CreatePayment(client, DocumentKind.Payment, "domain-action-payment");
        var act = CreateAct(client, "domain-action-act");
        db.Payments.Add(payment);
        db.Acts.Add(act);
        await db.SaveChangesAsync();

        var financeAction = CreateObjectNotification(user.Id, "payment", payment.Id, "Аванс требует решения");
        financeAction.Type = UserNotificationTypes.PaymentAdvanceReviewRequired;
        var financeInfo = CreateObjectNotification(user.Id, "act", act.Id, "Акт обновлен");
        financeInfo.Type = UserNotificationTypes.TaskChanged;
        var taskAction = CreateNotification(
            user.Id,
            "task:12:assigned",
            UserNotificationTypes.TaskAssigned,
            title: "Новая задача");
        db.UserNotifications.AddRange(financeAction, financeInfo, taskAction);
        await db.SaveChangesAsync();

        var controller = CreateController(db, user.Id);

        var listResult = await controller.GetNotifications(
            "active",
            null,
            null,
            domain: "finance",
            requiresAction: true);

        var page = Assert.IsType<PagedResponse<UserNotificationDto>>(Assert.IsType<OkObjectResult>(listResult.Result).Value);
        var item = Assert.Single(page.Items);
        Assert.Equal(financeAction.Id, item.Id);

        var markResult = await controller.MarkAllRead(
            "active",
            null,
            null,
            domain: "finance",
            requiresAction: true,
            ct: CancellationToken.None);

        var dto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(markResult.Result).Value);
        Assert.Equal(1, dto.UpdatedCount);

        await db.Entry(financeAction).ReloadAsync();
        await db.Entry(financeInfo).ReloadAsync();
        await db.Entry(taskAction).ReloadAsync();
        Assert.True(financeAction.IsRead);
        Assert.False(financeInfo.IsRead);
        Assert.False(taskAction.IsRead);
    }

    [Fact]
    public async Task BulkArchiveAndDelete_UpdateOnlySelectedCurrentUserNotifications()
    {
        await using var db = await CreateContextAsync();
        var currentUser = await CreateUserAsync(db, "current-selected-bulk@example.com");
        var otherUser = await CreateUserAsync(db, "other-selected-bulk@example.com");
        var first = CreateNotification(currentUser.Id, "task:1:changed");
        var second = CreateNotification(currentUser.Id, "task:2:changed");
        var third = CreateNotification(currentUser.Id, "task:3:changed");
        var otherUserNotification = CreateNotification(otherUser.Id, "task:4:changed");
        db.UserNotifications.AddRange(first, second, third, otherUserNotification);
        await db.SaveChangesAsync();

        var controller = CreateController(db, currentUser.Id);

        var archiveResult = await controller.ArchiveSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [first.Id, otherUserNotification.Id, 0, first.Id],
        }, CancellationToken.None);

        var archiveDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(archiveResult.Result).Value);
        Assert.Equal(1, archiveDto.UpdatedCount);

        await db.Entry(first).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.True(first.IsArchived);
        Assert.True(first.IsRead);
        Assert.False(otherUserNotification.IsArchived);

        var deleteResult = await controller.DeleteSelected(new PayPlanner.Api.Models.Requests.BulkIdsRequest
        {
            Ids = [second.Id, third.Id, otherUserNotification.Id],
        }, CancellationToken.None);

        var deleteDto = Assert.IsType<UserNotificationBulkActionResultDto>(Assert.IsType<OkObjectResult>(deleteResult.Result).Value);
        Assert.Equal(2, deleteDto.UpdatedCount);

        await db.Entry(second).ReloadAsync();
        await db.Entry(third).ReloadAsync();
        await db.Entry(otherUserNotification).ReloadAsync();
        Assert.NotNull(second.DeletedAt);
        Assert.NotNull(third.DeletedAt);
        Assert.Null(otherUserNotification.DeletedAt);
    }

    private static UserNotification CreateNotification(
        int userId,
        string eventKey,
        string type = UserNotificationTypes.TaskChanged,
        string title = "Изменение по задаче",
        string body = "Описание события")
        => new()
        {
            RecipientUserId = userId,
            Type = type,
            EventKey = eventKey,
            Title = title,
            BodyPreview = body,
            Tone = "blue",
            ObjectType = string.Empty,
            ObjectId = null,
            TaskJournalItemId = null,
            MetadataJson = "{}",
            CreatedAt = DateTime.UtcNow
        };

    private static UserNotification CreateObjectNotification(
        int userId,
        string objectType,
        int objectId,
        string title)
    {
        var notification = CreateNotification(
            userId,
            $"{objectType}:{objectId}:changed",
            title: title,
            body: $"{title} body");
        notification.ObjectType = objectType;
        notification.ObjectId = objectId;
        return notification;
    }

    private static TaskJournalItem CreateTask(int userId, string title)
        => new()
        {
            ObjectName = "Тестовое дело",
            Title = title,
            AuthorUserId = userId,
            AuthorName = $"user-{userId}",
            AssigneeUserId = userId,
            AssigneeName = $"user-{userId}",
            DueAt = DateTime.UtcNow.AddHours(2),
            PlannedDurationMinutes = 60,
            Type = "task",
            Priority = "normal",
            Status = "new",
            RemindersJson = "[]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static ClientCase CreateCase(Client client, string title)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            CaseNumber = title,
            Title = title,
            CaseType = "test",
            Stage = "active",
            Priority = "normal",
            CreatedAt = DateTime.UtcNow,
        };

    private static ClientCaseDocument CreateCaseDocument(ClientCase clientCase, string key)
        => new()
        {
            ClientCase = clientCase,
            ClientCaseId = clientCase.Id,
            DocumentKey = key,
            DocumentType = "test",
            Status = "created",
            OriginalFileName = $"{key}.txt",
            StoredFileName = $"{key}.txt",
            StoragePath = $"cases/{clientCase.Id}/{key}.txt",
            ContentType = "text/plain",
            UploadedByName = "Test",
            UploadedAt = DateTime.UtcNow,
        };

    private static Payment CreatePayment(Client client, DocumentKind documentKind, string account)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            Account = account,
            AccountDate = DateTime.UtcNow.Date,
            Amount = 1000m,
            Date = DateTime.UtcNow.Date,
            PlannedDate = DateTime.UtcNow.Date,
            Description = account,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = documentKind,
            CreatedAt = DateTime.UtcNow,
        };

    private static Act CreateAct(Client client, string number)
        => new()
        {
            Client = client,
            ClientId = client.Id,
            Number = number,
            Date = DateTime.UtcNow.Date,
            Amount = 1000m,
            CreatedAt = DateTime.UtcNow,
        };

    private static Contract CreateContract(Client client, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Date = DateTime.UtcNow.Date,
            Amount = 1000m,
            CreatedAt = DateTime.UtcNow,
        };
        contract.ClientContracts.Add(new ClientContract
        {
            Client = client,
            ClientId = client.Id,
            Contract = contract,
        });
        return contract;
    }

    private static Client CreateClient(string name)
        => new()
        {
            Name = name,
            Email = string.Empty,
            Phone = string.Empty,
            Company = string.Empty,
            Address = string.Empty,
            Notes = string.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

    private static NotificationInboxController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Test User")
        };

        return new NotificationInboxController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string email)
    {
        var role = new Role
        {
            Name = $"role-{Guid.NewGuid():N}",
            Description = "test",
            CreatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = email,
            FullName = email,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}

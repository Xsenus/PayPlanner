using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
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

    private static async Task<Client> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client { Name = name, IsActive = true };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ClientCase> CreateCaseAsync(PaymentContext db, int clientId, string title)
    {
        var clientCase = new ClientCase
        {
            ClientId = clientId,
            Title = title,
            Status = ClientCaseStatus.Open,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return clientCase;
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(
        PaymentContext db,
        int authorUserId,
        int clientId,
        int? assigneeUserId = null,
        int? clientCaseId = null,
        string? result = null)
    {
        var task = new TaskJournalItem
        {
            ClientId = clientId,
            ClientCaseId = clientCaseId,
            ObjectName = "Тестовый клиент",
            Title = "Тестовая задача",
            AuthorUserId = authorUserId,
            AuthorName = "Автор",
            AssigneeUserId = assigneeUserId,
            AssigneeName = "Исполнитель",
            DueAt = DateTime.UtcNow.AddHours(1),
            Status = "new",
            Result = result,
            RemindersJson = "[\"atDue\"]",
            ChannelsJson = "[\"inApp\"]",
            TagsJson = "[]",
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        return task;
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

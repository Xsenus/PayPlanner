using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class UsersControllerTests
{
    [Fact]
    public async Task AdminFilter_ReturnsForbid_ForUserWithoutAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "user");
        var controller = CreateController(db);
        var context = CreateActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.False(nextCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task AdminFilter_AllowsUserWithAdminRoleInDatabase()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db);
        var context = CreateActionExecutingContext(controller, user.Id);
        var nextCalled = false;

        await controller.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], controller));
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task GetUserContractorBalance_ReturnsSelectedExecutorPaymentsWithClientContext()
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, "admin@example.test", "Администратор", "admin");
        var contractor = await CreateUserAsync(db, "contractor@example.test", "Исполнитель", "lawyer", isExternalContractor: true);
        var otherContractor = await CreateUserAsync(db, "other@example.test", "Другой исполнитель", "lawyer", isExternalContractor: true);
        var client = new Client { Name = "Клиент расчета", IsActive = true };
        var clientCase = new ClientCase { Client = client, Title = "Дело расчета" };
        var payment = new Payment
        {
            Amount = 5_000m,
            PaidAmount = 1_500m,
            Status = PaymentStatus.Processing,
            Type = PaymentType.Expense,
            Date = new DateTime(2026, 6, 15),
            PlannedDate = new DateTime(2026, 6, 15),
        };
        db.TaskJournalItems.AddRange(
            new TaskJournalItem
            {
                Title = "Подготовить документы",
                ObjectName = "Тестовый объект",
                Status = "done",
                AssigneeUserId = contractor.Id,
                AssigneeName = contractor.FullName,
                AuthorUserId = admin.Id,
                AuthorName = admin.FullName,
                Client = client,
                ClientCase = clientCase,
                ContractorPaymentEnabled = true,
                ContractorPaymentAmount = 5_000m,
                ContractorPaymentDueDate = new DateTime(2026, 6, 20),
                ContractorPayment = payment,
                DueAt = new DateTime(2026, 6, 20),
            },
            new TaskJournalItem
            {
                Title = "Чужая задача",
                ObjectName = "Другой объект",
                Status = "done",
                AssigneeUserId = otherContractor.Id,
                AssigneeName = otherContractor.FullName,
                AuthorUserId = admin.Id,
                AuthorName = admin.FullName,
                ContractorPaymentEnabled = true,
                ContractorPaymentAmount = 9_000m,
                ContractorPaymentDueDate = new DateTime(2026, 6, 22),
                DueAt = new DateTime(2026, 6, 22),
            });
        await db.SaveChangesAsync();

        var result = await CreateController(db).GetUserContractorBalance(contractor.Id, CancellationToken.None);

        var balance = AssertBalance(result);
        Assert.Equal(3_500m, balance.OwedToMe);
        Assert.Equal(0m, balance.OwedByMe);
        Assert.Equal(1_500m, balance.PaidToMe);
        Assert.Equal(0m, balance.PaidByMe);
        var queued = Assert.Single(balance.Queue);
        Assert.Equal("Клиент расчета", queued.ClientName);
        Assert.Equal("Дело расчета", queued.ClientCaseTitle);
        Assert.Equal(3_500m, queued.OutstandingAmount);
        var paid = Assert.Single(balance.Paid);
        Assert.Equal(1_500m, paid.PaidAmount);
    }

    private static ContractorBalanceDto AssertBalance(ActionResult<ContractorBalanceDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<ContractorBalanceDto>(ok.Value);
    }

    private static UsersController CreateController(PaymentContext db)
        => new(CreateAuthService(db), db);

    private static AuthService CreateAuthService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        return new AuthService(
            db,
            configuration,
            new NoopEmailService(),
            new FakeWebHostEnvironment(),
            new PasswordHasher<User>());
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

    private static Task<User> CreateUserAsync(PaymentContext db, string roleName)
        => CreateUserAsync(
            db,
            $"{roleName}-{Guid.NewGuid():N}@example.test",
            roleName,
            roleName);

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string email,
        string fullName,
        string roleName,
        bool isExternalContractor = false)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == roleName)
            ?? new Role { Name = roleName, Description = roleName, CreatedAt = DateTime.UtcNow };
        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            IsActive = true,
            IsApproved = true,
            IsExternalContractor = isExternalContractor,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ActionExecutingContext CreateActionExecutingContext(
        UsersController controller,
        int userId)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(ClaimTypes.Name, $"User {userId}"),
                    new Claim(ClaimTypes.Role, "admin"),
                ],
                "TestAuth")),
        };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());
        controller.ControllerContext = new ControllerContext(actionContext);

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller);
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

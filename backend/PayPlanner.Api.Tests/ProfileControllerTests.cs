using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ProfileControllerTests
{
    [Fact]
    public async Task GetContractorBalance_ShowsOnlyCurrentUserExecutorPayments()
    {
        await using var db = await CreateContextAsync();
        var author = await CreateUserAsync(db, "author@example.com", "Постановщик");
        var contractor = await CreateUserAsync(db, "contractor@example.com", "Исполнитель", isExternalContractor: true);
        var task = new TaskJournalItem
        {
            Title = "Подготовить документы",
            Status = "done",
            AssigneeUserId = contractor.Id,
            AssigneeName = contractor.FullName,
            AuthorUserId = author.Id,
            AuthorName = author.FullName,
            DueAt = DateTime.UtcNow.AddDays(1),
            ContractorPaymentEnabled = true,
            ContractorPaymentAmount = 3_500m,
            ContractorPaymentDueDate = new DateTime(2026, 6, 10),
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();

        var authorResult = await CreateController(db, author).GetContractorBalance(CancellationToken.None);
        var contractorResult = await CreateController(db, contractor).GetContractorBalance(CancellationToken.None);

        var authorBalance = AssertBalance(authorResult);
        Assert.Equal(0m, authorBalance.OwedToMe);
        Assert.Equal(0m, authorBalance.OwedByMe);
        Assert.Empty(authorBalance.Queue);

        var contractorBalance = AssertBalance(contractorResult);
        Assert.Equal(3_500m, contractorBalance.OwedToMe);
        Assert.Equal(0m, contractorBalance.OwedByMe);
        var queued = Assert.Single(contractorBalance.Queue);
        Assert.Equal(task.Id, queued.TaskId);
        Assert.Equal("toMe", queued.Direction);
        Assert.Equal("Постановщик", queued.AuthorName);
    }

    private static ContractorBalanceDto AssertBalance(ActionResult<ContractorBalanceDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<ContractorBalanceDto>(ok.Value);
    }

    private static ProfileController CreateController(PaymentContext db, User user)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var passwordHasher = new PasswordHasher<User>();
        var authService = new AuthService(
            db,
            configuration,
            new NoopEmailService(),
            new FakeWebHostEnvironment(),
            passwordHasher);
        var externalAuthService = new ExternalAuthService(
            db,
            configuration,
            new FakeHttpClientFactory(),
            passwordHasher,
            authService,
            NullLogger<ExternalAuthService>.Instance);

        return new ProfileController(authService, externalAuthService, db, new FakeWebHostEnvironment())
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

    private static async Task<User> CreateUserAsync(PaymentContext db, string email, string fullName, bool isExternalContractor = false)
    {
        var role = await db.Roles.FirstOrDefaultAsync(item => item.Name == "profile-test")
            ?? new Role { Name = "profile-test", Description = "Profile test" };
        var user = new User
        {
            Email = email,
            PasswordHash = "hash",
            FullName = fullName,
            IsActive = true,
            IsApproved = true,
            IsExternalContractor = isExternalContractor,
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicesControllerValidationTests
{
    [Fact]
    public async Task Create_ReturnsHelpfulDuplicateMessage_WhenMatchingInvoiceIsHiddenByAccess()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Скрытый дубль");
        await CreateUserAsync(db, 2, "owner@example.com", "Ответственный владелец");
        await CreateInvoiceAsync(db, "25", new DateTime(2026, 4, 2), clientId, responsibleUserId: 2);

        var controller = CreateController(db, isAdmin: false, userId: 1);
        var request = CreateRequest("25", clientId);

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("Система проверяет уникальность по всем счетам за год", message);
        Assert.Contains("может быть скрыт текущими фильтрами", message);
    }

    [Fact]
    public async Task Create_ReturnsDuplicateDetails_WhenMatchingInvoiceIsVisibleToCurrentUser()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Видимый дубль");
        await CreateUserAsync(db, 2, "owner@example.com", "Ответственный владелец");
        await CreateInvoiceAsync(db, "25", new DateTime(2026, 4, 2), clientId, responsibleUserId: 2);

        var controller = CreateController(db, isAdmin: false, userId: 2);
        var request = CreateRequest("25", clientId);

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("Совпадающий доходный счет: №25 от 02.04.2026.", message);
        Assert.Contains("Клиент: Видимый дубль.", message);
    }

    private static UpsertInvoiceRequest CreateRequest(string number, int clientId)
    {
        return new UpsertInvoiceRequest
        {
            Number = number,
            Date = new DateTime(2026, 4, 8),
            DueDate = new DateTime(2026, 4, 10),
            Amount = 11_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            ClientId = clientId,
        };
    }

    private static async Task<int> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client
        {
            Name = name,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task CreateUserAsync(PaymentContext db, int id, string email, string fullName)
    {
        var role = await db.Roles.FirstOrDefaultAsync();
        if (role is null)
        {
            role = new Role
            {
                Name = "user",
                Description = "Test role",
            };

            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        db.Users.Add(new User
        {
            Id = id,
            Email = email,
            FullName = fullName,
            PasswordHash = "hash",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        });

        await db.SaveChangesAsync();
    }

    private static async Task CreateInvoiceAsync(PaymentContext db, string number, DateTime date, int clientId, int? responsibleUserId)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = number,
            AccountDate = date,
            InvoiceId = invoice.Id,
            ClientId = clientId,
            DocumentKind = DocumentKind.Invoice,
            Date = date,
            PlannedDate = date,
            Amount = 10_000m,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            Description = "Тестовый счет",
        };

        if (responsibleUserId.HasValue)
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleUserId.Value,
            });
        }

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
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

    private static InvoicesController CreateController(PaymentContext db, bool isAdmin, int? userId)
    {
        var claims = new List<Claim>();
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        if (userId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };

        return controller;
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}

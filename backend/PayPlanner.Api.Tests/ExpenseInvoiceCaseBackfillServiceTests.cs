using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ExpenseInvoiceCaseBackfillServiceTests
{
    [Fact]
    public async Task RunAsync_DryRunReportsExpenseClientCandidate_WithoutChangingPayment()
    {
        await using var db = await CreateContextAsync();
        var (_, clientCase, payment) = await CreateLegacyExpenseInvoiceAsync(db);

        var result = await ExpenseInvoiceCaseBackfillService.RunAsync(
            db,
            dryRun: true,
            limit: 100);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Applied);

        var item = Assert.Single(result.Items);
        Assert.Equal("WouldUpdate", item.Action);
        Assert.Equal(clientCase.ClientId, item.SuggestedExpenseClientId);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(entity => entity.Id == payment.Id);
        Assert.Null(storedPayment.ExpenseClientId);
    }

    [Fact]
    public async Task RunAsync_AppliesOnlyUnambiguousExpenseClientBackfill()
    {
        await using var db = await CreateContextAsync();
        var (_, clientCase, payment) = await CreateLegacyExpenseInvoiceAsync(db);

        var result = await ExpenseInvoiceCaseBackfillService.RunAsync(
            db,
            dryRun: false,
            limit: 100);

        Assert.False(result.DryRun);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Applied);

        var item = Assert.Single(result.Items);
        Assert.Equal("Updated", item.Action);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(entity => entity.Id == payment.Id);
        Assert.Equal(clientCase.ClientId, storedPayment.ExpenseClientId);
        Assert.Contains("[PayPlanner:ExpenseInvoiceCaseBackfill]", storedPayment.SystemNotes);

        var secondRun = await ExpenseInvoiceCaseBackfillService.RunAsync(db, dryRun: false, limit: 100);
        Assert.Equal(0, secondRun.Scanned);
        Assert.Equal(0, secondRun.Applied);
    }

    [Fact]
    public async Task RunAsync_SkipsConflictingExpenseClient_WhenPaymentIsSelectedExplicitly()
    {
        await using var db = await CreateContextAsync();
        var conflictClient = new Client { Name = "Другой клиент" };
        db.Clients.Add(conflictClient);
        await db.SaveChangesAsync();

        var (_, clientCase, payment) = await CreateLegacyExpenseInvoiceAsync(db);
        payment.ExpenseClientId = conflictClient.Id;
        await db.SaveChangesAsync();

        var result = await ExpenseInvoiceCaseBackfillService.RunAsync(
            db,
            dryRun: false,
            limit: 100,
            paymentIds: new[] { payment.Id });

        Assert.Equal(1, result.Scanned);
        Assert.Equal(0, result.Changed);
        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);

        var item = Assert.Single(result.Items);
        Assert.Equal("SkippedConflict", item.Action);
        Assert.Equal(clientCase.ClientId, item.SuggestedExpenseClientId);

        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(entity => entity.Id == payment.Id);
        Assert.Equal(conflictClient.Id, storedPayment.ExpenseClientId);
    }

    [Fact]
    public async Task BackfillExpenseInvoiceCases_RequiresConfirmBeforeApplying()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, roleName: "admin");
        var (_, _, payment) = await CreateLegacyExpenseInvoiceAsync(db);
        var controller = CreateController(db, user.Id, isAdminClaim: true);

        var result = await controller.BackfillExpenseInvoiceCases(
            new BackfillExpenseInvoiceCasesRequest { DryRun = false, Confirm = false },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var storedPayment = await db.Payments.AsNoTracking().SingleAsync(entity => entity.Id == payment.Id);
        Assert.Null(storedPayment.ExpenseClientId);
    }

    [Fact]
    public async Task BackfillExpenseInvoiceCases_ForbidsNonAdminUser()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, roleName: "lawyer");
        await CreateLegacyExpenseInvoiceAsync(db);
        var controller = CreateController(db, user.Id, isAdminClaim: false);

        var result = await controller.BackfillExpenseInvoiceCases(
            new BackfillExpenseInvoiceCasesRequest(),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private static async Task<(Client Supplier, ClientCase ClientCase, Payment Payment)> CreateLegacyExpenseInvoiceAsync(PaymentContext db)
    {
        var supplier = new Client { Name = "Поставщик" };
        var caseClient = new Client { Name = "Клиент дела" };
        db.Clients.AddRange(supplier, caseClient);
        await db.SaveChangesAsync();

        var clientCase = new ClientCase
        {
            ClientId = caseClient.Id,
            Title = "Дело клиента",
            Status = ClientCaseStatus.Open,
            CreatedAt = DateTime.UtcNow,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Account = "EXP-100",
            AccountDate = new DateTime(2026, 6, 23),
            Type = PaymentType.Expense,
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Pending,
            Amount = 5_000m,
            Date = new DateTime(2026, 6, 23),
            PlannedDate = new DateTime(2026, 6, 30),
            Description = "Старый расходный счет",
            CounterpartyName = supplier.Name,
            ClientId = supplier.Id,
            ClientCaseId = clientCase.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (supplier, clientCase, payment);
    }

    private static async Task<User> CreateUserAsync(PaymentContext db, string roleName)
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
            PermissionsUpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = $"{roleName}@example.com",
            PasswordHash = "hash",
            FullName = roleName,
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static InvoicesController CreateController(PaymentContext db, int userId, bool isAdminClaim)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Test User"),
        };
        if (isAdminClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
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

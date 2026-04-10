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
    private const string LongInvoiceLineItemName =
        "Юридические услуги / ОКАЗАНИЕ ЮРИДИЧЕСКИХ УСЛУГ В РАМКАХ ДОГОВОРА № 1-20/03-2026 ОТ 20.03.2026 Г. по вопросу оказания услуг реализации транспортного средства, организации подачи объявлений на площадках в сети интернет, организация просмотра транспортного средства, подготовки договора для продажи транспортного средства LADA VESTA GFL110; идентификационный номер (vin): XTAGFL110NY628724; год выпуска: октябрь 2021; государственный регистрационный номер: Е506ОХ763; номер кузова: XTAGFL110NY628724";

    [Fact]
    public async Task Create_IgnoresOrphanInvoiceRecord_WhenCheckingDuplicateNumber()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("25", clientId, new DateTime(2026, 4, 8));

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        Assert.NotNull(created.Value);
    }

    [Fact]
    public async Task Create_ReturnsShortDuplicateMessage_WhenActiveInvoiceExists()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "25", new DateTime(2026, 4, 7), clientId);

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("25", clientId, new DateTime(2026, 4, 8));

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Equal("Номер счета '25' уже занят в 2026 году.", message);
    }

    [Fact]
    public async Task GetNextNumber_IgnoresOrphanInvoiceRecord()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "24", new DateTime(2026, 4, 6), clientId);
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: 1);

        var action = await controller.GetNextNumber(new DateTime(2026, 4, 9), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var nextNumber = GetAnonymousProperty<string>(ok.Value, "nextNumber");
        Assert.Equal("25", nextNumber);
    }

    [Fact]
    public async Task CleanupOrphanInvoices_DeletesOnlyInvoicesWithoutLivePayments()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        await CreateActiveInvoiceAsync(db, "24", new DateTime(2026, 4, 6), clientId);
        await CreateOrphanInvoiceAsync(db, "25", new DateTime(2026, 4, 7));

        var controller = CreateController(db, isAdmin: true, userId: null);

        var action = await controller.CleanupOrphanInvoices(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var deleted = GetAnonymousProperty<int>(ok.Value, "deleted");
        Assert.Equal(1, deleted);

        var remainingNumbers = await db.Invoices.AsNoTracking().OrderBy(x => x.Number).Select(x => x.Number).ToListAsync();
        Assert.Equal(new[] { "24" }, remainingNumbers);
    }

    [Fact]
    public async Task Create_AllowsLongInvoiceLineItemName()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                Name = LongInvoiceLineItemName,
                Quantity = 1,
                Unit = "шт",
                Price = 22_500m,
            },
        ];
        request.Amount = 22_500m;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        Assert.NotNull(created.Value);

        var storedName = await db.InvoiceLineItems.AsNoTracking().Select(x => x.Name).SingleAsync();
        Assert.Equal(LongInvoiceLineItemName, storedName);
    }

    private static UpsertInvoiceRequest CreateRequest(string number, int clientId, DateTime date)
    {
        return new UpsertInvoiceRequest
        {
            Number = number,
            Date = date,
            DueDate = date.AddDays(2),
            Amount = 11_000m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            ClientId = clientId,
        };
    }

    private static T GetAnonymousProperty<T>(object? source, string name)
    {
        Assert.NotNull(source);
        var property = source!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(source));
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

    private static async Task CreateOrphanInvoiceAsync(PaymentContext db, string number, DateTime date)
    {
        db.Invoices.Add(new Invoice
        {
            Number = number,
            Date = date,
        });

        await db.SaveChangesAsync();
    }

    private static async Task CreateActiveInvoiceAsync(PaymentContext db, string number, DateTime date, int clientId)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.Payments.Add(new Payment
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
        });

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

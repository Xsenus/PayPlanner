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

    [Fact]
    public async Task Create_PersistsInvoiceLineItemServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var serviceId = await CreateServiceAsync(db, "Составление иска");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31A", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = serviceId,
                Name = "Составление иска по делу №42",
                Quantity = 1,
                Unit = "усл. ед.",
                Price = 22_500m,
            },
        ];
        request.Amount = 22_500m;

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        var lineItem = Assert.Single(dto.LineItems);
        Assert.Equal(serviceId, lineItem.ServiceId);

        var storedServiceId = await db.InvoiceLineItems.AsNoTracking().Select(x => x.ServiceId).SingleAsync();
        Assert.Equal(serviceId, storedServiceId);
    }

    [Fact]
    public async Task Create_RejectsUnknownInvoiceLineItemServiceId()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("31B", clientId, new DateTime(2026, 4, 10));
        request.LineItems =
        [
            new InvoiceLineItemRequest
            {
                ServiceId = 9999,
                Name = "Нестандартная услуга",
                Quantity = 1,
                Unit = "шт",
                Price = 1_000m,
            },
        ];

        var action = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal("Unknown invoice line item serviceId 9999", badRequest.Value);
    }

    [Fact]
    public async Task Create_AlignsDueDateWithLastInstallmentPaymentDate()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");

        var controller = CreateController(db, isAdmin: true, userId: null);
        var request = CreateRequest("32", clientId, new DateTime(2026, 4, 10));
        request.DueDate = new DateTime(2026, 4, 12);
        request.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var action = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action.Result);
        var dto = Assert.IsType<InvoiceDto>(created.Value);
        Assert.Equal(new DateTime(2026, 6, 10), dto.DueDate);

        var storedInvoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == dto.Id);
        Assert.Equal(new DateTime(2026, 6, 10), storedInvoicePayment.Date);
    }

    [Fact]
    public async Task Update_AlignsDueDateWithLastInstallmentPaymentDate()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("33", clientId, new DateTime(2026, 4, 10));
        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var updateRequest = CreateRequest("33", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 4, 12);
        updateRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        var dto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(new DateTime(2026, 6, 10), dto.DueDate);

        var storedInvoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);
        Assert.Equal(new DateTime(2026, 6, 10), storedInvoicePayment.Date);
    }

    [Fact]
    public async Task Update_PreservesExistingInstallmentSchedule_WhenInstallmentPlanIsOmitted()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("34", clientId, new DateTime(2026, 4, 10));
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);

        var updateRequest = CreateRequest("34", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 6, 10);

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedInstallments = await db.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.InvoiceId == invoicePayment.InvoiceId
                && payment.Id != invoicePayment.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains("[pp:installment-payment]"))
            .OrderBy(payment => payment.Date)
            .ToListAsync();

        Assert.Equal(2, storedInstallments.Count);
        Assert.Equal(new DateTime(2026, 5, 10), storedInstallments[0].Date);
        Assert.Equal(new DateTime(2026, 6, 10), storedInstallments[1].Date);
    }

    [Fact]
    public async Task Update_RemovesExistingInstallmentSchedule_WhenReplaceInstallmentPlanRequestedWithNullPlan()
    {
        await using var db = await CreateContextAsync();
        var clientId = await CreateClientAsync(db, "Тестовый клиент");
        var controller = CreateController(db, isAdmin: true, userId: null);

        var createRequest = CreateRequest("35", clientId, new DateTime(2026, 4, 10));
        createRequest.InstallmentPlan = CreateInstallmentPlanRequest(
            new DateTime(2026, 4, 10),
            new DateTime(2026, 5, 10),
            new DateTime(2026, 6, 10));

        var createAction = await controller.Create(createRequest, CancellationToken.None);
        var created = Assert.IsType<CreatedResult>(createAction.Result);
        var createdDto = Assert.IsType<InvoiceDto>(created.Value);

        var invoicePayment = await db.Payments.AsNoTracking().SingleAsync(payment => payment.Id == createdDto.Id);

        var updateRequest = CreateRequest("35", clientId, new DateTime(2026, 4, 10));
        updateRequest.DueDate = new DateTime(2026, 6, 10);
        updateRequest.ReplaceInstallmentPlan = true;
        updateRequest.InstallmentPlan = null;

        var updateAction = await controller.Update(createdDto.Id, updateRequest, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(updateAction.Result);
        Assert.IsType<InvoiceDto>(ok.Value);

        var storedInstallmentCount = await db.Payments
            .AsNoTracking()
            .CountAsync(payment =>
                payment.InvoiceId == invoicePayment.InvoiceId
                && payment.Id != invoicePayment.Id
                && payment.SystemNotes != null
                && payment.SystemNotes.Contains("[pp:installment-payment]"));

        Assert.Equal(0, storedInstallmentCount);
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

    private static InvoiceInstallmentPlanRequest CreateInstallmentPlanRequest(DateTime startDate, params DateTime[] dates)
    {
        var paymentAmount = dates.Length > 0 ? decimal.Round(11_000m / dates.Length, 2) : 11_000m;

        return new InvoiceInstallmentPlanRequest
        {
            Total = 11_000m,
            DownPayment = 0m,
            AnnualRate = 0m,
            Months = Math.Max(1, dates.Length),
            StartDate = startDate,
            RoundingMode = InstallmentRoundingMode.None,
            ToPay = 11_000m,
            Overpay = 0m,
            LoanAmount = 11_000m,
            BaseMonthlyPayment = paymentAmount,
            RoundedMonthlyPayment = paymentAmount,
            Items = dates.Select((itemDate, index) =>
            {
                var isLast = index == dates.Length - 1;
                var payment = isLast ? 11_000m - paymentAmount * (dates.Length - 1) : paymentAmount;
                var balance = isLast ? 0m : Math.Max(0m, 11_000m - paymentAmount * (index + 1));

                return new InvoiceInstallmentPlanItemRequest
                {
                    Sequence = index + 1,
                    Date = itemDate,
                    Principal = payment,
                    Interest = 0m,
                    Payment = payment,
                    Balance = balance,
                };
            }).ToList(),
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

    private static async Task<int> CreateServiceAsync(PaymentContext db, string name)
    {
        var service = new ServiceDictionaryItem
        {
            Name = name,
            Description = string.Empty,
            Unit = "усл. ед.",
            DefaultPrice = 0m,
            ColorHex = "#111827",
            IsActive = true,
        };

        db.Services.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
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

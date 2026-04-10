using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PaymentsControllerTests
{
    private const string InstallmentPaymentMarker = "[pp:installment-payment]";

    [Fact]
    public async Task Get_WithInstallmentFilter_ReturnsSerializableGraph_AndKeepsAuditMetadata()
    {
        await using var db = await CreateContextAsync();

        var clientStatus = new ClientStatusEntity
        {
            Name = "Активный",
            ColorHex = "#22C55E",
        };

        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
            ClientStatus = clientStatus,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по рассрочке",
        };

        var incomeType = new IncomeType
        {
            Name = "Оплата по делу",
            PaymentType = PaymentType.Income,
        };

        var invoice = new Invoice
        {
            Number = "INV-25",
            Date = new DateTime(2026, 3, 18),
        };

        var parentPayment = CreatePayment(new DateTime(2026, 3, 18), 25_000m, "Счёт с рассрочкой");
        parentPayment.Client = client;
        parentPayment.ClientCase = clientCase;
        parentPayment.IncomeType = incomeType;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.Invoice = invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var installmentPayment = CreatePayment(new DateTime(2026, 3, 18), 12_500m, "Платёж по рассрочке");
        installmentPayment.Client = client;
        installmentPayment.ClientCase = clientCase;
        installmentPayment.IncomeType = incomeType;
        installmentPayment.Invoice = invoice;
        installmentPayment.InvoiceId = invoice.Id;
        installmentPayment.SystemNotes = $"{InstallmentPaymentMarker}\nСоздан автоматически";

        var unrelatedPayment = CreatePayment(new DateTime(2026, 3, 20), 7_500m, "Обычный платёж");
        unrelatedPayment.Client = client;
        unrelatedPayment.ClientCase = clientCase;
        unrelatedPayment.IncomeType = incomeType;

        db.Payments.AddRange(parentPayment, installmentPayment, unrelatedPayment);
        await db.SaveChangesAsync();

        db.Entry(installmentPayment).Property("CreatedByUserName").CurrentValue = "Тестовый автор";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31),
            null,
            null,
            excludeInstallmentParents: true,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        Assert.Equal(2, payments.Count);
        Assert.DoesNotContain(payments, payment => payment.Id == parentPayment.Id);

        var installmentFromResult = Assert.Single(payments, payment => payment.Id == installmentPayment.Id);
        Assert.Equal("Тестовый автор", installmentFromResult.AuditCreatedByUserName);

        var json = JsonSerializer.Serialize(payments);
        Assert.StartsWith("[", json);
    }

    [Fact]
    public async Task Get_FiltersOnlyByPaymentDateRange_AndIgnoresAccountDate()
    {
        await using var db = await CreateContextAsync();

        var marchPayment = CreatePayment(new DateTime(2026, 3, 31), 5_000m, "Платеж до периода");
        marchPayment.AccountDate = new DateTime(2026, 4, 10);

        var aprilPayment = CreatePayment(new DateTime(2026, 4, 15), 7_500m, "Платеж внутри периода");
        aprilPayment.AccountDate = new DateTime(2025, 12, 20);

        var mayPayment = CreatePayment(new DateTime(2026, 5, 1), 9_000m, "Платеж после периода");
        mayPayment.AccountDate = new DateTime(2025, 8, 13);

        db.Payments.AddRange(marchPayment, aprilPayment, mayPayment);
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 30),
            null,
            null,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        var paymentIds = payments.Select(payment => payment.Id).ToList();
        Assert.Equal(new[] { aprilPayment.Id }, paymentIds);
    }

    [Fact]
    public async Task ApplyPaymentFilters_FiltersOnlyByPaymentDateRange_AndIgnoresAccountDate()
    {
        await using var db = await CreateContextAsync();

        var marchPayment = CreatePayment(new DateTime(2026, 3, 31), 5_000m, "Платеж до периода");
        marchPayment.AccountDate = new DateTime(2026, 4, 10);

        var aprilPayment = CreatePayment(new DateTime(2026, 4, 15), 7_500m, "Платеж внутри периода");
        aprilPayment.AccountDate = new DateTime(2025, 12, 20);

        var mayPayment = CreatePayment(new DateTime(2026, 5, 1), 9_000m, "Платеж после периода");
        mayPayment.AccountDate = new DateTime(2025, 8, 13);

        db.Payments.AddRange(marchPayment, aprilPayment, mayPayment);
        await db.SaveChangesAsync();

        var paymentIds = await db.Payments
            .AsNoTracking()
            .ApplyPaymentFilters(
                new DateTime(2026, 4, 1),
                new DateTime(2026, 4, 30),
                null,
                null,
                null)
            .OrderBy(payment => payment.Date)
            .Select(payment => payment.Id)
            .ToListAsync();

        Assert.Equal(new[] { aprilPayment.Id }, paymentIds);
    }

    [Fact]
    public async Task Get_WithCaseId_IncludesPaymentsLinkedThroughInvoiceCases()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Клиент по делу",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело с привязкой через счет",
        };

        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        var invoicePayment = CreatePayment(new DateTime(2026, 4, 8), 18_000m, "Счет №30");
        invoicePayment.Client = client;
        invoicePayment.DocumentKind = DocumentKind.Invoice;
        invoicePayment.Invoice = invoice;
        invoicePayment.InvoiceId = invoice.Id;
        invoicePayment.Account = invoice.Number;
        invoicePayment.AccountDate = invoice.Date;

        var linkedPayment = CreatePayment(new DateTime(2026, 4, 9), 18_000m, "Рассрочка 1/3");
        linkedPayment.Client = client;
        linkedPayment.Invoice = invoice;
        linkedPayment.InvoiceId = invoice.Id;
        linkedPayment.ClientCaseId = null;
        linkedPayment.DocumentKind = DocumentKind.Payment;

        db.Payments.AddRange(invoicePayment, linkedPayment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, new NoopFormRequirementsService());

        var actionResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: client.Id,
            caseId: clientCase.Id,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var payments = Assert.IsAssignableFrom<List<Payment>>(ok.Value);

        Assert.Collection(
            payments,
            payment => Assert.Equal(linkedPayment.Id, payment.Id));
    }

    [Fact]
    public async Task PaymentsV2_ExcludesParentInvoice_WhenInstallmentScheduleExists()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
        };

        var invoice = new Invoice
        {
            Number = "31",
            Date = new DateTime(2026, 4, 9),
        };

        var parentPayment = CreatePayment(new DateTime(2026, 4, 9), 60_000m, "Счет с рассрочкой");
        parentPayment.Client = client;
        parentPayment.DocumentKind = DocumentKind.Invoice;
        parentPayment.Invoice = invoice;
        parentPayment.InvoiceId = invoice.Id;
        parentPayment.Account = invoice.Number;
        parentPayment.AccountDate = invoice.Date;

        var installmentOne = CreatePayment(new DateTime(2026, 4, 10), 20_000m, "Платеж 1/3");
        installmentOne.Client = client;
        installmentOne.Invoice = invoice;
        installmentOne.InvoiceId = invoice.Id;
        installmentOne.SystemNotes = $"{InstallmentPaymentMarker} 1";

        var installmentTwo = CreatePayment(new DateTime(2026, 5, 10), 20_000m, "Платеж 2/3");
        installmentTwo.Client = client;
        installmentTwo.Invoice = invoice;
        installmentTwo.InvoiceId = invoice.Id;
        installmentTwo.SystemNotes = $"{InstallmentPaymentMarker} 2";

        db.Payments.AddRange(parentPayment, installmentOne, installmentTwo);
        await db.SaveChangesAsync();

        var controller = new PaymentsV2Controller(db);

        var actionResult = await controller.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 5, 31),
            clientId: client.Id,
            caseId: null,
            excludeInstallmentParents: true,
            clientIds: null,
            categoryTokens: null,
            type: null,
            status: null,
            minAmount: null,
            maxAmount: null,
            search: null,
            sortBy: "date",
            sortDir: "asc",
            page: 1,
            pageSize: 50,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<PaymentsPageResponse>(ok.Value);

        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Items.Count);
        Assert.DoesNotContain(response.Items, payment => payment.Id == parentPayment.Id);
        Assert.Equal(40_000m, response.Summary.Income);
        Assert.Equal(2, response.Summary.IncomeCount);
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

    private static Payment CreatePayment(DateTime date, decimal amount, string description)
    {
        return new Payment
        {
            Amount = amount,
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = description,
            DocumentKind = DocumentKind.Payment,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}

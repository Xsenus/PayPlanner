using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using System.Security.Claims;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoicesControllerListTests
{
    [Fact]
    public async Task Get_ReturnsSingleInvoiceRow_WhenInvoiceHasInstallmentPayments()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInstallmentInvoiceAggregateAsync(db);
        var controller = CreateController(db);

        var result = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = ExtractInvoiceItems(ok);
        var invoice = Assert.Single(items);

        Assert.Equal(aggregate.InvoicePaymentId, invoice.Id);
        Assert.Equal(DocumentKind.Invoice, invoice.DocumentKind);
        Assert.Equal("25", invoice.Number);
    }

    [Fact]
    public async Task Get_ReturnsFallbackRepresentative_WhenLegacyInvoiceHasNoInvoiceDocument()
    {
        await using var db = await CreateContextAsync();
        var legacyRepresentativeId = await CreateLegacyInvoiceAggregateWithoutInvoiceDocumentAsync(db);
        var controller = CreateController(db);

        var result = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = ExtractInvoiceItems(ok);
        var invoice = Assert.Single(items);

        Assert.Equal(legacyRepresentativeId, invoice.Id);
        Assert.Equal("LEG-7", invoice.Number);
    }

    [Fact]
    public async Task Get_FiltersByContractId_UsingCanonicalAndLegacyLinks()
    {
        await using var db = await CreateContextAsync();
        var contract = await CreateContractAsync(db, "CTR-LIST-1");

        var canonicalInvoice = await CreateInvoiceDocumentAsync(db, "INV-CAN-1", new DateTime(2026, 4, 8), 15_000m);
        canonicalInvoice.ContractId = contract.Id;

        var legacyInvoice = await CreateInvoiceDocumentAsync(db, "INV-LEG-1", new DateTime(2026, 4, 9), 16_000m);
        var outsideInvoice = await CreateInvoiceDocumentAsync(db, "INV-OUT-1", new DateTime(2026, 4, 10), 17_000m);

        db.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = legacyInvoice.Id,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: null,
            clientIds: null,
            contractId: contract.Id,
            caseId: null,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = ExtractInvoiceItems(ok);

        Assert.Equal(new[] { "INV-CAN-1", "INV-LEG-1" }, items.Select(item => item.Number).ToArray());
        Assert.DoesNotContain(items, item => item.Id == outsideInvoice.Id);
    }

    [Fact]
    public async Task Get_FiltersExpenseInvoicesByClientRole()
    {
        await using var db = await CreateContextAsync();
        var supplier = await CreateClientAsync(db, "Supplier");
        var expenseClient = await CreateClientAsync(db, "Expense Client");
        var unrelated = await CreateClientAsync(db, "Unrelated");

        var supplierInvoice = await CreateInvoiceDocumentAsync(db, "EXP-SUP", new DateTime(2026, 4, 11), 5_000m);
        supplierInvoice.Type = PaymentType.Expense;
        supplierInvoice.ClientId = supplier.Id;
        supplierInvoice.ExpenseClientId = expenseClient.Id;

        var clientExpenseInvoice = await CreateInvoiceDocumentAsync(db, "EXP-CLIENT", new DateTime(2026, 4, 12), 7_000m);
        clientExpenseInvoice.Type = PaymentType.Expense;
        clientExpenseInvoice.ClientId = unrelated.Id;
        clientExpenseInvoice.ExpenseClientId = supplier.Id;

        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var supplierResult = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: supplier.Id,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Expense,
            sortBy: "number",
            sortDir: "asc",
            clientRole: "counterparty",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var supplierItems = ExtractInvoiceItems(Assert.IsType<OkObjectResult>(supplierResult));
        Assert.Collection(supplierItems, item => Assert.Equal("EXP-SUP", item.Number));

        var expenseClientResult = await controller.Get(
            from: null,
            to: null,
            status: null,
            clientId: supplier.Id,
            clientIds: null,
            contractId: null,
            caseId: null,
            responsibleId: null,
            search: null,
            type: PaymentType.Expense,
            sortBy: "number",
            sortDir: "asc",
            clientRole: "expenseClient",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var expenseClientItems = ExtractInvoiceItems(Assert.IsType<OkObjectResult>(expenseClientResult));
        Assert.Collection(expenseClientItems, item => Assert.Equal("EXP-CLIENT", item.Number));
    }

    private static List<InvoiceDto> ExtractInvoiceItems(OkObjectResult ok)
    {
        var itemsProperty = ok.Value?.GetType().GetProperty("items");
        Assert.NotNull(itemsProperty);

        var rawItems = itemsProperty!.GetValue(ok.Value);
        var items = Assert.IsAssignableFrom<IEnumerable<InvoiceDto>>(rawItems);
        return items.ToList();
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

    private static InvoicesController CreateController(PaymentContext db)
    {
        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Role, "admin"),
                    },
                    authenticationType: "Test")),
            },
        };

        return controller;
    }

    private static async Task<(int InvoicePaymentId, int InvoiceEntityId)> CreateInstallmentInvoiceAggregateAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 7),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoicePayment = CreatePayment(invoice.Date, 25_000m, "Счёт с рассрочкой");
        invoicePayment.DocumentKind = DocumentKind.Invoice;
        invoicePayment.InvoiceId = invoice.Id;
        invoicePayment.Account = invoice.Number;
        invoicePayment.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoice.Date, 4_100m, "Рассрочка по счёту №25 · платёж 1/2");
        firstInstallment.InvoiceId = invoice.Id;

        var secondInstallment = CreatePayment(invoice.Date.AddMonths(1), 4_100m, "Рассрочка по счёту №25 · платёж 2/2");
        secondInstallment.InvoiceId = invoice.Id;

        db.Payments.AddRange(invoicePayment, firstInstallment, secondInstallment);
        await db.SaveChangesAsync();

        return (invoicePayment.Id, invoice.Id);
    }

    private static async Task<Contract> CreateContractAsync(PaymentContext db, string number)
    {
        var contract = new Contract
        {
            Number = number,
            Date = new DateTime(2026, 4, 1),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        return contract;
    }

    private static async Task<Client> CreateClientAsync(PaymentContext db, string name)
    {
        var client = new Client
        {
            Name = name,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<int> CreateLegacyInvoiceAggregateWithoutInvoiceDocumentAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "LEG-7",
            Date = new DateTime(2025, 12, 29),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var firstPayment = CreatePayment(new DateTime(2025, 12, 29), 10_000m, "Старый счёт без отдельного документа");
        firstPayment.InvoiceId = invoice.Id;
        firstPayment.Account = invoice.Number;
        firstPayment.AccountDate = invoice.Date;

        var secondPayment = CreatePayment(new DateTime(2026, 1, 29), 10_000m, "Второй платёж по старому счёту");
        secondPayment.InvoiceId = invoice.Id;
        secondPayment.Account = invoice.Number;
        secondPayment.AccountDate = invoice.Date;

        db.Payments.AddRange(firstPayment, secondPayment);
        await db.SaveChangesAsync();

        return firstPayment.Id;
    }

    private static async Task<Payment> CreateInvoiceDocumentAsync(PaymentContext db, string number, DateTime date, decimal amount)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = CreatePayment(date, amount, $"Счет {number}");
        payment.DocumentKind = DocumentKind.Invoice;
        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date;

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
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
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;

        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;

        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;

        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;

        public Task SendEmailWithAttachmentAsync(
            string toEmail,
            string subject,
            string body,
            string attachmentFileName,
            byte[] attachmentBytes,
            string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}

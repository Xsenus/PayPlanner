using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class AccountsControllerTests
{
    [Fact]
    public async Task GetRegistry_ReturnsPartialStatus_WhenInvoiceHasPartialPaidAmount()
    {
        await using var db = await CreateContextAsync();
        await CreateInvoiceWithDirectPartialPaymentAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
            status: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("partial", row.Status);
        Assert.False(row.IsOverdue);
    }

    [Fact]
    public async Task GetRegistry_ReturnsOverdueStatus_WhenInstallmentInvoiceHasPaidProgress_AndOverdueTail()
    {
        await using var db = await CreateContextAsync();
        await CreateInstallmentInvoiceAggregateAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
            status: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("overdue", row.Status);
        Assert.True(row.IsOverdue);
        Assert.Equal(6, row.Payments.Count);
        Assert.DoesNotContain(row.Payments, payment => payment.DocumentKind == DocumentKind.Invoice);
    }

    [Fact]
    public async Task GetRegistry_KeepsFullInstallmentSchedule_WhenRegistryFilteredByInvoiceDate()
    {
        await using var db = await CreateContextAsync();
        await CreateInstallmentInvoiceAggregateAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: new DateTime(2026, 1, 1),
            to: new DateTime(2026, 1, 31),
            status: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("2", row.AccountNumber);
        Assert.Equal(6, row.Payments.Count);
        Assert.DoesNotContain(row.Payments, payment => payment.DocumentKind == DocumentKind.Invoice);
        Assert.Equal(new DateTime(2026, 6, 25), row.DueDate);
    }

    [Fact]
    public async Task GetRegistry_KeepsLegacySiblingPayment_WhenPaymentHasNoInvoiceId_ButSharesAccountAndDate()
    {
        await using var db = await CreateContextAsync();
        await CreateInvoiceWithLegacySiblingPaymentAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("24", row.AccountNumber);
        Assert.Equal(2, row.Payments.Count);
        Assert.Equal(new[] { 3_000m, 10_000m }, row.Payments.Select(payment => payment.Amount).OrderBy(value => value).ToArray());
        Assert.Equal(new DateTime(2026, 4, 4), row.DueDate);
    }

    [Fact]
    public async Task GetRegistry_UsesInvoiceNumberAsTieBreaker_WhenDatesMatch()
    {
        await using var db = await CreateContextAsync();
        await CreateInvoiceDocumentAsync(db, "31", new DateTime(2026, 4, 8), 10_000m);
        await CreateInvoiceDocumentAsync(db, "30", new DateTime(2026, 4, 8), 18_000m);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: "date",
            sortDir: "desc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);

        Assert.Equal(new[] { "31", "30" }, payload.Items.Select(item => item.AccountNumber).ToArray());
    }

    [Fact]
    public async Task GetRegistry_SortsInvoiceNumbersNaturally_WhenSortingByNumber()
    {
        await using var db = await CreateContextAsync();
        await CreateInvoiceDocumentAsync(db, "10", new DateTime(2026, 4, 8), 10_000m);
        await CreateInvoiceDocumentAsync(db, "2", new DateTime(2026, 4, 8), 18_000m);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: "number",
            sortDir: "asc",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);

        Assert.Equal(new[] { "2", "10" }, payload.Items.Select(item => item.AccountNumber).ToArray());
    }

    [Fact]
    public async Task GetRegistry_IncludesInvoiceAndActLineItemsInInvoiceSnapshot()
    {
        await using var db = await CreateContextAsync();

        var client = new Client
        {
            Name = "Line Item Client",
        };

        var invoice = new Invoice
        {
            Number = "50",
            Date = new DateTime(2026, 4, 10),
        };

        db.AddRange(client, invoice);
        await db.SaveChangesAsync();

        var representativePayment = CreatePayment(invoice.Date, 12_000m, "Representative payment for invoice 50");
        representativePayment.DocumentKind = DocumentKind.Invoice;
        representativePayment.InvoiceId = invoice.Id;
        representativePayment.Account = invoice.Number;
        representativePayment.AccountDate = invoice.Date;
        representativePayment.Client = client;
        representativePayment.CounterpartyName = client.Name;
        representativePayment.InvoiceLineItems.Add(new InvoiceLineItem
        {
            Name = "Document preparation",
            Quantity = 2m,
            Unit = "service",
            Price = 6_000m,
            SortOrder = 0,
        });

        db.Payments.Add(representativePayment);
        await db.SaveChangesAsync();

        var storedInvoiceLineItemId = await db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == representativePayment.Id)
            .Select(item => item.Id)
            .SingleAsync();

        db.Acts.Add(new Act
        {
            Number = "ACT-50",
            Date = new DateTime(2026, 4, 11),
            Amount = 6_000m,
            CreationMode = ActCreationMode.Partial,
            InvoiceId = representativePayment.Id,
            InvoiceNumber = invoice.Number,
            ClientId = client.Id,
            Status = ActStatus.Created,
            LineItems =
            {
                new ActLineItem
                {
                    SortOrder = 0,
                    InvoiceLineItemId = storedInvoiceLineItemId,
                    SourceKey = $"db:{storedInvoiceLineItemId}",
                    SourceSortOrder = 0,
                    Name = "Document preparation",
                    Quantity = 1m,
                    Unit = "service",
                    Price = 6_000m,
                },
            },
        });
        await db.SaveChangesAsync();

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            search: null,
            type: PaymentType.Income,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);
        var invoiceSnapshot = Assert.IsType<InvoiceDto>(row.Invoice);
        var invoiceLineItem = Assert.Single(invoiceSnapshot.LineItems);
        var linkedAct = Assert.Single(invoiceSnapshot.Acts);
        var actLineItem = Assert.Single(linkedAct.LineItems);

        Assert.Equal("Document preparation", invoiceLineItem.Name);
        Assert.Equal(2m, invoiceLineItem.Quantity);
        Assert.Equal($"db:{storedInvoiceLineItemId}", invoiceLineItem.SourceKey);
        Assert.Equal("ACT-50", linkedAct.Number);
        Assert.Equal($"db:{storedInvoiceLineItemId}", actLineItem.SourceKey);
        Assert.Equal(1m, actLineItem.Quantity);
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

    private static async Task CreateInvoiceWithDirectPartialPaymentAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "INV-100",
            Date = new DateTime(2026, 1, 19),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = CreatePayment(new DateTime(2026, 1, 19), 86_000m, "Счёт с частичной оплатой");
        payment.DocumentKind = DocumentKind.Invoice;
        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date;
        payment.Status = PaymentStatus.Pending;
        payment.PaidAmount = 15_000m;
        payment.PaidDate = new DateTime(2026, 2, 1);
        payment.LastPaymentDate = payment.PaidDate;

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
    }

    private static async Task CreateInstallmentInvoiceAggregateAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "2",
            Date = new DateTime(2026, 1, 19),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoicePayment = CreatePayment(invoice.Date, 86_000m, "Счёт №2 от 19 января 2026 г.");
        invoicePayment.DocumentKind = DocumentKind.Invoice;
        invoicePayment.InvoiceId = invoice.Id;
        invoicePayment.Account = invoice.Number;
        invoicePayment.AccountDate = invoice.Date;
        invoicePayment.Status = PaymentStatus.Pending;

        var firstPaid = CreateLinkedPayment(invoice, new DateTime(2026, 2, 1), 15_000m, PaymentStatus.Completed);
        firstPaid.PaidAmount = firstPaid.Amount;
        firstPaid.PaidDate = firstPaid.Date;
        firstPaid.LastPaymentDate = firstPaid.Date;
        firstPaid.IsPaid = true;
        firstPaid.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 1";

        var secondPaid = CreateLinkedPayment(invoice, new DateTime(2026, 3, 4), 15_000m, PaymentStatus.Completed);
        secondPaid.PaidAmount = secondPaid.Amount;
        secondPaid.PaidDate = secondPaid.Date;
        secondPaid.LastPaymentDate = secondPaid.Date;
        secondPaid.IsPaid = true;
        secondPaid.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 2";

        var overdue = CreateLinkedPayment(invoice, new DateTime(2026, 3, 25), 15_000m, PaymentStatus.Overdue);
        overdue.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 3";
        var fourth = CreateLinkedPayment(invoice, new DateTime(2026, 4, 27), 15_000m, PaymentStatus.Pending);
        fourth.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 4";
        var fifth = CreateLinkedPayment(invoice, new DateTime(2026, 5, 25), 15_000m, PaymentStatus.Pending);
        fifth.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 5";
        var sixth = CreateLinkedPayment(invoice, new DateTime(2026, 6, 25), 11_000m, PaymentStatus.Pending);
        sixth.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} 6";

        db.Payments.AddRange(invoicePayment, firstPaid, secondPaid, overdue, fourth, fifth, sixth);
        await db.SaveChangesAsync();
    }

    private static async Task CreateInvoiceWithLegacySiblingPaymentAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Test Client",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Test Case",
        };

        var invoice = new Invoice
        {
            Number = "24",
            Date = new DateTime(2026, 4, 2),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = CreatePayment(invoice.Date, 10_000m, "Representative payment for invoice 24");
        representativePayment.DocumentKind = DocumentKind.Invoice;
        representativePayment.InvoiceId = invoice.Id;
        representativePayment.Account = invoice.Number;
        representativePayment.AccountDate = invoice.Date;
        representativePayment.Client = client;
        representativePayment.ClientCase = clientCase;
        representativePayment.CounterpartyName = client.Name;
        representativePayment.Status = PaymentStatus.Completed;
        representativePayment.IsPaid = true;
        representativePayment.PaidAmount = representativePayment.Amount;
        representativePayment.PaidDate = new DateTime(2026, 4, 3);
        representativePayment.LastPaymentDate = representativePayment.PaidDate;

        var legacySiblingPayment = CreatePayment(new DateTime(2026, 4, 4), 3_000m, "Legacy sibling payment for invoice 24");
        legacySiblingPayment.InvoiceId = null;
        legacySiblingPayment.Account = invoice.Number;
        legacySiblingPayment.AccountDate = invoice.Date;
        legacySiblingPayment.Client = client;
        legacySiblingPayment.ClientCase = clientCase;
        legacySiblingPayment.CounterpartyName = client.Name;
        legacySiblingPayment.Status = PaymentStatus.Completed;
        legacySiblingPayment.IsPaid = true;
        legacySiblingPayment.PaidAmount = legacySiblingPayment.Amount;
        legacySiblingPayment.PaidDate = legacySiblingPayment.Date;
        legacySiblingPayment.LastPaymentDate = legacySiblingPayment.Date;

        db.Payments.AddRange(representativePayment, legacySiblingPayment);
        await db.SaveChangesAsync();
    }

    private static async Task CreateInvoiceDocumentAsync(PaymentContext db, string number, DateTime date, decimal amount)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = CreatePayment(date, amount, $"Invoice {number}");
        payment.DocumentKind = DocumentKind.Invoice;
        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date;
        payment.Status = PaymentStatus.Pending;

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
    }

    private static Payment CreateLinkedPayment(Invoice invoice, DateTime date, decimal amount, PaymentStatus status)
    {
        var payment = CreatePayment(date, amount, $"Счёт №{invoice.Number} от {invoice.Date:dd MMMM yyyy} г.");
        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date;
        payment.Status = status;
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
}

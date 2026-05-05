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
        Assert.Equal(86_000m, payload.Summary.Total.Amount);
        Assert.Equal(71_000m, payload.Summary.Pending.Amount);
        Assert.Equal(1, payload.Summary.Pending.Count);
        Assert.Equal(0m, payload.Summary.Overdue.Amount);
        Assert.NotNull(row.Invoice);
        Assert.Equal(15_000m, row.Invoice!.PaidAmount);
        Assert.Equal(71_000m, row.Invoice.OutstandingAmount);
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
        Assert.Equal(86_000m, payload.Summary.Total.Amount);
        Assert.Equal(56_000m, payload.Summary.Pending.Amount);
        Assert.Equal(56_000m, payload.Summary.Overdue.Amount);
        Assert.NotNull(row.Invoice);
        Assert.Equal(30_000m, row.Invoice!.PaidAmount);
        Assert.Equal(56_000m, row.Invoice.OutstandingAmount);
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
    public async Task GetRegistry_KeepsConvertedLegacyRepresentative_WhenGeneratedInstallmentsExceedIt()
    {
        await using var db = await CreateContextAsync();
        await CreateConvertedLegacyInstallmentAggregateAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
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

        Assert.Equal("122", row.AccountNumber);
        Assert.Equal("paid", row.Status);
        Assert.Equal(212_500m, row.TotalAmount);
        Assert.Equal(new DateTime(2025, 10, 7), row.DueDate);
        Assert.Equal(4, row.Payments.Count);
        Assert.Contains(row.Payments, payment => payment.DocumentKind == DocumentKind.Invoice && payment.Amount == 62_500m);
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
        var payment = Assert.Single(row.Payments);
        Assert.Equal(3_000m, payment.Amount);
        Assert.Equal(new DateTime(2026, 4, 4), row.DueDate);
    }

    [Fact]
    public async Task GetRegistry_UsesFullLegacyInstallmentAggregate_WhenChildrenWereSavedAsInvoiceRows()
    {
        await using var db = await CreateContextAsync();
        await CreateLegacyBrokenInstallmentAggregateAsync(db);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
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

        Assert.Equal("155", row.AccountNumber);
        Assert.Equal("partial", row.Status);
        Assert.Equal(205_000m, row.TotalAmount);
        Assert.Equal(new DateTime(2026, 1, 30), row.DueDate);
        Assert.Equal(new DateTime(2025, 12, 25), row.PaidDate);
        Assert.Equal(3, row.Payments.Count);
        Assert.Equal(new[] { 18_000m, 18_000m, 18_000m }, row.Payments.Select(payment => payment.Amount).OrderBy(amount => amount).ToArray());
        Assert.DoesNotContain(row.Payments, payment => payment.Amount == 205_000m);
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

    [Fact]
    public async Task GetRegistry_FiltersExpenseInvoicesByClientRole()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Supplier" };
        var firstClient = new Client { Name = "First Client" };
        var secondClient = new Client { Name = "Second Client" };
        db.Clients.AddRange(supplier, firstClient, secondClient);
        await db.SaveChangesAsync();

        await CreateExpenseInvoiceAsync(db, "EXP-1", supplier, firstClient, 5_000m);
        await CreateExpenseInvoiceAsync(db, "EXP-2", supplier, secondClient, 7_000m);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
            status: null,
            search: null,
            type: PaymentType.Expense,
            sortBy: null,
            sortDir: null,
            clientId: firstClient.Id,
            clientRole: "expenseClient",
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("EXP-1", row.AccountNumber);
        Assert.Equal(firstClient.Id, row.Invoice?.ExpenseClientId);
        Assert.Equal(supplier.Id, row.Invoice?.ClientId);
    }

    [Fact]
    public async Task GetRegistry_PayablesOnlyReturnsOpenExpenseInvoices()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Supplier" };
        db.Clients.Add(supplier);
        await db.SaveChangesAsync();

        await CreateExpenseInvoiceAsync(db, "OPEN", supplier, null, 5_000m, PaymentStatus.Pending);
        await CreateExpenseInvoiceAsync(db, "PAID", supplier, null, 7_000m, PaymentStatus.Completed, paidAmount: 7_000m);
        await CreateInvoiceDocumentAsync(db, "INCOME", new DateTime(2026, 4, 8), 9_000m);

        var controller = new AccountsController(db);

        var result = await controller.GetRegistry(
            from: null,
            to: null,
            status: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            payablesOnly: true,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AccountsPageResponse>(ok.Value);
        var row = Assert.Single(payload.Items);

        Assert.Equal("OPEN", row.AccountNumber);
        Assert.Equal(PaymentType.Expense, row.Type);
        Assert.NotEqual("paid", row.Status);
    }

    [Fact]
    public async Task GetPayablesDashboard_GroupsOpenExpenseDebtBySupplierAndDueBucket()
    {
        await using var db = await CreateContextAsync();

        var today = DateTime.UtcNow.Date;
        var supplier = new Client { Name = "Geo Supplier" };
        var utilitySupplier = new Client { Name = "Utility Supplier" };
        var client = new Client { Name = "Matter Client" };
        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Matter case",
            Status = ClientCaseStatus.Open,
        };

        db.Clients.AddRange(supplier, utilitySupplier, client);
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();

        var overdue = CreatePayment(today.AddDays(-1), 5_000m, "Overdue supplier debt");
        overdue.Type = PaymentType.Expense;
        overdue.Status = PaymentStatus.Overdue;
        overdue.PaidAmount = 1_000m;
        overdue.ClientId = supplier.Id;
        overdue.ExpenseClientId = client.Id;
        overdue.ClientCaseId = clientCase.Id;
        overdue.CounterpartyName = supplier.Name;

        var upcoming = CreatePayment(today.AddDays(5), 3_000m, "Upcoming utility debt");
        upcoming.Type = PaymentType.Expense;
        upcoming.Status = PaymentStatus.Pending;
        upcoming.ClientId = utilitySupplier.Id;
        upcoming.CounterpartyName = utilitySupplier.Name;

        var paid = CreatePayment(today, 2_000m, "Paid expense");
        paid.Type = PaymentType.Expense;
        paid.Status = PaymentStatus.Completed;
        paid.PaidAmount = 2_000m;
        paid.ClientId = supplier.Id;
        paid.CounterpartyName = supplier.Name;

        db.Payments.AddRange(overdue, upcoming, paid);
        await db.SaveChangesAsync();

        var controller = new AccountsController(db);

        var result = await controller.GetPayablesDashboard(
            from: null,
            to: null,
            search: null,
            clientId: null,
            clientIds: null,
            clientRole: null,
            ct: CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result.Result);
        var dashboard = Assert.IsType<PayablesDashboardDto>(response.Value);

        Assert.Equal(7_000m, dashboard.TotalOutstanding);
        Assert.Equal(4_000m, dashboard.OverdueOutstanding);
        Assert.Equal(3_000m, dashboard.UpcomingOutstanding);
        Assert.Equal(2, dashboard.PaymentCount);
        Assert.Equal(2, dashboard.SupplierCount);

        var overdueBucket = Assert.Single(dashboard.DueBuckets, bucket => bucket.Key == "overdue");
        Assert.Equal(4_000m, overdueBucket.Amount);
        Assert.Equal(1, overdueBucket.Count);

        var weekBucket = Assert.Single(dashboard.DueBuckets, bucket => bucket.Key == "week");
        Assert.Equal(3_000m, weekBucket.Amount);
        Assert.Equal(1, weekBucket.Count);

        var supplierGroup = Assert.Single(dashboard.Suppliers, group => group.SupplierId == supplier.Id);
        Assert.Equal(4_000m, supplierGroup.TotalOutstanding);
        Assert.Equal(4_000m, supplierGroup.OverdueOutstanding);
        Assert.Equal(1, supplierGroup.CaseCount);
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

    private static async Task CreateConvertedLegacyInstallmentAggregateAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Converted Legacy Client",
        };

        var invoice = new Invoice
        {
            Number = "122",
            Date = new DateTime(2025, 7, 3),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = CreatePayment(new DateTime(2025, 10, 7), 62_500m, "Invoice 122 final legacy installment");
        representativePayment.DocumentKind = DocumentKind.Invoice;
        representativePayment.InvoiceId = invoice.Id;
        representativePayment.Account = invoice.Number;
        representativePayment.AccountDate = invoice.Date;
        representativePayment.Client = client;
        representativePayment.CounterpartyName = client.Name;
        representativePayment.Status = PaymentStatus.Completed;
        representativePayment.PaidAmount = representativePayment.Amount;
        representativePayment.PaidDate = representativePayment.Date;
        representativePayment.LastPaymentDate = representativePayment.Date;
        representativePayment.IsPaid = true;

        var firstInstallment = CreateLinkedPayment(invoice, new DateTime(2025, 7, 8), 50_000m, PaymentStatus.Completed);
        var secondInstallment = CreateLinkedPayment(invoice, new DateTime(2025, 8, 8), 50_000m, PaymentStatus.Completed);
        var thirdInstallment = CreateLinkedPayment(invoice, new DateTime(2025, 9, 9), 50_000m, PaymentStatus.Completed);

        foreach (var installment in new[] { firstInstallment, secondInstallment, thirdInstallment })
        {
            installment.Client = client;
            installment.CounterpartyName = client.Name;
            installment.PaidAmount = installment.Amount;
            installment.PaidDate = installment.Date;
            installment.LastPaymentDate = installment.Date;
            installment.IsPaid = true;
            installment.SystemNotes = $"{InvoiceManagedPaymentRules.InstallmentPaymentMarker} {{\"sequence\":1}}";
        }

        db.Payments.AddRange(representativePayment, firstInstallment, secondInstallment, thirdInstallment);
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

    private static async Task CreateLegacyBrokenInstallmentAggregateAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Broken Legacy Client",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Broken Legacy Case",
        };

        var invoice = new Invoice
        {
            Number = "155",
            Date = new DateTime(2025, 10, 24),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var representativePayment = CreatePayment(new DateTime(2026, 8, 25), 205_000m, "Representative payment for invoice 155");
        representativePayment.DocumentKind = DocumentKind.Invoice;
        representativePayment.InvoiceId = invoice.Id;
        representativePayment.Account = invoice.Number;
        representativePayment.AccountDate = invoice.Date;
        representativePayment.Client = client;
        representativePayment.ClientCase = clientCase;
        representativePayment.CounterpartyName = client.Name;
        representativePayment.Status = PaymentStatus.Pending;
        representativePayment.PlannedDate = invoice.Date;

        var firstInstallment = CreateLinkedPayment(invoice, new DateTime(2025, 11, 27), 18_000m, PaymentStatus.Completed);
        firstInstallment.DocumentKind = DocumentKind.Invoice;
        firstInstallment.Client = client;
        firstInstallment.ClientCase = clientCase;
        firstInstallment.CounterpartyName = client.Name;
        firstInstallment.PaidAmount = firstInstallment.Amount;
        firstInstallment.PaidDate = firstInstallment.Date;
        firstInstallment.LastPaymentDate = firstInstallment.Date;
        firstInstallment.IsPaid = true;

        var secondInstallment = CreateLinkedPayment(invoice, new DateTime(2025, 12, 25), 18_000m, PaymentStatus.Completed);
        secondInstallment.DocumentKind = DocumentKind.Payment;
        secondInstallment.Client = client;
        secondInstallment.ClientCase = clientCase;
        secondInstallment.CounterpartyName = client.Name;
        secondInstallment.PaidAmount = secondInstallment.Amount;
        secondInstallment.PaidDate = secondInstallment.Date;
        secondInstallment.LastPaymentDate = secondInstallment.Date;
        secondInstallment.IsPaid = true;

        var thirdInstallment = CreateLinkedPayment(invoice, new DateTime(2026, 1, 30), 18_000m, PaymentStatus.Pending);
        thirdInstallment.DocumentKind = DocumentKind.Invoice;
        thirdInstallment.Client = client;
        thirdInstallment.ClientCase = clientCase;
        thirdInstallment.CounterpartyName = client.Name;

        db.Payments.AddRange(representativePayment, firstInstallment, secondInstallment, thirdInstallment);
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

    private static async Task CreateExpenseInvoiceAsync(
        PaymentContext db,
        string number,
        Client supplier,
        Client? expenseClient,
        decimal amount,
        PaymentStatus status = PaymentStatus.Pending,
        decimal paidAmount = 0m)
    {
        var invoice = new Invoice
        {
            Number = number,
            Date = new DateTime(2026, 4, 8),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = CreatePayment(invoice.Date, amount, $"Expense invoice {number}");
        payment.DocumentKind = DocumentKind.Invoice;
        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date;
        payment.Type = PaymentType.Expense;
        payment.Status = status;
        payment.PaidAmount = paidAmount;
        payment.IsPaid = status == PaymentStatus.Completed;
        payment.PaidDate = status == PaymentStatus.Completed ? invoice.Date : null;
        payment.LastPaymentDate = payment.PaidDate;
        payment.Client = supplier;
        payment.CounterpartyName = supplier.Name;
        if (expenseClient is not null)
        {
            payment.ExpenseClient = expenseClient;
        }

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

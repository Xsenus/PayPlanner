using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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

public class InvoicePaymentConsistencyTests
{
    [Fact]
    public async Task InvoiceEndpoints_ReturnSameRepresentativePayment_ForClientInvoiceWithSingleOperation()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateSingleOperationInvoiceAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = new PaymentsController(db, new NoopFormRequirementsService());

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("24", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
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

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("24", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Collection(
            registryRow.Payments,
            payment => Assert.Equal(aggregate.InvoicePaymentId, payment.Id));

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Collection(
            paymentItems,
            payment => Assert.Equal(aggregate.InvoicePaymentId, payment.Id));
    }

    [Fact]
    public async Task InvoiceEndpoints_KeepAllSiblingOperations_ForClientInvoiceWithSeveralPayments()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInvoiceWithSiblingPaymentAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = new PaymentsController(db, new NoopFormRequirementsService());

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("25", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
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

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("25", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            registryRow.Payments.Select(payment => payment.Id).OrderBy(id => id).ToArray());

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task PaymentUpdate_NormalizesInvoiceLink_AndKeepsInvoiceReadsConsistent()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateInvoiceWithDetachedSiblingPaymentAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = CreatePaymentsController(db);

        var updateModel = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = "Test Client",
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Supplement for invoice 26",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            ClientId = aggregate.ClientId,
            ClientCaseId = aggregate.ClientCaseId,
            InvoiceId = aggregate.InvoiceEntityId,
            Account = " OLD-26 ",
            AccountDate = new DateTime(2025, 1, 15),
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var updateResult = await paymentsController.Update(
            aggregate.SiblingPaymentId,
            updateModel,
            CancellationToken.None);

        var updateOk = Assert.IsType<OkObjectResult>(updateResult);
        var updatedPayment = Assert.IsType<Payment>(updateOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, updatedPayment.InvoiceId);
        Assert.Equal("26", updatedPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 2), updatedPayment.AccountDate);
        Assert.Equal("OLD-26", updatedPayment.LegacyAccountNumber);
        Assert.Equal(new DateTime(2025, 1, 15), updatedPayment.LegacyAccountDate);

        var paymentByIdResult = await paymentsController.GetById(aggregate.SiblingPaymentId, CancellationToken.None);
        var paymentByIdOk = Assert.IsType<OkObjectResult>(paymentByIdResult);
        var paymentById = Assert.IsType<Payment>(paymentByIdOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, paymentById.InvoiceId);
        Assert.Equal("26", paymentById.Account);
        Assert.Equal(new DateTime(2026, 4, 2), paymentById.AccountDate);
        Assert.Equal("OLD-26", paymentById.LegacyAccountNumber);

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("26", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
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

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("26", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(aggregate.InvoiceEntityId, registryRow.Invoice?.InvoiceEntityId);
        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            registryRow.Payments.Select(payment => payment.Id).OrderBy(id => id).ToArray());

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, aggregate.SiblingPaymentId },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task PaymentCreate_NormalizesInvoiceLink_AndKeepsInvoiceReadsConsistent()
    {
        await using var db = await CreateContextAsync();
        var aggregate = await CreateSingleOperationInvoiceAsync(db);

        var invoicesController = CreateInvoicesController(db);
        var accountsController = new AccountsController(db);
        var paymentsController = CreatePaymentsController(db);

        var createModel = new Payment
        {
            Amount = 3_000m,
            CounterpartyName = "Supplement payment",
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 3_000m,
            Description = "Supplement for invoice 24",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            ClientId = aggregate.ClientId,
            ClientCaseId = aggregate.ClientCaseId,
            InvoiceId = aggregate.InvoiceEntityId,
            Account = " LEGACY-24 ",
            AccountDate = new DateTime(2025, 12, 31),
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var createResult = await paymentsController.Create(createModel, CancellationToken.None);

        var createCreated = Assert.IsType<CreatedResult>(createResult);
        var createdPayment = Assert.IsType<Payment>(createCreated.Value);

        Assert.NotEqual(aggregate.InvoicePaymentId, createdPayment.Id);
        Assert.Equal(aggregate.InvoiceEntityId, createdPayment.InvoiceId);
        Assert.Equal("24", createdPayment.Account);
        Assert.Equal(new DateTime(2026, 4, 2), createdPayment.AccountDate);
        Assert.Equal("LEGACY-24", createdPayment.LegacyAccountNumber);
        Assert.Equal(new DateTime(2025, 12, 31), createdPayment.LegacyAccountDate);

        var paymentByIdResult = await paymentsController.GetById(createdPayment.Id, CancellationToken.None);
        var paymentByIdOk = Assert.IsType<OkObjectResult>(paymentByIdResult);
        var paymentById = Assert.IsType<Payment>(paymentByIdOk.Value);

        Assert.Equal(aggregate.InvoiceEntityId, paymentById.InvoiceId);
        Assert.Equal("24", paymentById.Account);
        Assert.Equal(new DateTime(2026, 4, 2), paymentById.AccountDate);
        Assert.Equal("LEGACY-24", paymentById.LegacyAccountNumber);

        var invoicesResult = await invoicesController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            status: null,
            clientId: aggregate.ClientId,
            clientIds: null,
            contractId: null,
            caseId: aggregate.ClientCaseId,
            responsibleId: null,
            search: null,
            type: null,
            sortBy: null,
            sortDir: null,
            page: 1,
            pageSize: 20,
            ct: CancellationToken.None);

        var invoicesOk = Assert.IsType<OkObjectResult>(invoicesResult);
        var invoiceItems = ExtractInvoiceItems(invoicesOk);
        var invoiceRow = Assert.Single(invoiceItems);

        Assert.Equal(aggregate.InvoicePaymentId, invoiceRow.Id);
        Assert.Equal(aggregate.InvoiceEntityId, invoiceRow.InvoiceEntityId);
        Assert.Equal("24", invoiceRow.Number);

        var accountsResult = await accountsController.GetRegistry(
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

        var accountsOk = Assert.IsType<OkObjectResult>(accountsResult.Result);
        var accountsPayload = Assert.IsType<AccountsPageResponse>(accountsOk.Value);
        var registryRow = Assert.Single(accountsPayload.Items);

        Assert.Equal("24", registryRow.AccountNumber);
        Assert.Equal(aggregate.InvoicePaymentId, registryRow.Invoice?.Id);
        Assert.Equal(aggregate.InvoiceEntityId, registryRow.Invoice?.InvoiceEntityId);
        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, createdPayment.Id },
            registryRow.Payments.Select(payment => payment.Id).OrderBy(id => id).ToArray());

        var paymentsResult = await paymentsController.Get(
            from: new DateTime(2026, 4, 1),
            to: new DateTime(2026, 4, 30),
            clientId: aggregate.ClientId,
            caseId: aggregate.ClientCaseId,
            excludeInstallmentParents: false,
            ct: CancellationToken.None);

        var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResult);
        var paymentItems = Assert.IsAssignableFrom<List<Payment>>(paymentsOk.Value);

        Assert.Equal(
            new[] { aggregate.InvoicePaymentId, createdPayment.Id },
            paymentItems.Select(payment => payment.Id).OrderBy(id => id).ToArray());
    }

    private static List<InvoiceDto> ExtractInvoiceItems(OkObjectResult ok)
    {
        var itemsProperty = ok.Value?.GetType().GetProperty("items");
        Assert.NotNull(itemsProperty);

        var rawItems = itemsProperty!.GetValue(ok.Value);
        var items = Assert.IsAssignableFrom<IEnumerable<InvoiceDto>>(rawItems);
        return items.ToList();
    }

    private static async Task<(int InvoicePaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateSingleOperationInvoiceAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Байгузина Екатерина Евгеньевна",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ",
        };

        var invoice = new Invoice
        {
            Number = "24",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Оплата по счету №24",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.Add(representativePayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static async Task<(int InvoicePaymentId, int SiblingPaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateInvoiceWithSiblingPaymentAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Байгузина Екатерина Евгеньевна",
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "БАЙГУЗИНА - ОТМЕНА ПРОТОКОЛОВ ОБЩЕГО СОБРАНИЯ",
        };

        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Оплата по счету №25",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var siblingPayment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Доплата по счету №25",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            InvoiceId = null,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        db.Payments.AddRange(representativePayment, siblingPayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, siblingPayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static async Task<(int InvoicePaymentId, int SiblingPaymentId, int InvoiceEntityId, int ClientId, int ClientCaseId)> CreateInvoiceWithDetachedSiblingPaymentAsync(PaymentContext db)
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
            Number = "26",
            Date = new DateTime(2026, 4, 2),
        };

        var representativePayment = new Payment
        {
            Amount = 10_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 2),
            PlannedDate = new DateTime(2026, 4, 2),
            PaidDate = new DateTime(2026, 4, 3),
            LastPaymentDate = new DateTime(2026, 4, 3),
            PaidAmount = 10_000m,
            Description = "Invoice 26",
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            Invoice = invoice,
            Account = invoice.Number,
            AccountDate = invoice.Date,
        };

        var siblingPayment = new Payment
        {
            Amount = 5_000m,
            CounterpartyName = client.Name,
            Date = new DateTime(2026, 4, 4),
            PlannedDate = new DateTime(2026, 4, 4),
            PaidDate = new DateTime(2026, 4, 5),
            LastPaymentDate = new DateTime(2026, 4, 5),
            PaidAmount = 5_000m,
            Description = "Detached supplement",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Income,
            Client = client,
            ClientCase = clientCase,
            InvoiceId = null,
            Account = "OLD-26",
            AccountDate = new DateTime(2025, 1, 15),
        };

        db.Payments.AddRange(representativePayment, siblingPayment);
        await db.SaveChangesAsync();

        return (representativePayment.Id, siblingPayment.Id, invoice.Id, client.Id, clientCase.Id);
    }

    private static InvoicesController CreateInvoicesController(PaymentContext db)
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

    private static PaymentsController CreatePaymentsController(PaymentContext db)
    {
        var controller = new PaymentsController(db, new NoopFormRequirementsService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, "Test Admin"),
                        new Claim(ClaimTypes.Role, "admin"),
                    },
                    authenticationType: "Test")),
            },
        };

        return controller;
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

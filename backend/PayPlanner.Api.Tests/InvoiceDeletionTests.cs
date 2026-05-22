using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoiceDeletionTests
{
    [Fact]
    public async Task Delete_RemovesInvoiceDocument_AndAllLinkedInstallmentPayments()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var parentPaymentId = invoice.ParentPaymentId;
        var invoiceEntityId = invoice.InvoiceEntityId;
        var unrelatedPaymentId = invoice.UnrelatedPaymentId;

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());

        await controller.Delete(parentPaymentId, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Payments.Where(payment => payment.InvoiceId == invoiceEntityId).ToListAsync());
        Assert.Null(await db.Invoices.FindAsync(invoiceEntityId));
        Assert.NotNull(await db.Payments.FindAsync(unrelatedPaymentId));
    }

    [Fact]
    public async Task Delete_RemovesWholeInvoiceAggregate_WhenTargetIsInstallmentPayment()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var installmentPaymentId = invoice.InstallmentPaymentIds[0];
        var invoiceEntityId = invoice.InvoiceEntityId;
        var unrelatedPaymentId = invoice.UnrelatedPaymentId;

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());

        await controller.Delete(installmentPaymentId, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Null(await db.Payments.FindAsync(installmentPaymentId));
        Assert.Empty(await db.Payments.Where(payment => payment.InvoiceId == invoiceEntityId).ToListAsync());
        Assert.Null(await db.Invoices.FindAsync(invoiceEntityId));
        Assert.NotNull(await db.Payments.FindAsync(unrelatedPaymentId));
    }

    [Fact]
    public async Task Delete_DetachesLinkedAct_WhenInvoiceHasLinkedAct()
    {
        await using var db = await CreateContextAsync();
        var invoice = await CreateInvoiceAggregateAsync(db);
        var parentPaymentId = invoice.ParentPaymentId;
        var invoiceEntityId = invoice.InvoiceEntityId;

        db.Acts.Add(new Act
        {
            Number = "ACT-25",
            Date = new DateTime(2026, 4, 8),
            Amount = 25_000m,
            InvoiceId = parentPaymentId,
            InvoiceNumber = "25",
        });
        await db.SaveChangesAsync();

        var controller = new InvoicesController(db, new NoopEmailService(), new NoopFormRequirementsService());

        var result = await controller.Delete(parentPaymentId, CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Payments.FindAsync(parentPaymentId));
        Assert.Null(await db.Invoices.FindAsync(invoiceEntityId));
        var act = await db.Acts.SingleAsync();
        Assert.Null(act.InvoiceId);
        Assert.Null(act.InvoiceNumber);
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

    private static async Task<InvoiceAggregateIds> CreateInvoiceAggregateAsync(PaymentContext db)
    {
        var invoice = new Invoice
        {
            Number = "25",
            Date = new DateTime(2026, 4, 7),
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var invoiceDate = new DateTime(2026, 4, 7);
        var parent = CreatePayment(invoiceDate, 25_000m, "Счёт с рассрочкой");
        parent.DocumentKind = DocumentKind.Invoice;
        parent.InvoiceId = invoice.Id;
        parent.Account = invoice.Number;
        parent.AccountDate = invoice.Date;

        var firstInstallment = CreatePayment(invoiceDate, 4_100m, "Рассрочка по счёту №25 · платёж 1/2");
        firstInstallment.InvoiceId = invoice.Id;

        var secondInstallment = CreatePayment(invoiceDate.AddMonths(1), 4_100m, "Рассрочка по счёту №25 · платёж 2/2");
        secondInstallment.InvoiceId = invoice.Id;

        var unrelatedPayment = CreatePayment(invoiceDate, 2_000m, "Обычный платёж");

        db.Payments.AddRange(parent, firstInstallment, secondInstallment, unrelatedPayment);
        await db.SaveChangesAsync();

        return new InvoiceAggregateIds(
            invoice.Id,
            parent.Id,
            new[] { firstInstallment.Id, secondInstallment.Id },
            unrelatedPayment.Id);
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

    private sealed record InvoiceAggregateIds(
        int InvoiceEntityId,
        int ParentPaymentId,
        int[] InstallmentPaymentIds,
        int UnrelatedPaymentId);

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

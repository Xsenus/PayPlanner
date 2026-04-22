using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class StatsSummaryServiceTests
{
    [Fact]
    public async Task GetAsync_ExcludesRepresentativeInvoice_WhenInstallmentsExist()
    {
        await using var db = await CreateContextAsync();
        var client = new Client
        {
            Name = "Клиент со сводкой",
            IsActive = true,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Рассрочка",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "155",
            Date = new DateTime(2025, 10, 24),
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                Amount = 205_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 8, 25),
                PlannedDate = new DateTime(2026, 8, 25),
                Description = "Представительский счёт",
                DocumentKind = DocumentKind.Invoice,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            },
            new Payment
            {
                Amount = 115_000m,
                PaidAmount = 115_000m,
                Date = new DateTime(2026, 3, 25),
                PlannedDate = new DateTime(2026, 3, 25),
                Description = "Оплаченная часть",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Completed,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker,
            },
            new Payment
            {
                Amount = 90_000m,
                PaidAmount = 0m,
                Date = new DateTime(2026, 4, 25),
                PlannedDate = new DateTime(2026, 4, 25),
                Description = "Неоплаченная часть",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Income,
                ClientId = client.Id,
                ClientCaseId = clientCase.Id,
                InvoiceId = invoice.Id,
                Account = invoice.Number,
                AccountDate = invoice.Date,
                Notes = string.Empty,
                SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker,
            });
        await db.SaveChangesAsync();

        var service = new StatsSummaryService(db);

        var summary = await service.GetAsync(
            client.Id,
            null,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            null,
            PaymentType.Income,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(115_000m, summary.Income.CompletedAmount);
        Assert.Equal(90_000m, summary.Income.PendingAmount);
        Assert.Equal(205_000m, summary.Income.TotalAmount);
        Assert.Equal(2, summary.Income.TotalCount);
        Assert.Equal(90_000m, summary.Income.RemainingAmount);
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
}

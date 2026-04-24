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
            null,
            CancellationToken.None);

        Assert.Equal(115_000m, summary.Income.CompletedAmount);
        Assert.Equal(90_000m, summary.Income.PendingAmount);
        Assert.Equal(205_000m, summary.Income.TotalAmount);
        Assert.Equal(2, summary.Income.TotalCount);
        Assert.Equal(90_000m, summary.Income.RemainingAmount);
    }

    [Fact]
    public async Task GetAsync_FiltersExpenseStatsByClientRole()
    {
        await using var db = await CreateContextAsync();

        var supplier = new Client { Name = "Supplier", CreatedAt = DateTime.UtcNow, IsActive = true };
        var expenseClient = new Client { Name = "Expense Client", CreatedAt = DateTime.UtcNow, IsActive = true };
        var unrelated = new Client { Name = "Unrelated", CreatedAt = DateTime.UtcNow, IsActive = true };
        db.Clients.AddRange(supplier, expenseClient, unrelated);
        await db.SaveChangesAsync();

        db.Payments.AddRange(
            new Payment
            {
                Amount = 5_000m,
                PaidAmount = 1_000m,
                Date = new DateTime(2026, 4, 12),
                PlannedDate = new DateTime(2026, 4, 12),
                Description = "Supplier expense",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Expense,
                ClientId = supplier.Id,
                ExpenseClientId = expenseClient.Id,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            },
            new Payment
            {
                Amount = 7_000m,
                PaidAmount = 2_000m,
                Date = new DateTime(2026, 4, 13),
                PlannedDate = new DateTime(2026, 4, 13),
                Description = "Expense client expense",
                DocumentKind = DocumentKind.Payment,
                Status = PaymentStatus.Pending,
                Type = PaymentType.Expense,
                ClientId = unrelated.Id,
                ExpenseClientId = supplier.Id,
                Notes = string.Empty,
                SystemNotes = string.Empty,
            });
        await db.SaveChangesAsync();

        var service = new StatsSummaryService(db);

        var supplierSummary = await service.GetAsync(
            supplier.Id,
            null,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 30),
            null,
            PaymentType.Expense,
            null,
            null,
            "counterparty",
            CancellationToken.None);

        var expenseClientSummary = await service.GetAsync(
            supplier.Id,
            null,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 30),
            null,
            PaymentType.Expense,
            null,
            null,
            "expenseClient",
            CancellationToken.None);

        Assert.Equal(5_000m, supplierSummary.Expense.TotalAmount);
        Assert.Equal(4_000m, supplierSummary.Expense.PendingAmount);
        Assert.Equal(7_000m, expenseClientSummary.Expense.TotalAmount);
        Assert.Equal(5_000m, expenseClientSummary.Expense.PendingAmount);
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

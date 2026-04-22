using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DatabaseImportServiceTests
{
    [Fact]
    public async Task ImportSqliteAsync_WhenDuplicatePaymentsShareKey_PreservesDeferredMappingsForRelations()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"payplanner-import-source-{Guid.NewGuid():N}.sqlite");
        await using var targetConnection = await CreateOpenConnectionAsync();

        try
        {
            await SeedDuplicatePaymentsSourceAsync(sourcePath);

            await using (var targetSetupDb = CreateContext(targetConnection))
            {
                await targetSetupDb.Database.EnsureCreatedAsync();
            }

            var service = CreateService(targetConnection);
            var result = await service.ImportSqliteAsync(sourcePath);

            Assert.True(result.Success);
            Assert.DoesNotContain(result.Tables, table => table.Errors > 0);

            await using var verifyDb = CreateContext(targetConnection);
            var importedPayment = await verifyDb.Payments.SingleAsync();
            var linkedCase = await verifyDb.ClientCases.SingleAsync(item => item.Title == "Linked case");
            var importedLink = await verifyDb.InvoiceClientCases.SingleAsync();

            Assert.Equal(importedPayment.Id, importedLink.PaymentId);
            Assert.Equal(linkedCase.Id, importedLink.ClientCaseId);
        }
        finally
        {
            TryDeleteFile(sourcePath);
        }
    }

    [Fact]
    public async Task ImportSqliteAsync_WhenImportingContractInvoices_MapsRelationsToImportedPayments()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"payplanner-import-source-{Guid.NewGuid():N}.sqlite");
        await using var targetConnection = await CreateOpenConnectionAsync();

        try
        {
            await SeedContractInvoiceSourceAsync(sourcePath);

            int existingTargetPaymentId;
            await using (var targetSetupDb = CreateContext(targetConnection))
            {
                await targetSetupDb.Database.EnsureCreatedAsync();

                var existingPayment = CreatePayment(
                    new DateTime(2026, 1, 5),
                    1_000m,
                    "Existing target payment");

                targetSetupDb.Payments.Add(existingPayment);
                await targetSetupDb.SaveChangesAsync();
                existingTargetPaymentId = existingPayment.Id;
            }

            var service = CreateService(targetConnection);
            var result = await service.ImportSqliteAsync(sourcePath);

            Assert.True(result.Success);
            Assert.DoesNotContain(result.Tables, table => table.Errors > 0);

            await using var verifyDb = CreateContext(targetConnection);
            var importedPayment = await verifyDb.Payments
                .SingleAsync(payment => payment.Description == "Imported contract invoice payment");
            var contractLink = await verifyDb.ContractInvoices.SingleAsync();

            Assert.NotEqual(existingTargetPaymentId, importedPayment.Id);
            Assert.Equal(importedPayment.Id, contractLink.InvoiceId);
        }
        finally
        {
            TryDeleteFile(sourcePath);
        }
    }

    private static DatabaseImportService CreateService(SqliteConnection targetConnection)
    {
        var factory = new TestPaymentContextFactory(CreateOptions(targetConnection), new HttpContextAccessor());
        var provider = new DatabaseProviderInfo(DatabaseProviderKind.Sqlite, targetConnection.ConnectionString);
        return new DatabaseImportService(factory, provider, NullLogger<DatabaseImportService>.Instance);
    }

    private static async Task SeedDuplicatePaymentsSourceAsync(string sourcePath)
    {
        await using var sourceConnection = new SqliteConnection($"Data Source={sourcePath}");
        await sourceConnection.OpenAsync();
        await using var sourceDb = CreateContext(sourceConnection);
        await sourceDb.Database.EnsureCreatedAsync();

        var client = new Client { Name = "Source client" };
        var primaryCase = new ClientCase { Client = client, Title = "Primary case" };
        var linkedCase = new ClientCase { Client = client, Title = "Linked case" };
        var invoice = new Invoice
        {
            Number = "INV-100",
            Date = new DateTime(2026, 4, 1),
        };

        var firstPayment = CreateInvoicePayment(invoice, client, primaryCase, "Duplicated invoice payment");
        var secondPayment = CreateInvoicePayment(invoice, client, primaryCase, "Duplicated invoice payment");

        sourceDb.ClientCases.Add(linkedCase);
        sourceDb.Payments.AddRange(firstPayment, secondPayment);
        await sourceDb.SaveChangesAsync();

        sourceDb.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = secondPayment.Id,
            ClientCaseId = linkedCase.Id,
        });

        await sourceDb.SaveChangesAsync();
    }

    private static async Task SeedContractInvoiceSourceAsync(string sourcePath)
    {
        await using var sourceConnection = new SqliteConnection($"Data Source={sourcePath}");
        await sourceConnection.OpenAsync();
        await using var sourceDb = CreateContext(sourceConnection);
        await sourceDb.Database.EnsureCreatedAsync();

        var client = new Client { Name = "Contract client" };
        var contract = new Contract
        {
            Number = "CTR-1",
            Date = new DateTime(2026, 4, 2),
        };
        var invoice = new Invoice
        {
            Number = "INV-200",
            Date = new DateTime(2026, 4, 2),
        };
        var payment = CreateInvoicePayment(invoice, client, null, "Imported contract invoice payment");

        sourceDb.Payments.Add(payment);
        sourceDb.Contracts.Add(contract);
        await sourceDb.SaveChangesAsync();

        sourceDb.ContractInvoices.Add(new ContractInvoice
        {
            ContractId = contract.Id,
            InvoiceId = payment.Id,
        });

        await sourceDb.SaveChangesAsync();
    }

    private static Payment CreateInvoicePayment(Invoice invoice, Client client, ClientCase? clientCase, string description)
    {
        var paymentDate = new DateTime(2026, 4, 15);
        return new Payment
        {
            Invoice = invoice,
            Client = client,
            ClientCase = clientCase,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Amount = 15_000m,
            Date = paymentDate,
            PlannedDate = paymentDate,
            Description = description,
            DocumentKind = DocumentKind.Invoice,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
        };
    }

    private static Payment CreatePayment(DateTime date, decimal amount, string description)
        => new()
        {
            Amount = amount,
            Date = date,
            PlannedDate = date,
            Description = description,
            Status = PaymentStatus.Pending,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Payment,
        };

    private static PaymentContext CreateContext(SqliteConnection connection)
        => new(CreateOptions(connection), new HttpContextAccessor());

    private static DbContextOptions<PaymentContext> CreateOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

    private static async Task<SqliteConnection> CreateOpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class TestPaymentContextFactory : IDbContextFactory<PaymentContext>
    {
        private readonly DbContextOptions<PaymentContext> _options;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public TestPaymentContextFactory(DbContextOptions<PaymentContext> options, IHttpContextAccessor httpContextAccessor)
        {
            _options = options;
            _httpContextAccessor = httpContextAccessor;
        }

        public PaymentContext CreateDbContext()
            => new(_options, _httpContextAccessor);

        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}

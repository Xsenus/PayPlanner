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

public class ContractsControllerTests
{
    [Fact]
    public async Task Create_RequiresAmount_WhenBillingModeIsFixed()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-001",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = null,
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<ObjectResult>(actionResult.Result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains(nameof(request.Amount), details.Errors.Keys);
    }

    [Fact]
    public async Task Create_ClearsAmountAndValidUntil_WhenBillingModeIsActualAndContractIsOpenEnded()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-002",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            Amount = 15_000m,
            ValidUntil = new DateTime(2026, 12, 31),
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(created.Value);
        Assert.Equal(ContractBillingMode.Actual, dto.BillingMode);
        Assert.True(dto.IsOpenEnded);
        Assert.Null(dto.Amount);
        Assert.Null(dto.ValidUntil);

        var contract = await db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractBillingMode.Actual, contract.BillingMode);
        Assert.True(contract.IsOpenEnded);
        Assert.Null(contract.Amount);
        Assert.Null(contract.ValidUntil);
    }

    [Fact]
    public async Task Update_ClearsAmountAndValidUntil_WhenSwitchingToActualOpenEndedContract()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);

        var contract = new Contract
        {
            Number = "CT-003",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 20_000m,
            ValidUntil = new DateTime(2026, 12, 31),
            ClientContracts =
            {
                new ClientContract
                {
                    ClientId = client.Id,
                    Comment = "Исходный клиент",
                },
            },
        };

        db.Contracts.Add(contract);
        await db.SaveChangesAsync();

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = contract.Number,
            Date = contract.Date,
            BillingMode = ContractBillingMode.Actual,
            IsOpenEnded = true,
            Amount = 99_999m,
            ValidUntil = new DateTime(2027, 1, 1),
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Update(contract.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
        var dto = Assert.IsType<ContractDto>(ok.Value);
        Assert.Equal(ContractBillingMode.Actual, dto.BillingMode);
        Assert.True(dto.IsOpenEnded);
        Assert.Null(dto.Amount);
        Assert.Null(dto.ValidUntil);

        var updatedContract = await db.Contracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractBillingMode.Actual, updatedContract.BillingMode);
        Assert.True(updatedContract.IsOpenEnded);
        Assert.Null(updatedContract.Amount);
        Assert.Null(updatedContract.ValidUntil);
    }

    [Fact]
    public async Task Create_RejectsFixedContract_WhenLinkedInvoicesExceedContractAmount()
    {
        await using var db = await CreateContextAsync();
        var client = await CreateClientAsync(db);
        var invoicePayment = await CreateInvoicePaymentAsync(db, "INV-30", new DateTime(2026, 4, 14), 18_000m);

        var controller = new ContractsController(db, new NoopFormRequirementsService());
        var request = new UpsertContractRequest
        {
            Number = "CT-004",
            Date = new DateTime(2026, 4, 14),
            BillingMode = ContractBillingMode.Fixed,
            Amount = 10_000m,
            InvoiceIds = [invoicePayment.Id],
            Clients = [new UpsertContractClientRequest { ClientId = client.Id }],
        };

        var actionResult = await controller.Create(request, CancellationToken.None);

        var badRequest = Assert.IsType<ObjectResult>(actionResult.Result);
        var details = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Contains(nameof(request.Amount), details.Errors.Keys);
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

    private static async Task<Client> CreateClientAsync(PaymentContext db)
    {
        var client = new Client
        {
            Name = "Тестовый клиент",
            Company = "ООО Тест",
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<Payment> CreateInvoicePaymentAsync(PaymentContext db, string invoiceNumber, DateTime date, decimal amount)
    {
        var invoice = new Invoice
        {
            Number = invoiceNumber,
            Date = date,
        };

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var payment = new Payment
        {
            Amount = amount,
            CounterpartyName = "Тестовый клиент",
            Date = date,
            PlannedDate = date,
            Description = $"Счет {invoiceNumber}",
            DocumentKind = DocumentKind.Invoice,
            InvoiceId = invoice.Id,
            Account = invoice.Number,
            AccountDate = invoice.Date,
            Notes = string.Empty,
            Status = PaymentStatus.Pending,
            SystemNotes = string.Empty,
            Type = PaymentType.Income,
        };

        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }
}

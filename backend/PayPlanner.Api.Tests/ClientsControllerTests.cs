using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ClientsControllerTests
{
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";

    [Fact]
    public async Task Create_RoundTripsStructuredAddressEntries_WithFullAddress()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db);
        var controller = CreateController(db, userId);

        const string fullAddress = "633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19";
        var model = new Client
        {
            Name = "Тестовый клиент",
            Address = fullAddress,
            Notes = ClientProfileNotesPrefix + JsonSerializer.Serialize(new
            {
                addressEntries = new[]
                {
                    new
                    {
                        type = "registration",
                        sourceQuery = fullAddress,
                        fullAddress,
                        postalCode = "633208",
                        country = "Россия",
                        region = "Новосибирская обл",
                        district = "Искитимский р-н",
                        city = "г Искитим",
                        settlement = "Южный мкр",
                        street = "Южный мкр",
                        house = "37",
                        flat = "19",
                        fiasId = "fias-iskitim",
                        kladrId = "kladr-iskitim",
                        geoLat = "54.6265",
                        geoLon = "83.3045",
                    },
                },
            }),
            IsActive = true,
        };

        var action = await controller.Create(model, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(action);
        var dto = Assert.IsType<Client>(created.Value);
        var address = Assert.Single(dto.Addresses);

        Assert.Equal(fullAddress, address.UnrestrictedValue);
        Assert.Equal("Россия", address.Country);
        Assert.Equal("Новосибирская обл", address.RegionWithType);
        Assert.Equal("Искитимский р-н", address.AreaWithType);
        Assert.Equal("г Искитим", address.CityWithType);
        Assert.Equal("Южный мкр", address.SettlementWithType);
        Assert.Equal("Южный мкр", address.StreetWithType);
        Assert.Equal("37", address.House);
        Assert.Equal("19", address.Flat);

        var stored = await db.AddressRecords.SingleAsync(item => item.ClientId == dto.Id);
        Assert.Equal(fullAddress, stored.UnrestrictedValue);
        Assert.Equal("Россия", stored.Country);
        Assert.Equal("Новосибирская обл", stored.RegionWithType);
        Assert.Equal("Искитимский р-н", stored.AreaWithType);
        Assert.Equal("г Искитим", stored.CityWithType);
        Assert.Equal("Южный мкр", stored.SettlementWithType);
        Assert.Equal("Южный мкр", stored.StreetWithType);
        Assert.Equal("37", stored.House);
        Assert.Equal("19", stored.Flat);
    }

    [Fact]
    public async Task GetStats_IncludesPaymentsLinkedThroughInvoiceCases_ForSelectedCase()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var clientCase = new ClientCase
        {
            Client = client,
            Title = "Дело по счету",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "30",
            Date = new DateTime(2026, 4, 8),
        };

        db.Clients.Add(client);
        db.ClientCases.Add(clientCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedPayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 9_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 9),
            PlannedDate = new DateTime(2026, 4, 9),
            Description = "Рассрочка по счету №30",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "30",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.Add(linkedPayment);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedPayment.Id,
            ClientCaseId = clientCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetStats(client.Id, clientCase.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var stats = Assert.IsType<ClientStats>(ok.Value);

        Assert.Equal(client.Id, stats.ClientId);
        Assert.Equal(9_000m, stats.TotalIncome);
        Assert.Equal(9_000m, stats.OutstandingIncome);
        Assert.Equal(0m, stats.TotalExpenses);
        Assert.Equal(1, stats.TotalPayments);
        Assert.Equal(0, stats.PaidPayments);
        Assert.Equal(1, stats.PendingPayments);
        Assert.Equal(0, stats.OverduePayments);
        Assert.Collection(
            stats.RecentPayments,
            payment => Assert.Equal(linkedPayment.Id, payment.Id));
    }

    [Fact]
    public async Task GetCaseFinanceSummaries_AggregatesPaymentsLinkedThroughInvoiceCases()
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, roleName: "admin");
        var controller = CreateController(db, userId);

        var client = new Client
        {
            Name = "Тестовый клиент",
            ResponsibleUserId = userId,
            IsActive = true,
        };

        var primaryCase = new ClientCase
        {
            Client = client,
            Title = "Основное дело",
            Status = ClientCaseStatus.Open,
        };

        var secondaryCase = new ClientCase
        {
            Client = client,
            Title = "Второе дело",
            Status = ClientCaseStatus.Open,
        };

        var invoice = new Invoice
        {
            Number = "31",
            Date = new DateTime(2026, 4, 10),
        };

        db.Clients.Add(client);
        db.ClientCases.AddRange(primaryCase, secondaryCase);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var linkedIncomePayment = new Payment
        {
            Amount = 18_000m,
            PaidAmount = 6_000m,
            CounterpartyName = "Тестовый клиент",
            Date = new DateTime(2026, 4, 10),
            PlannedDate = new DateTime(2026, 4, 10),
            Description = "Платеж по счету №31",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Pending,
            IsPaid = false,
            Type = PaymentType.Income,
            ClientId = client.Id,
            ClientCaseId = null,
            InvoiceId = invoice.Id,
            Account = "31",
            AccountDate = invoice.Date,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        var unrelatedExpense = new Payment
        {
            Amount = 4_500m,
            PaidAmount = 4_500m,
            CounterpartyName = "Поставщик",
            Date = new DateTime(2026, 4, 11),
            PlannedDate = new DateTime(2026, 4, 11),
            Description = "Расход по другому делу",
            DocumentKind = DocumentKind.Payment,
            Status = PaymentStatus.Completed,
            IsPaid = true,
            Type = PaymentType.Expense,
            ClientId = client.Id,
            ClientCaseId = secondaryCase.Id,
            Notes = string.Empty,
            SystemNotes = string.Empty,
        };

        db.Payments.AddRange(linkedIncomePayment, unrelatedExpense);
        await db.SaveChangesAsync();

        db.InvoiceClientCases.Add(new InvoiceClientCase
        {
            PaymentId = linkedIncomePayment.Id,
            ClientCaseId = primaryCase.Id,
        });
        await db.SaveChangesAsync();

        var action = await controller.GetCaseFinanceSummaries(client.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var summaries = Assert.IsAssignableFrom<List<ClientCaseFinanceSummaryDto>>(ok.Value);

        var primarySummary = Assert.Single(summaries, item => item.CaseId == primaryCase.Id);
        Assert.Equal(18_000m, primarySummary.IncomeTotal);
        Assert.Equal(6_000m, primarySummary.IncomePaid);
        Assert.Equal(12_000m, primarySummary.IncomeOutstanding);
        Assert.Equal(1, primarySummary.IncomeCount);
        Assert.Equal(0m, primarySummary.ExpenseTotal);

        var secondarySummary = Assert.Single(summaries, item => item.CaseId == secondaryCase.Id);
        Assert.Equal(0m, secondarySummary.IncomeTotal);
        Assert.Equal(4_500m, secondarySummary.ExpenseTotal);
        Assert.Equal(4_500m, secondarySummary.ExpensePaid);
        Assert.Equal(0m, secondarySummary.ExpenseOutstanding);
        Assert.Equal(1, secondarySummary.ExpenseCount);
    }

    private static async Task<int> CreateUserAsync(PaymentContext db, string roleName = "user")
    {
        var role = new Role
        {
            Name = roleName,
            Description = roleName,
            CreatedAt = DateTime.UtcNow,
        };

        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "client-tests@example.com",
            PasswordHash = "hash",
            FullName = "Тестовый пользователь",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static ClientsController CreateController(PaymentContext db, int userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "Тестовый пользователь"),
        };

        var controller = new ClientsController(db, new NoopEnrichmentService(), new NoopFormRequirementsService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
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

    private sealed class NoopFormRequirementsService : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    private sealed class NoopEnrichmentService : ILegalEntityEnrichmentService
    {
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(Array.Empty<ILegalEntityEnrichmentService.DadataAddressSuggestion>());

        public Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
            => Task.FromResult<CleanedAddress?>(null);

        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
            => Task.FromResult<CleanedEmail?>(null);

        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
            => Task.FromResult<CleanedPhone?>(null);

        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
            => Task.FromResult<LegalEntityDetails?>(null);

        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>(Array.Empty<LegalEntitySuggestion>());
    }
}

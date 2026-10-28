using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneyOperationHistoricalCurrencyTests
{
    [Fact]
    public async Task Sqlite_DescriptionNeverInfersCurrencyOrBypassesInvoiceGuard()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await VerifyAsync(db);
    }

    [Fact]
    public async Task Postgres_DescriptionNeverInfersCurrencyOrBypassesInvoiceGuard()
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci",
                Password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD") };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci"
            || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("An existing loopback PostgreSQL server and isolated payplanner_ci role are required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_currency_edit_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_currency_edit_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var role = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await role.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(builder.ConnectionString).Options);
            await VerifyAsync(db);
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_currency_edit_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed record Scenario(string Name, string? Stored, string? Supplied, bool Imported = false,
        bool Link = false, bool ChangeAmount = false, bool CanEdit = true, bool Allowed = true,
        MoneyOperationSource Source = MoneyOperationSource.Bank, string AccountCurrency = "RUB");

    private static async Task VerifyAsync(PaymentContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        var companies = new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = "[]" };
        db.SystemSettings.Add(companies);
        var companyIds = new List<int>();
        var scenarios = new[]
        {
            new Scenario("unknown-note", null, null),
            new Scenario("imported-unknown-note", null, null, Imported: true),
            new Scenario("blank-note", " ", null, Imported: true),
            new Scenario("ruble-omitted", "RUB", null, Imported: true),
            new Scenario("ruble-raw", " rub ", "RUB", Imported: true),
            new Scenario("historical-usd", "USD", "USD", Imported: true),
            new Scenario("unknown-link", null, null, Link: true, Allowed: false),
            new Scenario("imported-unknown-link", null, null, Imported: true, Link: true, Allowed: false),
            new Scenario("usd-link", "USD", "USD", Link: true, Allowed: false),
            new Scenario("unknown-amount", null, null, ChangeAmount: true, Allowed: false),
            new Scenario("imported-currency-change", null, "RUB", Imported: true, Allowed: false),
            new Scenario("imported-amount-change", "RUB", "RUB", Imported: true, ChangeAmount: true, Allowed: false),
            new Scenario("no-edit", null, null, CanEdit: false, Allowed: false),
            new Scenario("ruble-link", "RUB", null, Link: true),
            new Scenario("confirmed-manual-currency", null, "RUB", ChangeAmount: true),
            new Scenario("cash-unknown-note", null, null, Source: MoneyOperationSource.Cash, Allowed: false),
            new Scenario("cash-unknown-account", null, null, Source: MoneyOperationSource.Cash, AccountCurrency: "", Allowed: false),
            new Scenario("cash-ruble-note", "RUB", null, Source: MoneyOperationSource.Cash),
        };
        foreach (var scenario in scenarios)
        {
            var role = new Role { Name = scenario.Name, Description = "Synthetic currency regression" };
            var user = new User { Email = scenario.Name + "@fixture.local", FullName = "Synthetic employee", PasswordHash = "hash",
                Role = role, IsEmployee = true, IsActive = true, IsApproved = true };
            var company = new LegalEntity { ShortName = scenario.Name };
            var account = new MoneyAccount { Name = "Synthetic account", Source = scenario.Source, Currency = scenario.AccountCurrency, Company = company, IsActive = true };
            var invoice = new Invoice { Number = scenario.Name, Date = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc) };
            db.Users.Add(user);
            db.MoneyAccounts.Add(account);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            companyIds.Add(company.Id);
            companies.Value = JsonSerializer.Serialize(companyIds);
            db.RolePermissions.AddRange(
                new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, CanEdit = scenario.CanEdit, ViewScope = DataVisibilityScope.All },
                new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Accounts, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All });
            db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = company.Id });
            var payment = new Payment { InvoiceId = invoice.Id, SellerCompanyId = company.Id, Account = invoice.Number,
                DocumentKind = DocumentKind.Invoice, Type = PaymentType.Income, Status = PaymentStatus.Pending, Amount = 100m, Date = invoice.Date };
            var operation = new MoneyOperation { MoneyAccountId = account.Id, Source = scenario.Source,
                Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, Amount = 10m,
                Currency = scenario.Stored, Date = invoice.Date.AddHours(3), Purpose = "Original", ResponsibleUserId = user.Id,
                SourceMovementKey = scenario.Imported ? scenario.Name : null };
            db.MoneyOperations.Add(operation);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            if (scenario.Source == MoneyOperationSource.Cash)
            {
                db.CashOrders.Add(new CashOrder { Number = scenario.Name, Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
                    MoneyOperationId = operation.Id, MoneyAccountId = account.Id, CompanyId = company.Id,
                    Currency = scenario.Stored, Amount = operation.Amount, Date = operation.Date, Basis = "Original" });
                await db.SaveChangesAsync();
            }
            var operationId = operation.Id;
            var storedDate = await db.MoneyOperations.AsNoTracking().Where(item => item.Id == operationId).Select(item => item.Date).SingleAsync();
            var request = new MoneyOperation { Id = operationId, MoneyAccountId = account.Id, Source = scenario.Source,
                Direction = operation.Direction, AccountingType = operation.AccountingType,
                Amount = scenario.ChangeAmount ? 20m : 10m, Currency = scenario.Supplied,
                Date = invoice.Date, Purpose = "Updated", ResponsibleUserId = user.Id,
                LinkedInvoiceId = scenario.Link ? invoice.Id : null,
                MatchStatus = scenario.Link ? MoneyOperationMatchStatus.Matched : MoneyOperationMatchStatus.None };
            db.ChangeTracker.Clear();
            var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                    new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }, "Test")) } },
            };
            var result = await controller.Update(operationId, request);
            Assert.True(scenario.Allowed ? result is OkObjectResult : result is BadRequestObjectResult or ForbidResult,
                $"{scenario.Name}: unexpected {result.GetType().Name}");
            db.ChangeTracker.Clear();
            var saved = await db.MoneyOperations.SingleAsync(item => item.Id == operationId);
            var savedPayment = await db.Payments.SingleAsync(item => item.Id == payment.Id);
            Assert.Equal(scenario.Allowed ? "Updated" : "Original", saved.Purpose);
            Assert.Equal(scenario.Allowed && scenario.ChangeAmount ? 20m : 10m, saved.Amount);
            Assert.Equal(scenario.Allowed && scenario.ChangeAmount ? invoice.Date : storedDate, saved.Date);
            Assert.Equal(scenario.Allowed && scenario.ChangeAmount ? "RUB" : scenario.Stored, saved.Currency);
            Assert.Equal(account.Id, saved.MoneyAccountId);
            Assert.Equal(scenario.Imported ? scenario.Name : null, saved.SourceMovementKey);
            Assert.Equal(scenario.Allowed && scenario.Link ? invoice.Id : (int?)null, saved.LinkedInvoiceId);
            Assert.Equal(scenario.Allowed && scenario.Link ? 10m : 0m, savedPayment.PaidAmount);
            Assert.Equal(scenario.Allowed && scenario.Link ? PaymentStatus.Processing : PaymentStatus.Pending, savedPayment.Status);
            if (scenario.Source == MoneyOperationSource.Cash)
            {
                var order = await db.CashOrders.SingleAsync(item => item.MoneyOperationId == operationId);
                Assert.Equal(scenario.Stored, order.Currency);
                Assert.Equal(10m, order.Amount);
                Assert.Equal(CashOrderStatus.Posted, order.Status);
                Assert.Equal(account.Id, order.MoneyAccountId);
                Assert.Equal(company.Id, order.CompanyId);
                if (!scenario.Allowed) Assert.Equal("Original", order.Basis);
            }
            companies = await db.SystemSettings.SingleAsync(item => item.Key == "legalEntities.mainCompanyIds");
        }
    }
}

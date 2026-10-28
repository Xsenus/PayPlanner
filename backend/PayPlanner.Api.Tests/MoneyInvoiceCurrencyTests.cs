using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneyInvoiceCurrencyTests
{
    [Theory]
    [InlineData("allowed", "RUB")]
    [InlineData("no-edit", "RUB")]
    [InlineData("no-invoice-view", "RUB")]
    [InlineData("foreign-company", "RUB")]
    [InlineData("allowed", "USD")]
    [InlineData("allowed", null)]
    public async Task CurrencyGuard_PreservesEmployeePermissionAndCompanyBoundaries(string scope, string? currency)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "synthetic-employee", Description = "Synthetic" };
        var user = new User { Email = "employee@fixture.local", FullName = "Synthetic", PasswordHash = "hash", Role = role, IsEmployee = true, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Own" };
        var other = new LegalEntity { ShortName = "Other" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(company, other);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, CanEdit = scope != "no-edit", ViewScope = DataVisibilityScope.All },
            new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Accounts, CanView = scope != "no-invoice-view", CanEdit = true, ViewScope = DataVisibilityScope.All });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = company.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id, other.Id }) });
        var account = new MoneyAccount { Name = "Synthetic bank", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = currency ?? "RUB", IsActive = true };
        var invoice = new Invoice { Number = "SYN-501", Date = new DateTime(2026, 10, 20) };
        var operation = new MoneyOperation
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 10m,
            Currency = currency,
            Date = invoice.Date,
            ResponsibleUserId = user.Id,
            Purpose = "Счёт SYN-501"
        };
        var payment = new Payment
        {
            Invoice = invoice,
            SellerCompanyId = scope == "foreign-company" ? other.Id : company.Id,
            Account = invoice.Number,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 100m,
            PaidAmount = 0m,
            Date = invoice.Date
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };
        var candidates = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);
        var result = await controller.LinkInvoice(operation.Id, new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id }, CancellationToken.None);
        db.ChangeTracker.Clear();
        var saved = await db.MoneyOperations.SingleAsync();
        var savedPayment = await db.Payments.SingleAsync();
        if (scope == "allowed" && currency == "RUB")
        {
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(Assert.IsType<OkObjectResult>(candidates.Result).Value));
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(10m, savedPayment.PaidAmount);
            Assert.Equal(invoice.Id, saved.LinkedInvoiceId);
        }
        else
        {
            if (candidates.Result is OkObjectResult ok) Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationInvoiceCandidateDto>>(ok.Value));
            else Assert.True(candidates.Result is ForbidResult or NotFoundResult or BadRequestObjectResult);
            Assert.True(result is ForbidResult or NotFoundResult or BadRequestObjectResult);
            Assert.Equal(0m, savedPayment.PaidAmount);
            Assert.Null(saved.LinkedInvoiceId);
        }
    }

    [Fact]
    public async Task PreviouslyLinkedForeignMovement_IsNotReappliedOrChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin", Description = "Synthetic" };
        var user = new User { Email = "existing@fixture.local", FullName = "Synthetic", PasswordHash = "hash", Role = role, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Synthetic company" };
        db.Users.Add(user);
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var account = new MoneyAccount { Name = "Synthetic bank", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = "USD", IsActive = true };
        var invoice = new Invoice { Number = "SYN-OLD", Date = new DateTime(2026, 10, 20) };
        var payment = new Payment
        {
            Invoice = invoice,
            SellerCompanyId = company.Id,
            Account = invoice.Number,
            DocumentKind = DocumentKind.Invoice,
            Type = PaymentType.Income,
            Status = PaymentStatus.Processing,
            Amount = 100m,
            PaidAmount = 25m,
            Date = invoice.Date
        };
        var operation = new MoneyOperation
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 25m,
            Currency = "USD",
            Date = invoice.Date,
            MatchStatus = MoneyOperationMatchStatus.Matched
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        operation.LinkedInvoiceId = invoice.Id;
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin")], "Test"))
                }
            },
        };
        var result = await controller.LinkInvoice(operation.Id, new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(25m, (await db.Payments.SingleAsync()).PaidAmount);
        var persisted = await db.MoneyOperations.SingleAsync();
        Assert.Equal(invoice.Id, persisted.LinkedInvoiceId);
        Assert.Equal("USD", persisted.Currency);
        Assert.Empty(await db.ClientBalanceTransactions.ToListAsync());
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("USD", false, false)]
    [InlineData("RUB", true, false)]
    [InlineData(" rub ", true, false)]
    [InlineData(null, false, true)]
    [InlineData("USD", false, true)]
    [InlineData("RUB", true, true)]
    public async Task ManualInvoiceLink_RequiresConfirmedRubleMovement(string? currency, bool allowed, bool expense)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin", Description = "Synthetic" };
        var user = new User { Email = "currency@fixture.local", FullName = "Synthetic", PasswordHash = "hash", Role = role, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Synthetic company" };
        db.Users.Add(user);
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var account = new MoneyAccount { Name = "Synthetic bank", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = currency ?? "RUB", IsActive = true };
        var invoice = new Invoice { Number = "SYN-33", Date = new DateTime(2026, 10, 20) };
        var operation = new MoneyOperation
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = expense ? MoneyOperationDirection.Expense : MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Amount = 100m,
            Currency = currency,
            Date = new DateTime(2026, 10, 21),
            Purpose = "Счёт SYN-33"
        };
        var payment = new Payment
        {
            Invoice = invoice,
            SellerCompanyId = company.Id,
            Account = invoice.Number,
            DocumentKind = DocumentKind.Invoice,
            Type = expense ? PaymentType.Expense : PaymentType.Income,
            Status = PaymentStatus.Pending,
            Amount = 100m,
            PaidAmount = 0m,
            Date = invoice.Date
        };
        db.MoneyOperations.Add(operation);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin")], "Test"))
                }
            },
        };
        var candidates = await controller.GetInvoiceCandidates(operation.Id, CancellationToken.None);
        var result = await controller.LinkInvoice(operation.Id, new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = invoice.Id }, CancellationToken.None);
        db.ChangeTracker.Clear();
        var saved = await db.MoneyOperations.SingleAsync();
        var savedPayment = await db.Payments.SingleAsync();
        if (allowed)
        {
            Assert.IsType<OkObjectResult>(candidates.Result);
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(invoice.Id, saved.LinkedInvoiceId);
            Assert.Equal(100m, savedPayment.PaidAmount);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(candidates.Result);
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Null(saved.LinkedInvoiceId);
            Assert.Equal(0m, savedPayment.PaidAmount);
            Assert.Equal(PaymentStatus.Pending, savedPayment.Status);
            Assert.Empty(await db.ClientBalanceTransactions.ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => MoneyOperationInvoiceAllocationService.ApplyAsync(db, saved, invoice.Id));
            Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        }
    }
}

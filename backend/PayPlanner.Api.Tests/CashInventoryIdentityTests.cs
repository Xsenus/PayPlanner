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
using PayPlanner.Api.Models.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashInventoryIdentityTests
{
    [Theory]
    [InlineData("confirmed", false)]
    [InlineData("confirmed", true)]
    [InlineData("unassigned", false)]
    [InlineData("unassigned", true)]
    [InlineData("no-id", false)]
    [InlineData("wrong-name", false)]
    public async Task Inventory_SameNamedAccountsUsesExplicitIdentityOnlyWhenHistoryIsConfirmed(string scenario, bool correction)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var first = new LegalEntity { ShortName = "Компания 1" };
        var second = new LegalEntity { ShortName = "Компания 2" };
        var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = first, IsActive = true };
        var other = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = second, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(cash, other);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { first.Id, second.Id }) });
        db.CashOrders.AddRange(
            new CashOrder { Number = "SYN-FIRST", Status = CashOrderStatus.Posted, Type = CashOrderType.Incoming, Date = new DateTime(2026, 10, 27),
                Amount = 100m, Currency = "RUB", Account = "Касса", MoneyAccountId = cash.Id, CompanyId = first.Id },
            new CashOrder { Number = "SYN-OTHER", Status = CashOrderStatus.Posted, Type = CashOrderType.Incoming, Date = new DateTime(2026, 10, 27),
                Amount = 900m, Currency = "RUB", Account = "Касса", MoneyAccountId = other.Id, CompanyId = second.Id });
        if (scenario == "unassigned") db.CashOrders.Add(new CashOrder { Number = "SYN-UNKNOWN", Status = CashOrderStatus.Posted,
            Type = CashOrderType.Incoming, Date = new DateTime(2026, 10, 27), Amount = 40m, Currency = "RUB", Account = "Касса" });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest { AccountName = scenario == "wrong-name" ? "Другая" : "Касса",
            MoneyAccountId = scenario == "no-id" ? null : cash.Id, Date = new DateTime(2026, 10, 28), ActualBalance = 120m, CreateCorrection = correction }, CancellationToken.None);
        if (scenario == "confirmed")
        {
            var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(result).Value);
            Assert.Equal(100m, inventory.AccountingBalance);
            Assert.Equal(20m, inventory.Difference);
            if (correction)
            {
                var adjustment = await db.CashOrders.SingleAsync(item => item.Id == inventory.CorrectionCashOrderId);
                Assert.Equal(cash.Id, adjustment.MoneyAccountId);
                Assert.Equal(first.Id, adjustment.CompanyId);
                Assert.Equal(20m, adjustment.Amount);
            }
            else Assert.Null(inventory.CorrectionCashOrderId);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashInventories.ToListAsync());
        }
        var otherOrder = await db.CashOrders.SingleAsync(item => item.Number == "SYN-OTHER");
        Assert.Equal(900m, otherOrder.Amount);
        Assert.Equal(other.Id, otherOrder.MoneyAccountId);
    }

    [Theory]
    [InlineData("change-currency")]
    [InlineData("same-currency")]
    [InlineData("post-mismatch")]
    [InlineData("post-inactive")]
    [InlineData("post-valid")]
    [InlineData("already-posted")]
    public async Task BoundCashOrder_ProtectsCurrencyAndPosting(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = scenario != "post-inactive" };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var order = new CashOrder { Number = "SYN-ORDER", Type = CashOrderType.Incoming,
            Status = scenario == "already-posted" ? CashOrderStatus.Posted : CashOrderStatus.Draft,
            Date = new DateTime(2026, 10, 28), Amount = 100m, Currency = scenario is "post-mismatch" or "already-posted" ? "USD" : "RUB",
            Account = account.Name, MoneyAccountId = account.Id, CompanyId = company.Id };
        db.CashOrders.Add(order);
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        IActionResult result;
        if (scenario is "change-currency" or "same-currency")
            result = await controller.Update(order.Id, new CashOrder { Type = order.Type, Amount = order.Amount, Date = order.Date,
                Account = order.Account, Currency = scenario == "change-currency" ? "USD" : "RUB", Comment = "Примечание" }, CancellationToken.None);
        else
            result = await controller.Post(order.Id, CancellationToken.None);
        if (scenario is "change-currency" or "post-mismatch" or "post-inactive") Assert.IsType<BadRequestObjectResult>(result);
        else Assert.IsType<OkObjectResult>(result);
        db.ChangeTracker.Clear();
        var saved = await db.CashOrders.SingleAsync();
        Assert.Equal(scenario is "post-mismatch" or "already-posted" ? "USD" : "RUB", saved.Currency);
        Assert.Equal(scenario is "post-valid" or "already-posted" ? CashOrderStatus.Posted : CashOrderStatus.Draft, saved.Status);
        Assert.Equal(account.Id, saved.MoneyAccountId);
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("renamed")]
    [InlineData("unbound")]
    [InlineData("inactive")]
    [InlineData("currency")]
    public async Task CopyCashOrder_PreservesOnlyConfirmedUsableAccount(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = scenario != "inactive" };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var source = new CashOrder { Number = "SYN-SOURCE", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 27), Amount = 100m, Currency = scenario == "currency" ? "USD" : "RUB", Account = scenario == "renamed" ? "Прежнее название" : account.Name,
            MoneyAccountId = scenario == "unbound" ? null : account.Id, CompanyId = scenario == "unbound" ? null : company.Id };
        db.CashOrders.Add(source);
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.Copy(source.Id, CancellationToken.None);
        if (scenario is "inactive" or "currency")
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Single(await db.CashOrders.ToListAsync());
        }
        else
        {
            var dto = Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(result).Value);
            Assert.Equal(scenario != "unbound", dto.HasLinkedCashAccount);
            var pagedAction = await controller.GetPaged(null, null, null, null, null, ct: CancellationToken.None);
            var paged = Assert.IsType<PagedResponse<CashOrderDto>>(Assert.IsType<OkObjectResult>(pagedAction.Result).Value);
            Assert.Equal(scenario != "unbound", Assert.Single(paged.Items, item => item.Id == dto.Id).HasLinkedCashAccount);
            var copy = await db.CashOrders.SingleAsync(order => order.Id == dto.Id);
            Assert.Equal(CashOrderStatus.Draft, copy.Status);
            Assert.Equal(scenario != "unbound" ? account.Id : null, copy.MoneyAccountId);
            Assert.Equal(scenario != "unbound" ? company.Id : null, copy.CompanyId);
            Assert.Equal(account.Name, copy.Account);
            Assert.Equal(100m, source.Amount);
        }
    }

    [Fact]
    public void ManualCashOrder_JsonSelectionCannotAssignTrustedIdentity()
    {
        var order = System.Text.Json.JsonSerializer.Deserialize<CashOrder>(
            "{\"selectedMoneyAccountId\":123,\"moneyAccountId\":456,\"companyId\":789}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(order);
        Assert.Equal(123, order.SelectedMoneyAccountId);
        Assert.Null(order.MoneyAccountId);
        Assert.Null(order.CompanyId);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign")]
    [InlineData("inactive")]
    [InlineData("currency")]
    [InlineData("linked")]
    public async Task ManualCashOrder_ValidatesExplicitAccountSelection(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var other = new LegalEntity { ShortName = "Чужая компания" };
        var account = new MoneyAccount { Name = "Подтверждённая касса", Source = MoneyOperationSource.Cash, Currency = "RUB",
            Company = scenario == "foreign" ? other : company, IsActive = scenario != "inactive" };
        db.Users.Add(user);
        db.LegalEntities.Add(company);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.Create(new CashOrder { Number = "SYN-MANUAL", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 28), Amount = 100m, Currency = scenario == "currency" ? "USD" : "RUB", Account = "Подменённое название",
            SelectedMoneyAccountId = account.Id, PaymentId = scenario == "linked" ? 1 : null }, CancellationToken.None);
        if (scenario == "valid")
        {
            Assert.True(Assert.IsType<CashOrderDto>(Assert.IsType<CreatedResult>(result).Value).HasLinkedCashAccount);
            var saved = Assert.Single(await db.CashOrders.ToListAsync());
            Assert.Equal(account.Id, saved.MoneyAccountId);
            Assert.Equal(company.Id, saved.CompanyId);
            Assert.Equal(account.Name, saved.Account);
            var inventory = await controller.CreateInventory(new CashInventoryCreateRequest { AccountName = account.Name, MoneyAccountId = account.Id,
                Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = false }, CancellationToken.None);
            var inventoryDto = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(inventory).Value);
            Assert.Equal(100m, inventoryDto.AccountingBalance);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashOrders.ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_RejectsUnassignedOrdersInsteadOfGuessingByName(bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        db.CashOrders.Add(new CashOrder { Number = "SYN-OLD", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 27), Amount = 100m, Currency = "RUB", Account = account.Name });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest { AccountName = account.Name, MoneyAccountId = account.Id,
            Date = new DateTime(2026, 10, 28), ActualBalance = 0m, CreateCorrection = createCorrection }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        var order = Assert.Single(await db.CashOrders.ToListAsync());
        Assert.Equal(100m, order.Amount);
        Assert.Null(order.MoneyAccountId);
        Assert.Null(order.CompanyId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CashAccountWithUnassignedHistory_IsArchivedInsteadOfDeleted(bool inventoryHistory)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        if (inventoryHistory)
            db.CashInventories.Add(new CashInventory { Number = "SYN-INV", AccountName = account.Name, Date = new DateTime(2026, 10, 27) });
        else
            db.CashOrders.Add(new CashOrder { Number = "SYN-ORDER", Account = account.Name, Amount = 100m, Currency = "RUB", Date = new DateTime(2026, 10, 27) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) };
        var controller = new MoneyAccountsController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
        Assert.IsType<NoContentResult>(await controller.Delete(account.Id, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.False((await db.MoneyAccounts.SingleAsync()).IsActive);
        if (inventoryHistory) Assert.Null((await db.CashInventories.SingleAsync()).MoneyAccountId);
        else Assert.Null((await db.CashOrders.SingleAsync()).MoneyAccountId);
    }

    [Theory]
    [InlineData("old-order", false)]
    [InlineData("new-order", false)]
    [InlineData("old-inventory", false)]
    [InlineData("new-inventory", false)]
    [InlineData("partial-order", false)]
    [InlineData("bank-to-cash", false)]
    [InlineData("bank-to-cash-same-name", false)]
    [InlineData("deleted-order", false)]
    [InlineData("bound-order", true)]
    [InlineData("unchanged", true)]
    [InlineData("other-history", true)]
    public async Task CashAccountRename_ProtectsUnassignedHistoricalNames(string scenario, bool allowed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = scenario.StartsWith("bank-to-cash") ? MoneyOperationSource.Bank : MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        if (scenario == "old-inventory" || scenario == "new-inventory")
            db.CashInventories.Add(new CashInventory { Number = "SYN-INV", AccountName = scenario == "new-inventory" ? "Новая касса" : "Касса", Date = new DateTime(2026, 10, 27) });
        else
        {
            var order = new CashOrder { Number = "SYN-ORDER", Account = scenario == "new-order" ? "Новая касса" : scenario == "other-history" ? "Постороннее название" : "Касса",
                Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted, Amount = 100m, Currency = "RUB", Date = new DateTime(2026, 10, 27),
                MoneyAccountId = scenario == "bound-order" || scenario == "partial-order" ? account.Id : null, CompanyId = scenario == "bound-order" ? company.Id : null };
            db.CashOrders.Add(order);
            await db.SaveChangesAsync();
            if (scenario == "deleted-order") db.Entry(order).Property<bool>("IsDeleted").CurrentValue = true;
        }
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) };
        var controller = new MoneyAccountsController(db) { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.Update(account.Id, new MoneyAccount
        {
            Name = scenario == "unchanged" || scenario == "bank-to-cash-same-name" ? "Касса" : "Новая касса", Source = scenario.StartsWith("bank-to-cash") ? MoneyOperationSource.Cash : account.Source,
            CompanyId = company.Id, Currency = "RUB", IsActive = true,
        }, CancellationToken.None);
        if (allowed) Assert.IsType<OkObjectResult>(result.Result);
        else Assert.IsType<ConflictObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.Equal(allowed && scenario != "unchanged" ? "Новая касса" : "Касса", (await db.MoneyAccounts.SingleAsync()).Name);
    }

    [Theory]
    [InlineData("USD", false)]
    [InlineData("USD", true)]
    [InlineData(" ", false)]
    [InlineData(" ", true)]
    public async Task Inventory_RejectsNonRubleOrUnknownAccountCurrencyEvenWhenEmpty(string currency, bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var account = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = currency, Company = company, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = account.Name, MoneyAccountId = account.Id, Date = new DateTime(2026, 10, 28),
            ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("other-account")]
    [InlineData("wrong-company")]
    [InlineData("partial-company")]
    public async Task Inventory_UsesBoundAccountIdentityInsteadOfHistoricalName(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Компания кассы" };
        var otherCompany = new LegalEntity { ShortName = "Другая компания" };
        var cash = new MoneyAccount { Name = "Текущее название", Source = MoneyOperationSource.Cash, Company = company, IsActive = true };
        var other = new MoneyAccount { Name = "Другая касса", Source = MoneyOperationSource.Cash, Company = otherCompany, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(cash, other);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id, otherCompany.Id }) });
        var oldOrder = new CashOrder { Number = "SYN-OLD", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 27), Amount = 100m, Currency = "RUB",
            Account = scenario == "renamed" || scenario == "wrong-company" ? "Прежнее название" : cash.Name,
            MoneyAccountId = scenario == "partial-company" ? null : scenario == "other-account" ? other.Id : cash.Id,
            CompanyId = scenario == "renamed" ? company.Id : otherCompany.Id };
        db.CashOrders.Add(oldOrder);
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } } };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = cash.Name, MoneyAccountId = cash.Id, Date = new DateTime(2026, 10, 28),
            ActualBalance = scenario == "renamed" ? 100m : 0m, CreateCorrection = true,
        }, CancellationToken.None);
        if (scenario is "wrong-company" or "partial-company")
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashInventories.ToListAsync());
        }
        else
        {
            var inventory = Assert.IsType<CashInventoryDto>(Assert.IsType<CreatedResult>(result).Value);
            Assert.Equal(scenario == "renamed" ? 100m : 0m, inventory.AccountingBalance);
            Assert.Null(inventory.CorrectionCashOrderId);
        }
        Assert.Single(await db.CashOrders.ToListAsync());
        Assert.Equal(100m, (await db.CashOrders.SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CashTransfer_AutomaticOrderUsesCashAccountIdentity(bool intoCash)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Company = company, IsActive = true, Currency = "RUB" };
        var bank = new MoneyAccount { Name = "Банк", Source = MoneyOperationSource.Bank, Company = company, IsActive = true, Currency = "RUB" };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(cash, bank);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) };
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.CreateTransfer(new MoneyOperationTransferRequest
        {
            SourceMoneyAccountId = intoCash ? bank.Id : cash.Id, TargetMoneyAccountId = intoCash ? cash.Id : bank.Id,
            Amount = 100m, Currency = "RUB", Date = new DateTime(2026, 10, 28),
        });
        Assert.IsType<CreatedResult>(result.Result);
        var order = Assert.Single(await db.CashOrders.ToListAsync());
        Assert.Equal(cash.Id, order.MoneyAccountId);
        Assert.Equal(company.Id, order.CompanyId);
        Assert.Equal(intoCash ? CashOrderType.Incoming : CashOrderType.Outgoing, order.Type);
        Assert.Equal(2, await db.MoneyOperations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualMoneyOperation_SyncsCashIdentityOnCreateAndExplicitAccountChange(bool historical)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var first = new MoneyAccount { Name = "Касса 1", Source = MoneyOperationSource.Cash, Company = company, IsActive = true, Currency = "RUB" };
        var second = new MoneyAccount { Name = "Касса 2", Source = MoneyOperationSource.Cash, Company = company, IsActive = true, Currency = "RUB" };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(first, second);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) };
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = http } };
        var created = Assert.IsType<CreatedResult>(await controller.Create(new MoneyOperation
        {
            MoneyAccountId = first.Id, Source = MoneyOperationSource.Cash, Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact, Amount = 100m, Currency = "RUB", Date = new DateTime(2026, 10, 28),
        }));
        var operation = Assert.IsType<MoneyOperation>(created.Value);
        var cashOrder = await db.CashOrders.SingleAsync();
        Assert.Equal(first.Id, cashOrder.MoneyAccountId);
        Assert.Equal(company.Id, cashOrder.CompanyId);
        if (historical)
        {
            cashOrder.MoneyAccountId = null;
            cashOrder.CompanyId = null;
            await db.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await controller.Update(operation.Id, new MoneyOperation
        {
            MoneyAccountId = second.Id, Source = MoneyOperationSource.Cash, Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact, Amount = 100m, Currency = "RUB", Date = new DateTime(2026, 10, 28),
        }));
        db.ChangeTracker.Clear();
        var updated = await db.CashOrders.SingleAsync();
        Assert.Equal(cashOrder.Id, updated.Id);
        Assert.Equal(historical ? null : second.Id, updated.MoneyAccountId);
        Assert.Equal(historical ? null : company.Id, updated.CompanyId);
        if (!historical)
        {
            updated.MoneyAccountId = first.Id;
            await db.SaveChangesAsync();
            var rejected = await controller.Update(operation.Id, new MoneyOperation
            {
                MoneyAccountId = second.Id, Source = MoneyOperationSource.Cash, Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact, Amount = 150m, Currency = "RUB", Date = new DateTime(2026, 10, 28),
            });
            Assert.IsType<ConflictObjectResult>(rejected);
            db.ChangeTracker.Clear();
            Assert.Equal(100m, (await db.MoneyOperations.SingleAsync()).Amount);
            Assert.Equal(first.Id, (await db.CashOrders.SingleAsync()).MoneyAccountId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountWithInventory_IsArchivedAndKeepsInventory(bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, IsActive = true, Company = company };
        db.Users.Add(user);
        db.MoneyAccounts.Add(cash);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) };
        var cashController = new CashOrdersController(db) { ControllerContext = new ControllerContext { HttpContext = httpContext } };
        var result = await cashController.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = cash.Name, MoneyAccountId = cash.Id, Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);
        Assert.IsType<CreatedResult>(result);
        var accountController = new MoneyAccountsController(db) { ControllerContext = new ControllerContext { HttpContext = httpContext } };
        Assert.IsType<NoContentResult>(await accountController.Delete(cash.Id, CancellationToken.None));
        var persisted = await db.MoneyAccounts.SingleAsync();
        Assert.False(persisted.IsActive);
        Assert.Equal(cash.Id, (await db.CashInventories.SingleAsync()).MoneyAccountId);
        Assert.Equal(createCorrection ? 1 : 0, await db.CashOrders.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PaymentCompany_IsCheckedWhenNoCashMovementExists(bool sameCompany, bool withMovement)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        var company = new LegalEntity { ShortName = "Компания кассы" };
        var other = new LegalEntity { ShortName = "Другая компания" };
        var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Company = company, IsActive = true };
        db.Users.Add(user);
        db.LegalEntities.Add(other);
        db.MoneyAccounts.Add(cash);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id, other.Id }) });
        var payment = new Payment { Amount = 50m, SellerCompanyId = sameCompany ? company.Id : other.Id, Date = new DateTime(2026, 10, 27), PlannedDate = new DateTime(2026, 10, 27) };
        var movement = withMovement ? new MoneyOperation { Source = MoneyOperationSource.Cash, MoneyAccountId = cash.Id, Amount = 50m, Currency = "RUB", Date = payment.Date } : null;
        db.CashOrders.Add(new CashOrder { Number = "SYN-2", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = payment.Date, Account = cash.Name, Amount = 50m, Currency = "RUB", Payment = payment, MoneyOperation = movement,
            MoneyAccountId = cash.Id, CompanyId = company.Id });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } },
        };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = cash.Name, Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = true,
        }, CancellationToken.None);
        if (!sameCompany && !withMovement)
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashInventories.ToListAsync());
            Assert.Single(await db.CashOrders.ToListAsync());
        }
        else
        {
            Assert.IsType<CreatedResult>(result);
            Assert.Single(await db.CashInventories.ToListAsync());
            Assert.Equal(2, await db.CashOrders.CountAsync());
        }
        Assert.Equal(sameCompany ? company.Id : other.Id, (await db.Payments.SingleAsync()).SellerCompanyId);
        Assert.Equal(50m, (await db.Payments.SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public async Task LinkedMovementAccount_MustMatchInventoryCashAccount(bool matches, bool createCorrection, bool included)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var ownCash = new MoneyAccount { Name = "Основная касса", Source = MoneyOperationSource.Cash, IsActive = true, Company = company };
        var otherCash = new MoneyAccount { Name = "Другая касса", Source = MoneyOperationSource.Cash, IsActive = true, Company = company };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(ownCash, otherCash);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var movement = new MoneyOperation { Source = MoneyOperationSource.Cash, MoneyAccountId = matches ? ownCash.Id : otherCash.Id, Amount = 50m, Currency = "RUB", Date = new DateTime(2026, 10, included ? 27 : 29) };
        db.CashOrders.Add(new CashOrder { Number = "SYN-1", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = movement.Date, Account = ownCash.Name, Amount = 50m, Currency = "RUB", MoneyOperation = movement,
            MoneyAccountId = ownCash.Id, CompanyId = company.Id });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } },
        };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = ownCash.Name, Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);
        if (matches || !included)
        {
            Assert.IsType<CreatedResult>(result);
            Assert.Single(await db.CashInventories.ToListAsync());
            Assert.Equal(createCorrection ? 2 : 1, await db.CashOrders.CountAsync());
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashInventories.ToListAsync());
            Assert.Single(await db.CashOrders.ToListAsync());
        }
        Assert.Equal(matches ? ownCash.Id : otherCash.Id, (await db.MoneyOperations.SingleAsync()).MoneyAccountId);
    }

    [Theory]
    [InlineData("inactive-account", true)]
    [InlineData("archived-company", false)]
    [InlineData("foreign-company", true)]
    [InlineData("unknown-company", false)]
    public async Task AdminCannotInventoryUnusableOrUnownedCashAccount(string scenario, bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        var company = new LegalEntity { ShortName = "Синтетическая компания", ProfileStatus = scenario == "archived-company" ? CompanyProfileStatuses.Archived : CompanyProfileStatuses.Active };
        var other = new LegalEntity { ShortName = "Не собственная компания" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(company, other);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        db.MoneyAccounts.Add(new MoneyAccount
        {
            Name = "Касса", Source = MoneyOperationSource.Cash, IsActive = scenario != "inactive-account",
            CompanyId = scenario == "unknown-company" ? null : scenario == "foreign-company" ? other.Id : company.Id,
        });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } },
        };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = "Касса", Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
    }

    [Theory]
    [InlineData("matching", true)]
    [InlineData("matching", false)]
    [InlineData("omitted", true)]
    [InlineData("omitted", false)]
    [InlineData("wrong", true)]
    [InlineData("invalid", true)]
    public async Task ConfirmedCashAccount_ValidatesOptionalIdentity(string identity, bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        db.Users.Add(user);
        var company = new LegalEntity { ShortName = "Синтетическая компания" };
        var cash = new MoneyAccount
        {
            Name = "Касса", Source = MoneyOperationSource.Cash, IsActive = true,
            Company = company,
        };
        db.MoneyAccounts.Add(cash);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } },
        };
        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = "Касса", MoneyAccountId = identity == "omitted" ? null : identity == "matching" ? cash.Id : identity == "invalid" ? 0 : cash.Id + 1000,
            Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);

        if (identity is "matching" or "omitted")
        {
            Assert.IsType<CreatedResult>(result);
            var saved = Assert.Single(await db.CashInventories.ToListAsync());
            Assert.Equal(cash.Id, saved.MoneyAccountId);
            Assert.Equal(company.Id, saved.CompanyId);
            var orders = await db.CashOrders.ToListAsync();
            if (createCorrection)
            {
                var order = Assert.Single(orders);
                Assert.Equal(100m, order.Amount);
                Assert.Equal("RUB", order.Currency);
                Assert.Equal(CashOrderStatus.Posted, order.Status);
                Assert.Equal(cash.Id, order.MoneyAccountId);
                Assert.Equal(company.Id, order.CompanyId);
            }
            else Assert.Empty(orders);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.CashInventories.ToListAsync());
            Assert.Empty(await db.CashOrders.ToListAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCashAccount_DoesNotSaveInventoryOrCorrection(bool createCorrection)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "synthetic-admin@fixture.local", FullName = "Synthetic administrator", PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic administrator" }, IsActive = true, IsApproved = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var controller = new CashOrdersController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin") }, "Test")) } },
        };

        var result = await controller.CreateInventory(new CashInventoryCreateRequest
        {
            AccountName = "Несуществующая касса", Date = new DateTime(2026, 10, 28), ActualBalance = 100m, CreateCorrection = createCorrection,
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.CashInventories.ToListAsync());
        Assert.Empty(await db.CashOrders.ToListAsync());
    }
}

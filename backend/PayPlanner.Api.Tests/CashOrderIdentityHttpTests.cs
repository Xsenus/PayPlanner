using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashOrderIdentityHttpTests
{
    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    [Fact]
    public async Task ManualOrder_HttpPipelineEnforcesIdentityAndSupportsPostingAndInventory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "fixture-employee", Description = "Synthetic" };
        var user = new User { Email = "http-fixture@fixture.local", FullName = "Synthetic", PasswordHash = "hash", Role = role,
            IsEmployee = true, IsActive = true, IsApproved = true };
        var company = new LegalEntity { ShortName = "Своя компания" };
        var foreignCompany = new LegalEntity { ShortName = "Другая компания" };
        var cash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = company, IsActive = true };
        var foreignCash = new MoneyAccount { Name = "Касса", Source = MoneyOperationSource.Cash, Currency = "RUB", Company = foreignCompany, IsActive = true };
        db.Users.Add(user);
        db.MoneyAccounts.AddRange(cash, foreignCash);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id, foreignCompany.Id }) });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = company.Id });
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments,
            CanView = true, CanCreate = true, CanEdit = true, CanExport = true, ViewScope = DataVisibilityScope.All });
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services => {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CashOrdersController).Assembly)
                    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        var payload = new { number = "SYN-MANUAL", type = "Incoming", status = "Draft", date = "2026-10-28", amount = 100, currency = "RUB",
            selectedMoneyAccountId = cash.Id, account = "Подмена", moneyAccountId = foreignCash.Id, companyId = foreignCompany.Id };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/cash-orders", payload)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString());
        var denied = await client.PostAsJsonAsync("/api/cash-orders", new { type = "Incoming", status = "Draft", amount = 100,
            currency = "RUB", selectedMoneyAccountId = foreignCash.Id });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Empty(await db.CashOrders.ToListAsync());
        var response = await client.PostAsJsonAsync("/api/cash-orders", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("id").GetInt32();
        Assert.True(json.RootElement.GetProperty("hasLinkedCashAccount").GetBoolean());
        Assert.Equal("Касса", json.RootElement.GetProperty("account").GetString());
        Assert.Equal(cash.Id, (await db.CashOrders.SingleAsync()).MoneyAccountId);
        Assert.Equal(company.Id, (await db.CashOrders.SingleAsync()).CompanyId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/cash-orders/{id}/post", null)).StatusCode);
        // A same-named unassigned order cannot be attributed to either company by guesswork.
        var unassigned = new CashOrder { Number = "SYN-UNASSIGNED", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 27), Amount = 50m, Currency = "RUB", Account = "Касса" };
        db.CashOrders.Add(unassigned);
        await db.SaveChangesAsync();
        var inventory = await client.PostAsJsonAsync("/api/cash-orders/inventories", new { accountName = "Касса", moneyAccountId = cash.Id,
            date = "2026-10-28", actualBalance = 100, createCorrection = false });
        Assert.Equal(HttpStatusCode.BadRequest, inventory.StatusCode);
        Assert.Empty(await db.CashInventories.ToListAsync());
        unassigned.Status = CashOrderStatus.Draft;
        await db.SaveChangesAsync();
        var confirmedInventory = await client.PostAsJsonAsync("/api/cash-orders/inventories", new { accountName = "Касса", moneyAccountId = cash.Id,
            date = "2026-10-28", actualBalance = 100, createCorrection = false });
        Assert.Equal(HttpStatusCode.Created, confirmedInventory.StatusCode);
        var firstInventory = await db.CashInventories.SingleAsync();
        Assert.Equal(100m, firstInventory.AccountingBalance);
        firstInventory.AccountName = "Прежнее название";
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = foreignCompany.Id });
        await db.SaveChangesAsync();
        db.CashInventories.Add(new CashInventory { Number = "SYN-OTHER", Date = new DateTime(2026, 10, 28), AccountName = "Касса",
            MoneyAccountId = foreignCash.Id, CompanyId = foreignCompany.Id, AccountingBalance = 900m, ActualBalance = 900m });
        await db.SaveChangesAsync();
        var historyResponse = await client.GetAsync($"/api/cash-orders/inventories?moneyAccountId={cash.Id}&accountName=%D0%9A%D0%B0%D1%81%D1%81%D0%B0");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Equal(cash.Id, history.RootElement.GetProperty("moneyAccountId").GetInt32());
        var histories = history.RootElement.GetProperty("items");
        Assert.Equal(1, histories.GetArrayLength());
        Assert.Equal(firstInventory.Id, histories[0].GetProperty("id").GetInt32());
        var ownOrder = await db.CashOrders.SingleAsync(item => item.Id == id);
        ownOrder.Account = "Прежнее название";
        db.CashOrders.Add(new CashOrder { Number = "SYN-FOREIGN-ORDER", Type = CashOrderType.Incoming, Status = CashOrderStatus.Posted,
            Date = new DateTime(2026, 10, 28), Amount = 900m, Currency = "RUB", Account = "Касса",
            MoneyAccountId = foreignCash.Id, CompanyId = foreignCompany.Id });
        await db.SaveChangesAsync();
        var query = $"from=2026-10-28&to=2026-10-28&moneyAccountId={cash.Id}&accountName=%D0%9A%D0%B0%D1%81%D1%81%D0%B0";
        var registerResponse = await client.GetAsync($"/api/cash-orders/register?{query}");
        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        using var register = JsonDocument.Parse(await registerResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, register.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(100m, register.RootElement.GetProperty("incomingAmount").GetDecimal());
        Assert.Equal(id, register.RootElement.GetProperty("items")[0].GetProperty("id").GetInt32());
        Assert.Equal(cash.Id, register.RootElement.GetProperty("moneyAccountId").GetInt32());
        var pageResponse = await client.GetAsync($"/api/cash-orders/paged?{query}");
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
        Assert.Equal(id, Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        var bookResponse = await client.GetAsync($"/api/cash-orders/cashbook?{query}");
        Assert.Equal(HttpStatusCode.OK, bookResponse.StatusCode);
        using var book = JsonDocument.Parse(await bookResponse.Content.ReadAsStringAsync());
        Assert.Equal(100m, book.RootElement.GetProperty("income").GetDecimal());
        Assert.Equal(cash.Id, book.RootElement.GetProperty("moneyAccountId").GetInt32());
        var registerPrint = await client.GetAsync($"/api/cash-orders/register/print?{query}");
        Assert.Equal(HttpStatusCode.OK, registerPrint.StatusCode);
        var registerHtml = await registerPrint.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SYN-FOREIGN-ORDER", registerHtml);
        Assert.Contains("SYN-MANUAL", registerHtml);
        Assert.Contains("Только ордера с подтверждённой кассой", registerHtml);
        Assert.Contains($"<meta name=\"payplanner-account-id\" content=\"{cash.Id}\">", registerHtml);
        var bookPrint = await client.GetAsync($"/api/cash-orders/cashbook/print?{query}");
        Assert.Equal(HttpStatusCode.OK, bookPrint.StatusCode);
        var bookHtml = await bookPrint.Content.ReadAsStringAsync();
        Assert.Contains("Только ордера с подтверждённой кассой", bookHtml);
        Assert.Contains($"<meta name=\"payplanner-account-id\" content=\"{cash.Id}\">", bookHtml);
        db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync(item => item.UserId == user.Id && item.LegalEntityId == foreignCompany.Id));
        await db.SaveChangesAsync();
        var foreignHistoryResponse = await client.GetAsync($"/api/cash-orders/inventories?moneyAccountId={foreignCash.Id}");
        Assert.Equal(HttpStatusCode.NotFound, foreignHistoryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cash-orders/register?moneyAccountId={foreignCash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cash-orders/paged?moneyAccountId={foreignCash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cash-orders/cashbook?moneyAccountId={foreignCash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cash-orders/register/print?moneyAccountId={foreignCash.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/cash-orders/cashbook/print?moneyAccountId={foreignCash.Id}")).StatusCode);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestPaymentHttpTests
{
    [Fact]
    public async Task Http_SelectInvoiceAndCompleteOnlyAfterFullReceipt()
    {
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var admin = new User { Email = "http-request@example.test", FullName = "Администратор", PasswordHash = "x", Role = new Role { Name = "admin" }, IsActive = true, IsApproved = true };
        var outsider = new User { Email = "http-request-outsider@example.test", FullName = "Другой", PasswordHash = "x", Role = new Role { Name = "lawyer" }, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Клиент", Phone = "+79000000011" };
        var company = new LegalEntity { ShortName = "Компания" };
        db.AddRange(admin, outsider, client, company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        var invoice = new Invoice { Number = "HTTP-1", Date = new DateTime(2026, 1, 1) };
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Currency = "RUB", Name = "Счёт" };
        var payment = new Payment { Invoice = invoice, ClientId = client.Id, SellerCompanyId = company.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
        db.AddRange(account, payment);
        await db.SaveChangesAsync();
        var receipt = new MoneyOperation { LinkedInvoiceId = invoice.Id, MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 50m, ResponsibleUserId = admin.Id };
        db.MoneyOperations.Add(receipt);
        await db.SaveChangesAsync();
        var service = new SalesRequestService(db);
        var request = (await service.CreateAsync(new SalesRequestCreateInput("Call", client.Id, null, null, null, null, null, "Обращение", null, null, null), admin.Id, default)).Value!;
        await db.TaskJournalItems.Where(task => task.SalesRequestId == request.Id).ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Status, "done"));
        var paid = await db.SalesRequestStatuses.Where(status => status.Key == "paid").Select(status => status.Id).SingleAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db); services.AddSingleton<ISalesRequestService>(service);
                services.AddAuthentication("request-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("request-fixture", _ => { });
                services.AddAuthorization(); services.AddControllers().AddApplicationPart(typeof(SalesRequestsController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var http = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/requests/{request.Id}/service-invoice")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-User", outsider.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/requests/{request.Id}/service-invoice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/api/requests/{request.Id}/invoice-candidates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PutAsJsonAsync($"/api/requests/{request.Id}/service-invoice", new { version = request.Version, invoiceId = invoice.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync($"/api/requests/{request.Id}/transition", new { toStatusId = paid, version = request.Version })).StatusCode);
        http.DefaultRequestHeaders.Remove("X-Fixture-User");
        http.DefaultRequestHeaders.Add("X-Fixture-User", admin.Id.ToString(CultureInfo.InvariantCulture));
        using var candidates = await http.GetAsync($"/api/requests/{request.Id}/invoice-candidates?search=HTTP");
        Assert.Equal(HttpStatusCode.OK, candidates.StatusCode);
        using var list = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync());
        Assert.Equal(invoice.Id, list.RootElement.GetProperty("items")[0].GetProperty("invoiceId").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"/api/requests/{request.Id}/service-invoice", new { version = request.Version })).StatusCode);
        using var bind = await http.PutAsJsonAsync($"/api/requests/{request.Id}/service-invoice", new { version = request.Version, invoiceId = invoice.Id });
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
        using var binding = JsonDocument.Parse(await bind.Content.ReadAsStringAsync());
        var version = binding.RootElement.GetProperty("version").GetInt32();
        using var linked = await http.GetAsync($"/api/requests/{request.Id}/service-invoice");
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using var linkedJson = JsonDocument.Parse(await linked.Content.ReadAsStringAsync());
        Assert.Equal(invoice.Id, linkedJson.RootElement.GetProperty("invoice").GetProperty("invoiceId").GetInt32());
        using var partial = await http.PostAsJsonAsync($"/api/requests/{request.Id}/transition", new { toStatusId = paid, version });
        Assert.Equal(HttpStatusCode.Conflict, partial.StatusCode);
        Assert.Null((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
        receipt.Amount = 100m;
        await db.SaveChangesAsync();
        using var full = await http.PostAsJsonAsync($"/api/requests/{request.Id}/transition", new { toStatusId = paid, version });
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.NotNull((await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id)).ResultClientCaseId);
        using var repeat = await http.PostAsJsonAsync($"/api/requests/{request.Id}/transition", new { toStatusId = paid, version });
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(1, await db.ClientCases.CountAsync());
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture))], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

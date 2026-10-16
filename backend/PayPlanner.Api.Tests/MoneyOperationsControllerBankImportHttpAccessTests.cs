using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData("missing-company", HttpStatusCode.BadRequest)]
    [InlineData("missing-account", HttpStatusCode.BadRequest)]
    [InlineData("foreign-company", HttpStatusCode.Forbidden)]
    [InlineData("not-own-company", HttpStatusCode.BadRequest)]
    [InlineData("unknown-account", HttpStatusCode.NotFound)]
    public async Task BankImportAccess_HttpUploadRejectsUntrustedContextBeforeCreatingMoney(
        string scenario,
        HttpStatusCode expectedStatus)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "bank-import-http@example.test", "Импорт своей компании");
        user.IsEmployee = true;
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.All,
        });
        var ownCompany = new LegalEntity { ShortName = "Своя синтетическая компания" };
        var foreignCompany = new LegalEntity { ShortName = "Чужая синтетическая компания" };
        var counterparty = new LegalEntity { ShortName = "Сторонний синтетический контрагент" };
        db.LegalEntities.AddRange(ownCompany, foreignCompany, counterparty);
        var account = new MoneyAccount
        {
            Name = "Синтетический банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
        };
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = "legalEntities.mainCompanyIds",
                Value = JsonSerializer.Serialize(new[] { ownCompany.Id, foreignCompany.Id }),
            },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = ownCompany.Id });
        await db.SaveChangesAsync();
        var before = await CaptureBankImportStateAsync(db);

        using var server = new TestServer(new WebHostBuilder()
            .UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false")
            .UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddAuthentication("bank-import-fixture")
                    .AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(
            "Дата;Сумма;Контрагент;Назначение;Номер\n" +
            "01.10.2026;-123;Синтетический поставщик;Синтетический расход;101\n")), "file", "synthetic-statement.csv");
        if (scenario != "missing-company")
        {
            var companyId = scenario switch
            {
                "foreign-company" => foreignCompany.Id,
                "not-own-company" => counterparty.Id,
                _ => ownCompany.Id,
            };
            form.Add(new StringContent(companyId.ToString(CultureInfo.InvariantCulture)), "companyId");
        }
        if (scenario != "missing-account")
        {
            var accountId = scenario == "unknown-account" ? account.Id + 10_000 : account.Id;
            form.Add(new StringContent(accountId.ToString(CultureInfo.InvariantCulture)), "moneyAccountId");
        }

        using var response = await client.PostAsync("/api/money-operations/bank-statement/import", form);

        Assert.Equal(before, await CaptureBankImportStateAsync(db));
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(foreignCompany.ShortName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(counterparty.ShortName, body, StringComparison.Ordinal);
    }

    private sealed class BankImportFixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var userId) || userId <= 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            // A stale admin claim must not bypass the role and company grants in DB.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, "admin"),
            }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

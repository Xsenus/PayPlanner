using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData(false, "nested-package")]
    [InlineData(true, "nested-package")]
    [InlineData(false, "foreign-package-id")]
    [InlineData(true, "foreign-package-id")]
    [InlineData(false, "nested-account")]
    [InlineData(true, "nested-account")]
    public async Task BankPackageInput_ManualHttpCreateCannotForgeImportOriginOrPrivilegedAccount(bool postgres, string attack)
    {
        await using var fixture = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var seed = await SeedBankScopeAsync(db, "foreign-company");
        var packageBefore = await CaptureBankScopeStateAsync(db);
        var accountsBefore = await db.MoneyAccounts.AsNoTracking().OrderBy(account => account.Id)
            .Select(account => new { account.Id, account.Name, account.CompanyId, account.BankConnectionAccountId }).ToArrayAsync();
        var ownAccount = await db.MoneyAccounts.AsNoTracking().SingleAsync(account => account.CompanyId != seed.Batch.CompanyId);
        var payload = new Dictionary<string, object?>
        {
            ["source"] = (int)MoneyOperationSource.Bank,
            ["direction"] = (int)MoneyOperationDirection.Expense,
            ["amount"] = 17m,
            ["date"] = BankScopeDate,
            ["accountName"] = ownAccount.Name,
            ["moneyAccountId"] = ownAccount.Id,
            ["currency"] = ownAccount.Currency,
            ["responsibleUserId"] = seed.ActorId,
            // Protected JSON fields must not become trusted import identity.
            ["sourceMovementKey"] = "forged-http-movement-key",
        };
        if (attack == "nested-package")
            payload["bankStatementImportBatch"] = new
            {
                companyId = seed.Batch.CompanyId,
                moneyAccountId = seed.Batch.MoneyAccountId,
                source = BankStatementImportSources.BackgroundBank,
                trigger = BankStatementImportTriggers.Background,
                importedByUserId = seed.ActorId + 10_000,
                responsibleUserId = seed.ActorId,
                fileName = "forged-http.csv",
                fileHash = BankScopeKey("forged-http"),
                packageKey = BankScopeKey("forged-package"),
            };
        else if (attack == "foreign-package-id") payload["bankStatementImportBatchId"] = seed.Batch.Id;
        else payload["moneyAccount"] = new
        {
            name = "Незаконно созданный вложенный счёт",
            source = (int)MoneyOperationSource.Bank,
            currency = "RUB",
            companyId = seed.Batch.CompanyId,
        };

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
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly)
                    .AddJsonOptions(options => options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", seed.ActorId.ToString(CultureInfo.InvariantCulture));
        using var response = await client.PostAsJsonAsync("/api/money-operations", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(accountsBefore, await db.MoneyAccounts.AsNoTracking().OrderBy(account => account.Id)
            .Select(account => new { account.Id, account.Name, account.CompanyId, account.BankConnectionAccountId }).ToArrayAsync());
        var manual = await db.MoneyOperations.SingleAsync(operation => operation.Amount == 17m);
        Assert.Null(manual.BankStatementImportBatchId);
        Assert.Null(manual.SourceMovementKey);
        Assert.Equal(ownAccount.Id, manual.MoneyAccountId);
        Assert.Equal(ownAccount.Name, manual.AccountName);
        Assert.Equal(ownAccount.Source, manual.Source);
        Assert.Equal(ownAccount.Currency, manual.Currency);
        Assert.Equal(packageBefore, await CaptureBankScopeStateAsync(db, excludedManualOperationId: manual.Id));
    }
}

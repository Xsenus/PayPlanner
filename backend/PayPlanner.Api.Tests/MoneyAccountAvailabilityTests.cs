using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountAvailability_HttpKeepsComputedFlagsAcrossReadCreateAndUpdate(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: true);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        using var get = await client.GetAsync("/api/money-accounts?activeOnly=false");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var list = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var own = list.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == fixture.Seed.Own.Id);
        var unknown = list.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == fixture.Seed.Unknown.Id);
        Assert.True(own.GetProperty("canUseForNewOperations").GetBoolean());
        Assert.True(own.GetProperty("canEditExistingOperations").GetBoolean());
        Assert.False(unknown.GetProperty("canUseForNewOperations").GetBoolean());
        Assert.False(unknown.GetProperty("canEditExistingOperations").GetBoolean());

        object Body(string name) => new
        {
            companyId = fixture.Seed.Company.Id,
            name,
            source = (int)MoneyOperationSource.Cash,
            currency = "RUB",
            isActive = false,
            isDefault = false,
            canUseForNewOperations = true,
            canEditExistingOperations = true,
        };
        using var createBody = AccountJson(Body("SYNTHETIC-INACTIVE-HTTP-ACCOUNT"));
        using var create = await client.PostAsync("/api/money-accounts", createBody);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetInt32();
        Assert.False(created.RootElement.GetProperty("canUseForNewOperations").GetBoolean());
        Assert.False(created.RootElement.GetProperty("canEditExistingOperations").GetBoolean());
        Assert.False(created.RootElement.GetProperty("isActive").GetBoolean());

        using var updateBody = AccountJson(Body("SYNTHETIC-INACTIVE-HTTP-RENAMED"));
        using var update = await client.PutAsync($"/api/money-accounts/{id}", updateBody);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.Equal(id, updated.RootElement.GetProperty("id").GetInt32());
        Assert.False(updated.RootElement.GetProperty("canUseForNewOperations").GetBoolean());
        Assert.False(updated.RootElement.GetProperty("canEditExistingOperations").GetBoolean());
        await using var verify = fixture.Open();
        var stored = await verify.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.False(stored.IsActive);
        Assert.Equal("SYNTHETIC-INACTIVE-HTTP-RENAMED", stored.Name);
    }

    public static IEnumerable<object[]> AccountAvailabilityCases =>
        from postgres in new[] { false, true }
        from state in new[] { "active", "inactive", "hidden-bank", "closed-bank", "archived-company", "unknown-admin", "unknown-employee", "incoherent-admin" }
        from endpoint in new[] { "list", "detail" }
        select new object[] { postgres, state, endpoint };

    [Theory]
    [MemberData(nameof(AccountAvailabilityCases))]
    public async Task AccountAvailability_HistoryKeepsIdentityAndSeparatesNewOperations(bool postgres, string state, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: state.EndsWith("-admin", StringComparison.Ordinal));
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var target = state.StartsWith("unknown-", StringComparison.Ordinal) ? seed.Unknown
            : state == "incoherent-admin" ? seed.Incoherent : seed.Own;
        if (state == "inactive")
            await db.MoneyAccounts.Where(item => item.Id == target.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (state == "hidden-bank")
            await db.BankConnectionAccounts.Where(item => item.Id == target.BankConnectionAccountId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsHidden, true));
        if (state == "closed-bank")
            await db.BankConnectionAccounts.Where(item => item.Id == target.BankConnectionAccountId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, BankAccountStatuses.Closed));
        if (state == "archived-company")
            await db.LegalEntities.Where(item => item.Id == seed.Company.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProfileStatus, CompanyProfileStatuses.Archived));
        var before = await CaptureAccountScopeAsync(db);
        var readable = state != "unknown-employee";
        MoneyAccount? account;
        if (endpoint == "list")
        {
            var result = await CreateMoneyAccountsController(db, seed.Actor.Id).Get(activeOnly: false);
            var items = Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>(result.Result).Value);
            account = items.SingleOrDefault(item => item.Id == target.Id);
            Assert.Equal(readable, account is not null);
            var activeResult = await CreateMoneyAccountsController(db, seed.Actor.Id).Get(activeOnly: true);
            var active = Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>(activeResult.Result).Value);
            Assert.Equal(state == "active", active.Any(item => item.Id == target.Id));
        }
        else
        {
            var result = await CreateMoneyAccountsController(db, seed.Actor.Id).GetById(target.Id, default);
            if (!readable)
            {
                Assert.IsType<NotFoundResult>(result.Result);
                account = null;
            }
            else account = Assert.IsType<MoneyAccount>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }
        if (account is not null)
        {
            Assert.Equal(target.Id, account.Id);
            Assert.Equal(target.CompanyId, account.CompanyId);
            Assert.Equal(target.Name, account.Name);
            Assert.Equal(target.Currency, account.Currency);
            Assert.Equal(state == "active", account.CanUseForNewOperations);
            Assert.Equal(state is "active" or "hidden-bank" or "closed-bank", account.CanEditExistingOperations);
        }
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureAccountScopeAsync(verify));
    }

    [Fact]
    public void AccountAvailability_ComputedFlagsCannotBeAssignedByClientJson()
    {
        var account = JsonSerializer.Deserialize<MoneyAccount>(
            "{\"canUseForNewOperations\":true,\"canEditExistingOperations\":true}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(account);
        Assert.False(account.CanUseForNewOperations);
        Assert.False(account.CanEditExistingOperations);
    }

    private sealed class AccountLookupCommandCounter : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Reads++;
            return ValueTask.FromResult(result);
        }
    }

    public static IEnumerable<object[]> AccountHistoryMutationCases =>
        from postgres in new[] { false, true }
        from state in new[] { "active", "inactive", "closed-bank", "archived-company" }
        select new object[] { postgres, state };

    [Theory]
    [MemberData(nameof(AccountHistoryMutationCases))]
    public async Task AccountAvailability_AnnotationUsesTheSameExistingAccountState(bool postgres, string state)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareManualCompanyAsync(db, seed);
        var operation = ManualCompanyOperation(seed, seed.Own.Id);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        if (state == "inactive")
            await db.MoneyAccounts.Where(item => item.Id == seed.Own.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (state == "closed-bank")
            await db.BankConnectionAccounts.Where(item => item.Id == seed.Own.BankConnectionAccountId)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, BankAccountStatuses.Closed));
        if (state == "archived-company")
            await db.LegalEntities.Where(item => item.Id == seed.Company.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.ProfileStatus, CompanyProfileStatuses.Archived));
        var before = await CaptureInvoiceScopeAsync(db);
        var financialBefore = await CaptureHistoricalInvoiceMoneyAsync(db);
        var detail = await CreateController(db, seed.Actor.Id).GetById(operation.Id, default);
        var model = Assert.IsType<MoneyOperation>(Assert.IsType<OkObjectResult>(detail).Value);
        model.Purpose = "SYNTHETIC-ACCOUNT-HISTORY-ANNOTATION";
        var result = await CreateController(db, seed.Actor.Id).Update(operation.Id, model);
        await using var verify = fixture.Open();
        if (state is "active" or "closed-bank")
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(model.Purpose, await verify.MoneyOperations.Where(item => item.Id == operation.Id).Select(item => item.Purpose).SingleAsync());
            Assert.Equal(financialBefore, await CaptureHistoricalInvoiceMoneyAsync(verify));
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(before, await CaptureInvoiceScopeAsync(verify));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountAvailability_AdditionalAccountsDoNotAddDatabaseRoundTrips(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        var counter = new AccountLookupCommandCounter();
        await using var db = fixture.Open(counter);
        await CreateMoneyAccountsController(db, fixture.Seed.Actor.Id).Get(activeOnly: false);
        var initialReads = counter.Reads;
        for (var index = 0; index < 30; index++)
            db.MoneyAccounts.Add(new MoneyAccount
            {
                CompanyId = fixture.Seed.Company.Id,
                Name = "SYNTHETIC-HISTORY-LOOKUP-" + index,
                Source = MoneyOperationSource.Cash,
                Currency = "RUB",
                IsActive = true,
            });
        await db.SaveChangesAsync();
        counter.Reads = 0;
        var result = await CreateMoneyAccountsController(db, fixture.Seed.Actor.Id).Get(activeOnly: false);
        var items = Assert.IsAssignableFrom<IReadOnlyList<MoneyAccount>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(31, items.Count);
        Assert.All(items, item => { Assert.True(item.CanUseForNewOperations); Assert.True(item.CanEditExistingOperations); });
        Assert.InRange(counter.Reads, 1, initialReads + 1);
        Assert.Null(db.Model.FindEntityType(typeof(MoneyAccount))!.FindProperty(nameof(MoneyAccount.CanUseForNewOperations)));
        Assert.Null(db.Model.FindEntityType(typeof(MoneyAccount))!.FindProperty(nameof(MoneyAccount.CanEditExistingOperations)));
    }
}

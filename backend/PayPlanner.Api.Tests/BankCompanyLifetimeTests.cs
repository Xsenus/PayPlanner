using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    public static IEnumerable<object[]> FileCompanyLifetimeCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var admin in new[] { false, true })
                foreach (var deleted in new[] { false, true })
                    yield return [postgres, admin, deleted];
    }

    [Theory]
    [MemberData(nameof(FileCompanyLifetimeCases))]
    public async Task BankFile_RequiresExistingCompanyBeforeCreatingPackage(bool postgres, bool admin, bool deleted)
    {
        await using var fixture = await CreateModelDatabaseAsync(postgres);
        var db = fixture.Db;
        var actor = await CreateUserAsync(db, "company-lifetime@example.test", "Синтетический оператор",
            roleName: admin ? "admin" : "company-lifetime");
        var controller = await CreateBankImportControllerAsync(db, actor.Id, isAdminClaim: admin);
        var account = await db.MoneyAccounts.AsNoTracking().SingleAsync();
        if (deleted)
            await db.LegalEntities.Where(company => company.Id == account.CompanyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => EF.Property<bool>(company, "IsDeleted"), true));
        db.ChangeTracker.Clear();
        var before = await CaptureBankScopeStateAsync(db);
        var action = await controller.ImportBankStatement(CreateFormFile(
            "Дата;Сумма;Контрагент;Назначение;Номер\n16.10.2026;55;Синтетический контрагент;Синтетическая операция;1\n",
            "company-lifetime.csv"), CancellationToken.None);
        if (deleted)
        {
            var status = action.Result switch
            {
                ForbidResult => 403,
                StatusCodeResult result => result.StatusCode,
                ObjectResult result => result.StatusCode ?? 200,
                _ => throw new InvalidOperationException("Unexpected import response"),
            };
            Assert.Contains(status, new[] { 400, 403, 404 });
            Assert.Equal(before, await CaptureBankScopeStateAsync(db));
        }
        else
        {
            var result = Assert.IsType<BankStatementImportResultDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
            Assert.Equal(1, result.Created);
            Assert.NotNull(result.BatchId);
            Assert.Equal(account.Id, (await db.MoneyOperations.SingleAsync()).MoneyAccountId);
        }
        Assert.Equal(1, await db.MoneyAccounts.CountAsync());
    }
}

public partial class BankSyncServiceTests
{
    public static IEnumerable<object[]> NativeCompanyDisappearanceCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var scheduled in new[] { false, true })
                foreach (var beforePage in new[] { 0, 1 })
                    yield return [postgres, scheduled, beforePage];
    }

    [Theory]
    [MemberData(nameof(NativeCompanyDisappearanceCases))]
    public async Task SharedNative_CompanyDisappearingBeforePageCannotWriteMoreFacts(bool postgres, bool scheduled, int beforePage)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres, bankModelTemplate);
        var db = fixture.First;
        var (_, principal) = await AddNativeAdminAsync(db);
        var companyId = (await db.BankConnections.AsNoTracking().SingleAsync()).LegalEntityId;
        var bank = NativeOnePage();
        bank.Pages["shared"] = [[Op("company-first", 1)], [Op("company-second", 2)]];
        string? factsBeforeDeletion = null;
        bank.BeforeStatementPage = async page =>
        {
            if (page == beforePage)
            {
                factsBeforeDeletion = await CaptureCompanyLifetimeFactsAsync(fixture.Second);
                await fixture.Second.LegalEntities.Where(company => company.Id == companyId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(company => EF.Property<bool>(company, "IsDeleted"), true));
            }
        };
        var invocation = scheduled ? BankSyncInvocation.Scheduled("company-disappearing") : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        var result = await fixture.Service(db, bank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.NotNull(result.Error);
        fixture.Second.ChangeTracker.Clear();
        Assert.NotNull(factsBeforeDeletion);
        Assert.Equal(factsBeforeDeletion, await CaptureCompanyLifetimeFactsAsync(fixture.Second));
        Assert.Equal(beforePage, await fixture.Second.MoneyOperations.IgnoreQueryFilters().CountAsync());
        Assert.Equal(beforePage, await fixture.Second.BankStatementOperations.CountAsync());
        Assert.Equal(beforePage, await fixture.Second.BankStatementImportRows.CountAsync());
        Assert.False(await fixture.Second.LegalEntities.AnyAsync(company => company.Id == companyId));
        Assert.DoesNotContain(await fixture.Second.BankStatementImportBatches.ToArrayAsync(), batch => batch.Status == BankStatementImportBatchStatus.Applied);
    }

    private static async Task<string> CaptureCompanyLifetimeFactsAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            Money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Amount, item.Currency, item.Date, item.Source, item.SourceMovementKey, item.MoneyAccountId, item.BankStatementImportBatchId }).ToArrayAsync(),
            Raw = await db.BankStatementOperations.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.ExternalOperationId, item.Amount, item.OperationDate, item.IsBooked, item.IsIncome }).ToArrayAsync(),
            Rows = await db.BankStatementImportRows.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.BatchId, item.MoneyOperationId, item.BankStatementOperationId, item.MovementKey, item.Status }).ToArrayAsync(),
            Accounts = await db.BankConnectionAccounts.AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.StatementWatermark, item.LastStatementSyncAt, item.LastError }).ToArrayAsync(),
            SuccessfulLogs = await db.BankSyncLogs.AsNoTracking().Where(item => item.Kind == "statement" && item.Result == "ok")
                .OrderBy(item => item.Id).Select(item => new { item.Id, item.OperationsCount }).ToArrayAsync(),
        });

    [Theory]
    [InlineData(false, BankAccountStatuses.Closed)]
    [InlineData(false, BankAccountStatuses.Limited)]
    [InlineData(true, BankAccountStatuses.Closed)]
    [InlineData(true, BankAccountStatuses.Limited)]
    public async Task SharedNative_ExistingCompanyRetainsStatementImportForClosedOrLimitedBankAccount(bool postgres, string status)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await db.BankConnectionAccounts.SingleAsync();
        account.Status = status;
        await db.SaveChangesAsync();
        fixture.Bank.Pages["shared"] = [[Op("existing-history", 1)]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("existing-company-history"), default);
        Assert.Null(result.Error);
        Assert.NotNull(result.BatchId);
        Assert.Single(await db.MoneyOperations.ToArrayAsync());
        Assert.Single(await db.BankStatementOperations.ToArrayAsync());
        Assert.False((await db.MoneyAccounts.SingleAsync()).IsActive);
    }

    public static IEnumerable<object[]> NativeCompanyLifetimeCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var scheduled in new[] { false, true })
                foreach (var deleted in new[] { false, true })
                    yield return [postgres, scheduled, deleted];
    }

    [Theory]
    [MemberData(nameof(NativeCompanyLifetimeCases))]
    public async Task SharedNative_RequiresExistingCompanyBeforeProviderOrPackage(bool postgres, bool scheduled, bool deleted)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (_, principal) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("company-lifetime", 1)]];
        var companyId = fixture.Connection.LegalEntityId;
        if (deleted)
            await db.LegalEntities.Where(company => company.Id == companyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(company => EF.Property<bool>(company, "IsDeleted"), true));
        db.ChangeTracker.Clear();
        var invocation = scheduled ? BankSyncInvocation.Scheduled("company-lifetime") : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        if (deleted)
        {
            Assert.NotNull(result.Error);
            Assert.Null(result.BatchId);
            Assert.Equal(0, fixture.Bank.StatementCalls);
            Assert.Empty(await db.BankStatementImportBatches.ToArrayAsync());
            Assert.Empty(await db.BankStatementImportRows.ToArrayAsync());
            Assert.Empty(await db.BankStatementOperations.ToArrayAsync());
            Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
            Assert.Empty(await db.MoneyAccounts.ToArrayAsync());
        }
        else
        {
            Assert.Null(result.Error);
            Assert.NotNull(result.BatchId);
            Assert.Equal(1, fixture.Bank.StatementCalls);
            Assert.Single(await db.MoneyOperations.ToArrayAsync());
            Assert.Equal(companyId, (await db.MoneyAccounts.SingleAsync()).CompanyId);
        }
    }
}

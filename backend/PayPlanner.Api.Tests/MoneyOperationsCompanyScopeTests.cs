using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private static MoneyOperation ManualCompanyOperation(AccountScopeSeed seed, int? accountId) => new()
    {
        Source = MoneyOperationSource.Bank,
        Direction = MoneyOperationDirection.Expense,
        AccountingType = MoneyOperationAccountingType.Fact,
        MoneyAccountId = accountId,
        AccountName = "Введённое имя не доказывает компанию",
        Currency = "RUB",
        Amount = 10m,
        Date = BankScopeDate,
        ResponsibleUserId = seed.Actor.Id,
    };

    private static async Task<MoneyAccount> PrepareManualCompanyAsync(PaymentContext db, AccountScopeSeed seed)
    {
        await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.CanCreate, true).SetProperty(item => item.CanEdit, true));
        var cash = new MoneyAccount { CompanyId = seed.Company.Id, Name = "Синтетическая касса своей компании", Source = MoneyOperationSource.Cash, IsActive = true };
        db.MoneyAccounts.Add(cash);
        await db.SaveChangesAsync();
        return cash;
    }

    private static async Task<string> CaptureManualCompanyAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Accounts = await CaptureAccountScopeAsync(db),
        Money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new
        {
            item.Id,
            item.Source,
            item.Direction,
            item.AccountingType,
            item.MoneyAccountId,
            item.AccountName,
            item.Currency,
            item.Amount,
            item.Date,
            item.RecognitionDate,
            item.ResponsibleUserId,
            item.LinkedInvoiceId,
            item.MatchStatus,
            item.MatchComment,
            item.BankStatementImportBatchId,
            item.SourceMovementKey,
            IsDeleted = EF.Property<bool>(item, "IsDeleted"),
        }).ToArrayAsync(),
        Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.Amount, item.PaidAmount }).ToArrayAsync(),
        CashOrders = await db.CashOrders.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.MoneyOperationId, item.Amount }).ToArrayAsync(),
    });

    public static IEnumerable<object[]> ManualCompanyDeniedCases =>
        from postgres in new[] { false, true }
        from endpoint in new[] { "create", "update", "transfer-source", "transfer-target" }
        from reason in new[] { "foreign", "unknown", "incoherent", "revoked", "closed-bank", "inactive-account", "missing-id", "inactive-user", "unapproved-user", "no-action" }
        select new object[] { postgres, endpoint, reason };

    [Theory]
    [MemberData(nameof(ManualCompanyDeniedCases))]
    public async Task ManualCompany_WriteCannotBypassAccountScope(bool postgres, string endpoint, string reason)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var cash = await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        var candidate = reason switch
        {
            "foreign" => seed.Foreign.Id,
            "unknown" => seed.Unknown.Id,
            "incoherent" => seed.Incoherent.Id,
            "missing-id" => 0,
            _ => seed.Own.Id,
        };
        if (reason == "revoked") await db.UserCompanyAccesses.Where(item => item.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        if (reason == "closed-bank") await db.BankConnectionAccounts.Where(item => item.Id == seed.Own.BankConnectionAccountId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, BankAccountStatuses.Closed));
        if (reason == "inactive-account") await db.MoneyAccounts.Where(item => item.Id == seed.Own.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        if (reason == "inactive-user") await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        if (reason == "unapproved-user") await db.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        if (reason == "no-action") await db.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanCreate, false).SetProperty(item => item.CanEdit, false));
        var before = await CaptureManualCompanyAsync(db);
        var controller = CreateController(db, seed.Actor.Id, isAdminClaim: true);
        var updateModel = ManualCompanyOperation(seed, candidate);
        // A closed bank may keep descriptive metadata editable; this case changes money.
        if (reason == "closed-bank") updateModel.Amount = 12m;
        IActionResult? result = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            result = endpoint switch
            {
                "create" => await controller.Create(ManualCompanyOperation(seed, candidate)),
                "update" => await controller.Update(original.Id, updateModel),
                _ => (await controller.CreateTransfer(new MoneyOperationTransferRequest
                {
                    SourceMoneyAccountId = endpoint == "transfer-source" ? candidate : cash.Id,
                    TargetMoneyAccountId = endpoint == "transfer-target" ? candidate : cash.Id,
                    Amount = 10m,
                    Currency = "RUB",
                    Date = BankScopeDate,
                    ResponsibleUserId = seed.Actor.Id,
                })).Result,
            };
        });
        Assert.Null(error);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
        Assert.True(result is BadRequestObjectResult or ForbidResult or NotFoundResult, result?.GetType().Name);
        if (result is ObjectResult objectResult)
            Assert.DoesNotContain(seed.Foreign.BankAccountNumber!, JsonSerializer.Serialize(objectResult.Value), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ManualCompanyAllowedCases =>
        from postgres in new[] { false, true }
        from admin in new[] { false, true }
        from endpoint in new[] { "create", "update", "transfer" }
        select new object[] { postgres, admin, endpoint };

    [Theory]
    [MemberData(nameof(ManualCompanyAllowedCases))]
    public async Task ManualCompany_AllowedOwnAccountKeepsFinancialBehavior(bool postgres, bool admin, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var cash = await PrepareManualCompanyAsync(db, seed);
        var controller = CreateController(db, seed.Actor.Id, isAdminClaim: !admin);
        if (endpoint == "transfer")
        {
            var response = Assert.IsType<MoneyOperationTransferResponse>(Assert.IsType<CreatedResult>((await controller.CreateTransfer(new MoneyOperationTransferRequest
            {
                SourceMoneyAccountId = seed.Own.Id,
                TargetMoneyAccountId = cash.Id,
                Date = BankScopeDate,
                Amount = 10m,
                Currency = "RUB",
                ResponsibleUserId = seed.Actor.Id,
            })).Result).Value);
            Assert.Equal(seed.Own.Id, response.SourceOperation.MoneyAccountId);
            Assert.Equal(cash.Id, response.TargetOperation.MoneyAccountId);
            Assert.Equal(10m, response.SourceOperation.Amount);
            Assert.Equal(10m, response.TargetOperation.Amount);
            Assert.Equal(MoneyOperationDirection.Expense, response.SourceOperation.Direction);
            Assert.Equal(MoneyOperationDirection.Income, response.TargetOperation.Direction);
            Assert.Equal(2, await db.MoneyOperations.CountAsync());
            Assert.Single(await db.CashOrders.ToArrayAsync());
        }
        else
        {
            var model = ManualCompanyOperation(seed, seed.Own.Id);
            if (endpoint == "update")
            {
                var original = ManualCompanyOperation(seed, seed.Own.Id);
                db.MoneyOperations.Add(original);
                await db.SaveChangesAsync();
                model.Amount = 12m;
                Assert.IsType<OkObjectResult>(await controller.Update(original.Id, model));
            }
            else Assert.IsType<CreatedResult>(await controller.Create(model));
            await using var verify = fixture.Open();
            var saved = Assert.Single(await verify.MoneyOperations.ToArrayAsync());
            Assert.Equal(seed.Own.Id, saved.MoneyAccountId);
            Assert.Equal(seed.Own.Name, saved.AccountName);
            Assert.Equal(endpoint == "update" ? 12m : 10m, saved.Amount);
        }
    }

    [Theory]
    [InlineData(false, "create")]
    [InlineData(false, "update")]
    [InlineData(false, "transfer")]
    [InlineData(true, "create")]
    [InlineData(true, "update")]
    [InlineData(true, "transfer")]
    public async Task ManualCompany_AdminCannotMutateUnattributedHistoricalAccount(bool postgres, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        var controller = CreateController(db, seed.Actor.Id);
        IActionResult? result = endpoint switch
        {
            "create" => await controller.Create(ManualCompanyOperation(seed, seed.Unknown.Id)),
            "update" => await controller.Update(original.Id, ManualCompanyOperation(seed, seed.Unknown.Id)),
            _ => (await controller.CreateTransfer(new MoneyOperationTransferRequest { SourceMoneyAccountId = seed.Own.Id, TargetMoneyAccountId = seed.Unknown.Id, Amount = 10m, Currency = "RUB", Date = BankScopeDate })).Result,
        };
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
        Assert.True(result is BadRequestObjectResult or ForbidResult or NotFoundResult, result?.GetType().Name);
    }

    [Theory]
    [InlineData(false, "list")]
    [InlineData(false, "detail")]
    [InlineData(false, "ignore")]
    [InlineData(false, "candidates")]
    [InlineData(false, "link-invoice")]
    [InlineData(true, "list")]
    [InlineData(true, "detail")]
    [InlineData(true, "ignore")]
    [InlineData(true, "candidates")]
    [InlineData(true, "link-invoice")]
    public async Task ManualCompany_ForeignCompanyCannotBeReadOrChangedThroughOperationId(bool postgres, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareManualCompanyAsync(db, seed);
        var operation = ManualCompanyOperation(seed, seed.Foreign.Id);
        db.MoneyOperations.Add(operation);
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        var controller = CreateController(db, seed.Actor.Id);
        if (endpoint == "list")
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null));
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Empty(body.RootElement.EnumerateArray());
        }
        else
        {
            var action = endpoint == "detail" ? await controller.GetById(operation.Id, default)
                : endpoint == "ignore" ? await controller.Ignore(operation.Id, default)
                : endpoint == "link-invoice" ? await controller.LinkInvoice(operation.Id, new MoneyOperationsController.LinkMoneyOperationInvoiceRequest { InvoiceId = 123456 }, default)
                : (await controller.GetInvoiceCandidates(operation.Id, default)).Result;
            Assert.IsType<NotFoundResult>(action);
        }
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualCompany_OrdinaryEditCannotMoveImportedMoneyToAnotherAccount(bool postgres, bool admin)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var cash = await PrepareManualCompanyAsync(db, seed);
        var batch = new BankStatementImportBatch { CompanyId = seed.Company.Id, MoneyAccountId = seed.Own.Id, Source = BankStatementImportSources.File, FileName = "synthetic.csv", FileHash = "synthetic" };
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        original.BankStatementImportBatch = batch;
        original.SourceMovementKey = "synthetic-protected-source";
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        var result = await CreateController(db, seed.Actor.Id).Update(original.Id, ManualCompanyOperation(seed, cash.Id));
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompany_ClosedBankAllowsMetadataWithoutChangingMoneyFacts(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        original.AccountName = seed.Own.Name;
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        await db.BankConnectionAccounts.Where(bank => bank.Id == seed.Own.BankConnectionAccountId).ExecuteUpdateAsync(setters => setters.SetProperty(bank => bank.Status, BankAccountStatuses.Closed));
        var before = await CaptureManualCompanyAsync(db);
        var model = ManualCompanyOperation(seed, seed.Own.Id);
        model.AccountName = seed.Own.Name;
        model.Purpose = "Уточнённое описание прежней операции";
        Assert.IsType<OkObjectResult>(await CreateController(db, seed.Actor.Id).Update(original.Id, model));
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureManualCompanyAsync(verify));
        Assert.Equal(model.Purpose, (await verify.MoneyOperations.SingleAsync()).Purpose);
    }

    [Theory]
    [InlineData(false, "create")]
    [InlineData(false, "update")]
    [InlineData(false, "transfer")]
    [InlineData(true, "create")]
    [InlineData(true, "update")]
    [InlineData(true, "transfer")]
    public async Task ManualCompany_DifferentCurrencyCannotBeWrittenToAccount(bool postgres, string endpoint)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var cash = await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        var model = ManualCompanyOperation(seed, seed.Own.Id);
        model.Currency = "USD";
        var controller = CreateController(db, seed.Actor.Id);
        var result = endpoint == "create" ? await controller.Create(model)
            : endpoint == "update" ? await controller.Update(original.Id, model)
            : (await controller.CreateTransfer(new MoneyOperationTransferRequest { SourceMoneyAccountId = seed.Own.Id, TargetMoneyAccountId = cash.Id, Amount = 10m, Currency = "USD", Date = BankScopeDate })).Result;
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompany_EmptyCurrencyUsesDeclaredAccountCurrency(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        await PrepareManualCompanyAsync(db, fixture.Seed);
        var model = ManualCompanyOperation(fixture.Seed, fixture.Seed.Own.Id);
        model.Currency = null;
        var saved = Assert.IsType<MoneyOperation>(Assert.IsType<CreatedResult>(await CreateController(db, fixture.Seed.Actor.Id).Create(model)).Value);
        Assert.Equal("RUB", saved.Currency);
        Assert.Equal(fixture.Seed.Own.Id, saved.MoneyAccountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompany_TransferDoesNotSilentlyConvertBetweenCurrencies(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var cash = await PrepareManualCompanyAsync(db, fixture.Seed);
        await db.MoneyAccounts.Where(account => account.Id == cash.Id).ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Currency, "USD"));
        var before = await CaptureManualCompanyAsync(db);
        var result = await CreateController(db, fixture.Seed.Actor.Id).CreateTransfer(new MoneyOperationTransferRequest
        { SourceMoneyAccountId = fixture.Seed.Own.Id, TargetMoneyAccountId = cash.Id, Amount = 10m, Currency = "RUB", Date = BankScopeDate });
        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    public static IEnumerable<object[]> ManualImportedFactCases =>
        from postgres in new[] { false, true }
        from admin in new[] { false, true }
        from field in new[] { "amount", "date", "direction", "accounting", "currency" }
        select new object[] { postgres, admin, field };

    [Theory]
    [MemberData(nameof(ManualImportedFactCases))]
    public async Task ManualCompany_ImportedMoneyFactsRemainImmutable(bool postgres, bool admin, string field)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, seed.Own.Id);
        original.BankStatementImportBatch = new BankStatementImportBatch { CompanyId = seed.Company.Id, MoneyAccountId = seed.Own.Id, Source = BankStatementImportSources.File, FileName = "synthetic.csv", FileHash = "synthetic" };
        original.SourceMovementKey = "synthetic-imported-money-fact";
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        var model = ManualCompanyOperation(seed, seed.Own.Id);
        if (field == "amount") model.Amount = 11m;
        if (field == "date") model.Date = BankScopeDate.AddDays(1);
        if (field == "direction") model.Direction = MoneyOperationDirection.Income;
        if (field == "accounting") model.AccountingType = MoneyOperationAccountingType.Plan;
        if (field == "currency") model.Currency = "USD";
        Assert.IsType<BadRequestObjectResult>(await CreateController(db, seed.Actor.Id).Update(original.Id, model));
        Assert.Equal(before, await CaptureManualCompanyAsync(db));
    }

    private sealed class ManualCashSaveFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<CashOrder>().Any(entry => entry.State is EntityState.Added or EntityState.Modified))
            {
                Fired = true;
                throw new InvalidOperationException("synthetic cash-order save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    public static IEnumerable<object[]> ManualCashFailureCases =>
        from postgres in new[] { false, true }
        from endpoint in new[] { "create", "update", "transfer" }
        from outer in new[] { false, true }
        select new object[] { postgres, endpoint, outer };

    [Theory]
    [MemberData(nameof(ManualCashFailureCases))]
    public async Task ManualCompany_CashFailureRollsBackWholeMutationAndPreservesOuterWork(bool postgres, string endpoint, bool outer)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        var fault = new ManualCashSaveFault();
        await using var db = fixture.Open(fault);
        var seed = fixture.Seed;
        var cash = await PrepareManualCompanyAsync(db, seed);
        var original = ManualCompanyOperation(seed, cash.Id);
        original.Source = MoneyOperationSource.Cash;
        original.AccountName = cash.Name;
        if (endpoint == "update") { db.MoneyOperations.Add(original); await db.SaveChangesAsync(); }
        await using var outerTransaction = outer ? await db.Database.BeginTransactionAsync() : null;
        db.SystemSettings.Add(new SystemSetting { Key = "synthetic.manual.outer-marker", Value = "preserved" });
        await db.SaveChangesAsync();
        var before = await CaptureManualCompanyAsync(db);
        fault.Enabled = true;
        var model = ManualCompanyOperation(seed, cash.Id);
        model.Amount = 12m;
        var controller = CreateController(db, seed.Actor.Id);
        var error = await Record.ExceptionAsync(async () =>
        {
            if (endpoint == "create") await controller.Create(model);
            else if (endpoint == "update") await controller.Update(original.Id, model);
            else await controller.CreateTransfer(new MoneyOperationTransferRequest
            { SourceMoneyAccountId = seed.Own.Id, TargetMoneyAccountId = cash.Id, Amount = 12m, Currency = "RUB", Date = BankScopeDate });
        });
        Assert.IsType<InvalidOperationException>(error);
        Assert.True(fault.Fired);
        if (outerTransaction is not null) await outerTransaction.CommitAsync();
        await using var verify = fixture.Open();
        Assert.Equal(before, await CaptureManualCompanyAsync(verify));
        Assert.Equal("preserved", (await verify.SystemSettings.SingleAsync(setting => setting.Key == "synthetic.manual.outer-marker")).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualCompany_AccountScopeDoesNotResurrectArchivedMoney(bool postgres, bool admin)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin);
        await using var db = fixture.Open();
        var original = ManualCompanyOperation(fixture.Seed, fixture.Seed.Own.Id);
        db.MoneyOperations.Add(original);
        await db.SaveChangesAsync();
        db.MoneyOperations.Remove(original);
        await db.SaveChangesAsync();
        Assert.True(await db.MoneyOperations.IgnoreQueryFilters().AnyAsync(operation => operation.Id == original.Id && EF.Property<bool>(operation, "IsDeleted")));
        var controller = CreateController(db, fixture.Seed.Actor.Id);
        var result = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Empty(body.RootElement.EnumerateArray());
        Assert.IsType<NotFoundResult>(await controller.GetById(original.Id, default));

        // Disabling an account keeps its live history; deleting a movement must
        // still exclude it from the journal and every derived balance.
        var live = ManualCompanyOperation(fixture.Seed, fixture.Seed.Own.Id);
        live.Amount = 7m;
        db.MoneyOperations.Add(live);
        await db.SaveChangesAsync();
        await db.MoneyAccounts.Where(account => account.Id == fixture.Seed.Own.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.IsActive, false));
        var journal = Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null, null));
        Assert.Equal(live.Id, Assert.Single(Assert.IsAssignableFrom<IEnumerable<MoneyOperationListItemDto>>(journal.Value)).Id);
        Assert.IsType<NotFoundResult>(await controller.GetById(original.Id, default));
        Assert.IsType<OkObjectResult>(await controller.GetById(live.Id, default));

        var pageResult = await controller.GetPaged(null, null, null, null, null);
        var page = Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(pageResult.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(live.Id, Assert.Single(page.Items).Id);
        Assert.Equal(7m, page.Summary.Expense);
        Assert.Equal(-7m, page.Summary.EndBalance);
        Assert.Equal(1, page.Summary.ExpenseCount);

        var dailyResult = await controller.GetDailyBalances(BankScopeDate, BankScopeDate, MoneyOperationSource.Bank, null);
        var daily = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationDailyBalanceDto>>(
            Assert.IsType<OkObjectResult>(dailyResult.Result).Value));
        Assert.Equal(fixture.Seed.Company.Id, daily.CompanyId);
        Assert.Equal(fixture.Seed.Own.Id, daily.MoneyAccountId);
        Assert.Equal("RUB", daily.Currency);
        Assert.Equal(7m, daily.Expense);
        Assert.Equal(-7m, daily.ClosingBalance);
        Assert.Equal(1, daily.ExpenseCount);
    }
}

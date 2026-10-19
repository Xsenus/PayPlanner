using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    public static IEnumerable<object[]> CrossSourceOverlapCases =>
        from postgres in new[] { false, true }
        from nativeFirst in new[] { false, true }
        from scheduled in new[] { false, true }
        from hasDocument in new[] { false, true }
        select new object[] { postgres, nativeFirst, scheduled, hasDocument };

    public static IEnumerable<object[]> CrossSourceIndependentCases =>
        from postgres in new[] { false, true }
        from nativeFirst in new[] { false, true }
        from difference in new[] { "amount", "date", "direction", "counterparty", "account", "company", "currency" }
        select new object[] { postgres, nativeFirst, difference };

    public static IEnumerable<object[]> CrossSourceArchivedCases =>
        from postgres in new[] { false, true }
        from nativeFirst in new[] { false, true }
        from scheduled in new[] { false, true }
        select new object[] { postgres, nativeFirst, scheduled };

    private static async Task<MoneyAccount> AddCrossSourceAccountAsync(SharedFixture fixture)
        => await AddCrossSourceAccountAsync(fixture.Database.Db, fixture.Connection.LegalEntityId);

    private static async Task<MoneyAccount> AddCrossSourceAccountAsync(PaymentContext db, int companyId)
    {
        var bank = await db.BankConnectionAccounts.AsNoTracking().SingleAsync();
        var account = new MoneyAccount
        {
            CompanyId = companyId,
            BankConnectionAccountId = bank.Id,
            BankAccountNumber = bank.AccountNumber,
            BankBik = bank.Bik,
            Currency = bank.Currency,
            Source = MoneyOperationSource.Bank,
            Name = "Синтетический общий счёт файла и банка",
            IsActive = true,
        };
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<BankStatementImportResultDto> ImportCrossSourceFileAsync(
        SharedFixture fixture, ClaimsPrincipal principal, MoneyAccount account, bool hasDocument)
        => await ImportCrossSourceFileAsync(fixture.Database.Db, principal, account, hasDocument);

    private static async Task<BankStatementImportResultDto> ImportCrossSourceFileAsync(
        PaymentContext db, ClaimsPrincipal principal, MoneyAccount account, bool hasDocument)
    {
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
        var text = "Дата;Сумма;Контрагент;Назначение;Номер;Счёт\n"
            + $"02.10.2026;100;ООО Клиент;Оплата по счёту;{(hasDocument ? "101" : "")};{account.BankAccountNumber}\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var file = new FormFile(stream, 0, stream.Length, "file", "synthetic-cross-source.csv");
        var result = await controller.ImportBankStatement(file, default, account.CompanyId, account.Id);
        return Assert.IsType<BankStatementImportResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Theory]
    [MemberData(nameof(CrossSourceOverlapCases))]
    public async Task SharedNative_CrossSourceOverlapRequiresClarificationWithoutSecondMoney(
        bool postgres, bool nativeFirst, bool scheduled, bool hasDocument)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var (_, principal) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("shared-source", 2)]];
        var invocation = scheduled
            ? BankSyncInvocation.Scheduled("cross-source-overlap")
            : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        if (nativeFirst)
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        else
            Assert.Equal(1, (await ImportCrossSourceFileAsync(fixture, principal, account, hasDocument)).Created);

        db.ChangeTracker.Clear();
        var original = await db.MoneyOperations.AsNoTracking().SingleAsync();
        int secondBatchId;
        if (nativeFirst)
        {
            var result = await ImportCrossSourceFileAsync(fixture, principal, account, hasDocument);
            Assert.NotNull(result.BatchId);
            secondBatchId = result.BatchId.Value;
        }
        else
        {
            var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
            Assert.Null(result.Error);
            Assert.NotNull(result.BatchId);
            secondBatchId = result.BatchId.Value;
        }

        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        Assert.True(money.Length == 1,
            $"Possible cross-source overlap must require clarification; postgres={postgres}, nativeFirst={nativeFirst}, "
            + $"scheduled={scheduled}, document={hasDocument}; persisted money IDs={string.Join(',', money.Select(item => item.Id))}");
        Assert.Equal(original.Id, money[0].Id);
        Assert.Equal(original.SourceMovementKey, money[0].SourceMovementKey);
        Assert.Equal(original.Amount, money[0].Amount);
        Assert.Equal(original.Date, money[0].Date);
        Assert.Equal(original.BankStatementImportBatchId, money[0].BankStatementImportBatchId);
        var secondRow = await db.BankStatementImportRows.AsNoTracking().SingleAsync(row => row.BatchId == secondBatchId);
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, secondRow.Status);
        Assert.Null(secondRow.MoneyOperationId);
        Assert.False(string.IsNullOrWhiteSpace(secondRow.Message));
        var rawId = (await db.BankStatementOperations.AsNoTracking().SingleAsync()).Id;
        Assert.Equal(2, await db.BankStatementImportBatches.CountAsync());

        // Both source receipts and a new bank request must preserve the original
        // fact without manufacturing an association from similar attributes.
        await ImportCrossSourceFileAsync(fixture, principal, account, hasDocument);
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            scheduled ? BankSyncInvocation.Scheduled("cross-source-rescan")
                : BankSyncInvocation.ForUser(principal, Guid.NewGuid()), default)).Error);
        db.ChangeTracker.Clear();
        Assert.Equal(original.Id, (await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id);
        Assert.Equal(rawId, (await db.BankStatementOperations.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(3, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(3, await db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [MemberData(nameof(CrossSourceIndependentCases))]
    public async Task SharedNative_CrossSourceIndependentFactsKeepOwnMoney(
        bool postgres, bool nativeFirst, string difference)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var fileAccount = account;
        var (_, principal) = await AddNativeAdminAsync(db);
        var operation = Op("independent-source", 2);
        operation = difference switch
        {
            "amount" => operation with { Amount = 101m },
            "date" => operation with { Date = Op("independent-source", 3).Date },
            "direction" => operation with { IsIncome = false },
            "counterparty" => operation with { CounterpartyName = "Другой синтетический клиент" },
            _ => operation,
        };
        if (difference is "account" or "company" or "currency")
        {
            var companyId = account.CompanyId;
            if (difference == "company")
            {
                var company = new LegalEntity { ShortName = "Другая собственная компания" };
                db.LegalEntities.Add(company);
                await db.SaveChangesAsync();
                companyId = company.Id;
                (await db.SystemSettings.SingleAsync(setting => setting.Key == "legalEntities.mainCompanyIds")).Value
                    = System.Text.Json.JsonSerializer.Serialize(new[] { account.CompanyId!.Value, company.Id });
            }
            fileAccount = new MoneyAccount
            {
                Name = "Другой синтетический файловый счёт",
                Source = MoneyOperationSource.Bank,
                CompanyId = companyId,
                BankAccountNumber = "40702810200000000002",
                Currency = difference == "currency" ? "USD" : "RUB",
                IsActive = true,
            };
            db.MoneyAccounts.Add(fileAccount);
            await db.SaveChangesAsync();
        }
        fixture.Bank.Pages["shared"] = [[operation]];
        var invocation = BankSyncInvocation.Scheduled("independent-cross-source");
        if (nativeFirst)
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        else
            Assert.Equal(1, (await ImportCrossSourceFileAsync(fixture, principal, fileAccount, true)).Created);
        db.ChangeTracker.Clear();
        var original = await db.MoneyOperations.AsNoTracking().SingleAsync();
        if (nativeFirst)
            Assert.Equal(1, (await ImportCrossSourceFileAsync(fixture, principal, fileAccount, true)).Created);
        else
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, money.Length);
        Assert.Contains(money, item => item.Id == original.Id && item.SourceMovementKey == original.SourceMovementKey
            && item.Amount == original.Amount && item.Date == original.Date && item.Currency == original.Currency);
        Assert.All(await db.BankStatementImportRows.AsNoTracking().ToArrayAsync(), row =>
        {
            Assert.Equal(BankStatementImportRowStatus.Created, row.Status);
            Assert.NotNull(row.MoneyOperationId);
        });
        Assert.Single(await db.BankStatementOperations.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedNative_DistinctNativeIdsRemainIndependentAcrossNativeTriggers(bool postgres, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (_, principal) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("authoritative-first", 2)]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            scheduled ? BankSyncInvocation.Scheduled("independent-first")
                : BankSyncInvocation.ForUser(principal, Guid.NewGuid()), default)).Error);
        var originalId = (await db.MoneyOperations.AsNoTracking().SingleAsync()).Id;
        fixture.Bank.Pages["shared"] = [[Op("authoritative-second", 2)]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            scheduled ? BankSyncInvocation.ForUser(principal, Guid.NewGuid())
                : BankSyncInvocation.Scheduled("independent-second"), default)).Error);
        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, money.Length);
        Assert.Contains(money, item => item.Id == originalId);
        Assert.Equal(2, await db.BankStatementOperations.CountAsync());
        Assert.All(await db.BankStatementImportRows.AsNoTracking().ToArrayAsync(), row =>
            Assert.Equal(BankStatementImportRowStatus.Created, row.Status));
    }

    [Theory]
    [MemberData(nameof(CrossSourceArchivedCases))]
    public async Task SharedNative_CrossSourceOverlapCannotRecreateOrResurrectArchivedMoney(
        bool postgres, bool nativeFirst, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var (_, principal) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("archived-source", 2)]];
        var invocation = scheduled
            ? BankSyncInvocation.Scheduled("archived-overlap")
            : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        if (nativeFirst)
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        else
            Assert.Equal(1, (await ImportCrossSourceFileAsync(fixture, principal, account, true)).Created);
        var original = await db.MoneyOperations.SingleAsync();
        db.MoneyOperations.Remove(original);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        Assert.Single(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());

        if (nativeFirst)
            await ImportCrossSourceFileAsync(fixture, principal, account, true);
        else
            Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        Assert.True(money.Length == 1,
            $"Archived source must remain occupied; postgres={postgres}, nativeFirst={nativeFirst}, scheduled={scheduled}; "
            + $"persisted money IDs={string.Join(',', money.Select(item => item.Id))}");
        Assert.Equal(original.Id, money[0].Id);
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        var row = await db.BankStatementImportRows.AsNoTracking().OrderByDescending(item => item.Id).FirstAsync();
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, row.Status);
        Assert.Null(row.MoneyOperationId);
        Assert.Single(await db.BankStatementOperations.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedNative_CrossSourceCompanyScopeDoesNotDependOnOriginalResponsibleUser(bool postgres, bool scheduled)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var (_, admin) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("company-visible-source", 2)]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            scheduled ? BankSyncInvocation.Scheduled("company-visible-source")
                : BankSyncInvocation.ForUser(admin, Guid.NewGuid()), default)).Error);
        var originalId = (await db.MoneyOperations.AsNoTracking().SingleAsync()).Id;
        var clerk = new User
        {
            Role = new Role { Name = "cross-source-clerk" },
            FullName = "Сотрудник своей компании",
            Email = "cross-source-clerk@example.test",
            PasswordHash = "hash",
            IsActive = true,
            IsApproved = true,
            IsEmployee = true,
        };
        db.Users.Add(clerk);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = clerk.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.Own,
        });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = clerk.Id, LegalEntityId = account.CompanyId!.Value });
        await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, clerk.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Role, "cross-source-clerk")], "test"));
        var result = await ImportCrossSourceFileAsync(fixture, principal, account, true);
        db.ChangeTracker.Clear();
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        Assert.True(money.Length == 1, $"Authorized company import must not duplicate another responsible user's bank fact; money IDs={string.Join(',', money.Select(item => item.Id))}");
        Assert.Equal(originalId, money[0].Id);
        Assert.Equal(0, result.Created);
        Assert.Empty(result.Operations);
        var row = await db.BankStatementImportRows.AsNoTracking().SingleAsync(item => item.BatchId == result.BatchId);
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, row.Status);
        Assert.Null(row.MoneyOperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_CrossSourceCannotGuessUnknownLegacyOwnershipFromAccountName(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var legacy = new MoneyOperation
        {
            Source = MoneyOperationSource.Bank,
            AccountingType = MoneyOperationAccountingType.Fact,
            Direction = MoneyOperationDirection.Income,
            AccountName = account.Name,
            Currency = "RUB",
            Date = Op("legacy", 2).Date,
            Amount = 100m,
            CounterpartyName = "ООО Клиент",
        };
        db.MoneyOperations.Add(legacy);
        await db.SaveChangesAsync();
        fixture.Bank.Pages["shared"] = [[Op("known-native-with-unknown-neighbor", 2)]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("unknown-legacy-neighbor"), default)).Error);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.MoneyOperations.CountAsync());
        var preserved = await db.MoneyOperations.AsNoTracking().SingleAsync(item => item.Id == legacy.Id);
        Assert.Null(preserved.MoneyAccountId);
        Assert.Null(preserved.SourceMovementKey);
        Assert.Null(preserved.BankStatementImportBatchId);
        Assert.Equal(100m, preserved.Amount);
        var row = await db.BankStatementImportRows.AsNoTracking().SingleAsync();
        Assert.NotEqual(legacy.Id, row.MoneyOperationId);
        Assert.Equal(account.Id, (await db.MoneyOperations.AsNoTracking().SingleAsync(item => item.Id == row.MoneyOperationId)).MoneyAccountId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_CrossSourceCandidateLimitCannotSilentlyMissLateOverlap(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var account = await AddCrossSourceAccountAsync(fixture);
        var batch = new BankStatementImportBatch
        {
            CompanyId = account.CompanyId,
            MoneyAccountId = account.Id,
            Source = BankStatementImportSources.File,
            Trigger = BankStatementImportTriggers.Manual,
            FileName = "synthetic-many-candidates.csv",
            FileHash = "synthetic-many-candidates",
            PackageKey = "synthetic-many-candidates",
            RowsRead = 1001,
            CreatedRows = 1001,
        };
        for (var index = 0; index <= 1000; index++)
        {
            var date = Op("candidate", index == 1000 ? 2 : 3).Date;
            var amount = index == 1000 ? 100m : 150m;
            var money = new MoneyOperation
            {
                Source = MoneyOperationSource.Bank,
                AccountingType = MoneyOperationAccountingType.Fact,
                Direction = MoneyOperationDirection.Income,
                MoneyAccountId = account.Id,
                AccountName = account.Name,
                Currency = "RUB",
                Date = date,
                Amount = amount,
                CounterpartyName = "ООО Клиент",
                SourceMovementKey = $"synthetic-file-candidate-{index}",
                BankStatementImportBatch = batch,
            };
            db.BankStatementImportRows.Add(new BankStatementImportRow
            {
                Batch = batch,
                RowNumber = index + 1,
                RowIdentityKey = $"synthetic-candidate-{index}",
                MovementKey = money.SourceMovementKey,
                MoneyOperation = money,
                Status = BankStatementImportRowStatus.Created,
                Date = date,
                Amount = amount,
                Currency = "RUB",
                Direction = MoneyOperationDirection.Income,
                CounterpartyName = "ООО Клиент",
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        fixture.Bank.Pages["shared"] = [[Op("late-overlap", 2), Op("independent-neighbor", 4, 200m)]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("late-overlap"), default);
        Assert.Null(result.Error);
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.AsNoTracking().SingleAsync(item => item.ExternalOperationId == "late-overlap");
        var row = await db.BankStatementImportRows.AsNoTracking().SingleAsync(item => item.BankStatementOperationId == raw.Id);
        Assert.Equal(BankStatementImportRowStatus.ClarificationRequired, row.Status);
        Assert.Null(row.MoneyOperationId);
        Assert.InRange(await db.MoneyOperations.IgnoreQueryFilters().CountAsync(), 1001, 1002);
        Assert.Equal(1001, await db.MoneyOperations.CountAsync(item => item.BankStatementImportBatchId == batch.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_ConcurrentFileAndBankKeepOneFactInIndependentContexts(bool postgres)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var companyId = (await fixture.First.BankConnections.AsNoTracking().SingleAsync()).LegalEntityId;
        var account = await AddCrossSourceAccountAsync(fixture.First, companyId);
        var (_, principal) = await AddNativeAdminAsync(fixture.First);
        fixture.First.ChangeTracker.Clear();
        var bank = new FakeBank { Pages = { ["shared"] = [[Op("concurrent-cross-source", 2)]] } };
        var invocation = BankSyncInvocation.Scheduled("concurrent-cross-source");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bankReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fileReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = Task.Run(async () =>
        {
            bankReady.SetResult();
            await start.Task;
            return await fixture.Service(fixture.First, bank).SyncStatementAsync(
                fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        });
        var file = Task.Run(async () =>
        {
            fileReady.SetResult();
            await start.Task;
            return await ImportCrossSourceFileAsync(fixture.Second, principal, account, true);
        });
        await Task.WhenAll(bankReady.Task, fileReady.Task);
        start.SetResult();
        await Task.WhenAll(native, file);
        Assert.Null((await native).Error);
        fixture.Second.ChangeTracker.Clear();
        var money = await fixture.Second.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        Assert.True(money.Length == 1, $"Concurrent source imports must keep one money fact; postgres={postgres}; money IDs={string.Join(',', money.Select(item => item.Id))}");
        var rows = await fixture.Second.BankStatementImportRows.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Equal(money[0].Id, Assert.Single(rows, row => row.Status == BankStatementImportRowStatus.Created).MoneyOperationId);
        Assert.Null(Assert.Single(rows, row => row.Status == BankStatementImportRowStatus.ClarificationRequired).MoneyOperationId);
        Assert.Single(await fixture.Second.BankStatementOperations.AsNoTracking().ToArrayAsync());

        var replay = await fixture.Service(fixture.Second, new FakeBank { Unauthorized = true }).SyncStatementAsync(
            fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Equal((await native).BatchId, replay.BatchId);
        Assert.Null(replay.Error);
        Assert.Equal((await file).BatchId,
            (await ImportCrossSourceFileAsync(fixture.First, principal, account, true)).BatchId);
        fixture.Second.ChangeTracker.Clear();
        Assert.Equal(money[0].Id, (await fixture.Second.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id);
        Assert.Equal(2, await fixture.Second.BankStatementImportBatches.CountAsync());
        Assert.Equal(2, await fixture.Second.BankStatementImportRows.CountAsync());
    }
}

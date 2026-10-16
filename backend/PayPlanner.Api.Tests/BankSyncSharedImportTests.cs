using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    private sealed record SharedFixture(BankImportProvenanceModelTests.ModelDatabase Database, FakeBank Bank, BankSyncService Service, BankConnection Connection, int AccountId);

    private static async Task<SharedFixture> CreateSharedAsync(bool postgres)
    {
        var database = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
        var db = database.Db;
        var company = new LegalEntity { ShortName = "Синтетическая собственная компания", Inn = "7707083893" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var bank = new FakeBank { Accounts = [Account("shared")] };
        var service = new BankSyncService(db, [bank], new EphemeralDataProtectionProvider());
        var connection = new BankConnection { Provider = BankProviderKeys.Tochka, LegalEntityId = company.Id };
        service.StoreTokens(connection, "synthetic-access", "synthetic-refresh", DateTime.UtcNow.AddHours(1));
        db.BankConnections.Add(connection);
        await db.SaveChangesAsync();
        Assert.Null((await service.SyncAccountsAsync(connection.Id, default)).Error);
        return new(database, bank, service, connection, (await db.BankConnectionAccounts.SingleAsync()).Id);
    }

    private static readonly DateTime SharedFrom = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SharedTo = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<(User User, ClaimsPrincipal Principal)> AddNativeAdminAsync(PaymentContext db)
    {
        var user = new User { Role = new Role { Name = "admin" }, FullName = "Синтетический администратор", Email = "native-scope@example.test", PasswordHash = "hash", IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Role, "admin")], "test")));
    }

    public static IEnumerable<object[]> SharedNativeDeniedCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "inactive", "unapproved", "fake-admin-claim", "unknown-user", "missing-id", "foreign-company" }
        select new object[] { postgres, reason };

    [Theory]
    [MemberData(nameof(SharedNativeDeniedCases))]
    public async Task SharedNative_DeniesUntrustedCallerBeforeProviderOrPackage(bool postgres, string reason)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (user, principal) = await AddNativeAdminAsync(db);
        if (reason == "inactive") user.IsActive = false;
        if (reason == "unapproved") user.IsApproved = false;
        if (reason == "fake-admin-claim") user.Role!.Name = "viewer";
        if (reason == "unknown-user") principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, int.MaxValue.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "admin")], "test"));
        if (reason == "missing-id") principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "test"));
        if (reason == "foreign-company") (await db.SystemSettings.SingleAsync(setting => setting.Key == "legalEntities.mainCompanyIds")).Value = "[]";
        await db.SaveChangesAsync();
        var invocation = reason == "foreign-company" ? BankSyncInvocation.Scheduled("foreign-company") : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.NotNull(result.Error);
        Assert.Equal(0, fixture.Bank.StatementCalls);
        Assert.Empty(await db.BankStatementImportBatches.ToArrayAsync());
        Assert.Empty(await db.BankStatementOperations.ToArrayAsync());
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await db.MoneyAccounts.ToArrayAsync());
    }

    [Fact]
    public void SharedNative_NullUserCannotBecomeScheduledInvocation()
        => Assert.Throws<ArgumentNullException>(() => BankSyncInvocation.ForUser(null!, Guid.NewGuid()));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedNative_CompletedReplayRequiresCurrentUserAndCompany(bool postgres, bool removeCompany)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (user, principal) = await AddNativeAdminAsync(db);
        var invocation = BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        fixture.Bank.Pages["shared"] = [[Op("one", 1)]];
        var completed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(completed.Error);
        var moneyId = (await db.MoneyOperations.SingleAsync()).Id;
        if (removeCompany) (await db.SystemSettings.SingleAsync(setting => setting.Key == "legalEntities.mainCompanyIds")).Value = "[]";
        else user.IsActive = false;
        await db.SaveChangesAsync();
        var calls = fixture.Bank.StatementCalls;
        var denied = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.NotNull(denied.Error);
        Assert.Null(denied.BatchId);
        Assert.Equal(calls, fixture.Bank.StatementCalls);
        Assert.Equal(moneyId, (await db.MoneyOperations.SingleAsync()).Id);
        Assert.Equal(1, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(1, await db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false, "inactive")]
    [InlineData(false, "company-removed")]
    [InlineData(false, "bank-company-changed")]
    [InlineData(false, "statements-disabled")]
    [InlineData(true, "inactive")]
    [InlineData(true, "company-removed")]
    [InlineData(true, "bank-company-changed")]
    [InlineData(true, "statements-disabled")]
    public async Task SharedNative_RevocationDuringProviderReplyRejectsBeforeRawAndMoneyWrite(bool postgres, string change)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var (user, principal) = await AddNativeAdminAsync(db);
        fixture.Bank.Pages["shared"] = [[Op("one", 1)]];
        fixture.Bank.BeforeStatementPage = async _ =>
        {
            if (change == "inactive") await db.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
            else if (change == "company-removed") await db.SystemSettings.Where(item => item.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Value, "[]"));
            else if (change == "statements-disabled") await db.BankConnectionAccounts.Where(item => item.Id == fixture.AccountId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StatementsEnabled, false));
            else
            {
                var foreign = new LegalEntity { ShortName = "Синтетическая другая компания" };
                db.LegalEntities.Add(foreign);
                await db.SaveChangesAsync();
                await db.BankConnections.Where(item => item.Id == fixture.Connection.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LegalEntityId, foreign.Id));
            }
        };
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.ForUser(principal, Guid.NewGuid()), default);
        Assert.NotNull(result.Error);
        Assert.Equal(1, fixture.Bank.StatementCalls);
        Assert.Empty(await db.BankStatementOperations.ToArrayAsync());
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await db.BankStatementImportRows.ToArrayAsync());
        Assert.Null((await db.BankStatementImportBatches.SingleAsync()).CompletedAt);
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_PagesAndSuccessfulReplayKeepOnePackageAndCanonicalMoney(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        fixture.Bank.Pages["shared"] = [[Op("one", 1), Op("two", 2)], [Op("one", 1), Op("three", 3)]];
        var invocation = BankSyncInvocation.Scheduled("same-logical-run");
        var first = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(first.Error);
        Assert.NotNull(first.BatchId);
        Assert.Equal(3, first.Inserted);
        var calls = fixture.Bank.StatementCalls;
        var replay = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Equal(first, replay);
        Assert.Equal(calls, fixture.Bank.StatementCalls);
        db.ChangeTracker.Clear();
        var batch = await db.BankStatementImportBatches.SingleAsync();
        Assert.Equal(first.BatchId, batch.Id);
        Assert.Equal(3, batch.RowsRead);
        Assert.Equal(3, batch.CreatedRows);
        Assert.NotNull(batch.CompletedAt);
        Assert.Equal(BankStatementImportSources.BackgroundBank, batch.Source);
        Assert.Null(batch.ImportedByUserId);
        Assert.Null(batch.ResponsibleUserId);
        Assert.Equal(3, await db.BankStatementOperations.CountAsync());
        Assert.Equal(3, await db.MoneyOperations.CountAsync());
        var rows = await db.BankStatementImportRows.ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, rows.Select(row => row.RowIdentityKey).Distinct().Count());
        Assert.All(rows, row => { Assert.NotNull(row.BankStatementOperationId); Assert.NotNull(row.MoneyOperationId); Assert.NotNull(row.MovementKey); });
        Assert.Empty(db.ChangeTracker.Entries<BankStatementOperation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_PendingToBookedAcrossRunsRetainsRawIdentityAndOneMoney(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        fixture.Bank.Pages["shared"] = [[Op("pending", 2, booked: false)]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("pending"), default)).Error);
        var rawId = (await db.BankStatementOperations.SingleAsync()).Id;
        Assert.Empty(await db.MoneyOperations.ToArrayAsync());
        Assert.Equal(BankStatementImportRowStatus.Pending, (await db.BankStatementImportRows.SingleAsync()).Status);
        fixture.Bank.Pages["shared"] = [[Op("pending", 2, booked: true)]];
        var booked = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("booked"), default);
        Assert.Null(booked.Error);
        Assert.Equal(rawId, (await db.BankStatementOperations.SingleAsync()).Id);
        var moneyId = (await db.MoneyOperations.SingleAsync()).Id;
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("overlap"), default)).Error);
        Assert.Equal(moneyId, (await db.MoneyOperations.SingleAsync()).Id);
        Assert.Equal(3, await db.BankStatementImportBatches.CountAsync());
        Assert.All(await db.BankStatementImportRows.ToArrayAsync(), row => Assert.Equal(rawId, row.BankStatementOperationId));
        Assert.Equal(2, await db.BankStatementImportRows.CountAsync(row => row.MoneyOperationId == moneyId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_PartialFailureResumesSameRequestWithoutSecondMoney(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        fixture.Bank.Pages["shared"] = [[Op("one", 1)], [Op("two", 2)]];
        fixture.Bank.FailOnPage = 1;
        var invocation = BankSyncInvocation.Scheduled("resume-request");
        var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.NotNull(failed.Error);
        Assert.Equal(1, await db.BankStatementOperations.CountAsync());
        var originalId = (await db.MoneyOperations.SingleAsync()).Id;
        Assert.Null((await db.BankStatementImportBatches.SingleAsync()).CompletedAt);
        Assert.Null((await db.BankConnectionAccounts.SingleAsync()).StatementWatermark);
        fixture.Bank.FailOnPage = null;
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        db.ChangeTracker.Clear();
        var batch = await db.BankStatementImportBatches.SingleAsync();
        Assert.Equal(resumed.BatchId, batch.Id);
        Assert.Equal(2, batch.RowsRead);
        Assert.Equal(2, batch.CreatedRows);
        Assert.Null(batch.ErrorSummary);
        Assert.NotNull(batch.CompletedAt);
        Assert.Equal(2, await db.MoneyOperations.CountAsync());
        Assert.True(await db.MoneyOperations.AnyAsync(operation => operation.Id == originalId));
        Assert.Equal(2, await db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_ManualAndSystemKeepDifferentActorsButShareMoney(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var user = new User { Role = new Role { Name = "admin" }, FullName = "Синтетический администратор", Email = "bank-admin@example.test", PasswordHash = "hash", IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture))], "test"));
        fixture.Bank.Pages["shared"] = [[Op("one", 1)]];
        var manual = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.ForUser(principal, Guid.NewGuid()), default);
        Assert.Null(manual.Error);
        var system = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("independent-background"), default);
        Assert.Null(system.Error);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.MoneyOperations.CountAsync());
        Assert.Equal(user.Id, (await db.MoneyOperations.SingleAsync()).ResponsibleUserId);
        var manualBatch = await db.BankStatementImportBatches.SingleAsync(batch => batch.Id == manual.BatchId);
        var systemBatch = await db.BankStatementImportBatches.SingleAsync(batch => batch.Id == system.BatchId);
        Assert.Equal(BankStatementImportSources.ManualBank, manualBatch.Source);
        Assert.Equal(user.Id, manualBatch.ImportedByUserId);
        Assert.Equal(BankStatementImportSources.BackgroundBank, systemBatch.Source);
        Assert.Null(systemBatch.ImportedByUserId);
        Assert.Equal(2, await db.BankStatementImportRows.CountAsync(row => row.MoneyOperationId.HasValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_EmptyRunPersistsKnownOriginAndReplay(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var invocation = BankSyncInvocation.Scheduled("empty-request");
        var first = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(first.Error);
        Assert.NotNull(first.BatchId);
        Assert.Equal(first, await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default));
        var batch = await database.Db.BankStatementImportBatches.SingleAsync();
        Assert.Equal(0, batch.RowsRead);
        Assert.NotNull(batch.CompanyId);
        Assert.NotNull(batch.MoneyAccountId);
        Assert.NotNull(batch.BankConnectionAccountId);
        Assert.NotNull(batch.CompletedAt);
        Assert.Empty(await database.Db.MoneyOperations.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_RequestIdRejectsConflictingPeriodBeforeCallingBank(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var invocation = BankSyncInvocation.Scheduled("conflicting-period");
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default)).Error);
        var calls = fixture.Bank.StatementCalls;
        var conflict = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom.AddDays(1), SharedTo, invocation, default);
        Assert.NotNull(conflict.Error);
        Assert.Equal(calls, fixture.Bank.StatementCalls);
        Assert.Equal(1, await database.Db.BankStatementImportBatches.CountAsync());
        Assert.Empty(await database.Db.MoneyOperations.ToArrayAsync());
    }
}

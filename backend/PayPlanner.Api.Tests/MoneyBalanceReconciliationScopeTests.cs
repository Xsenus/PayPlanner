using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReconciliationPeriod_SupportsLongBoundedPeriodsAndRejectsReversedDates(bool postgres, bool reversed)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        await SeedReconciliationFactAsync(db, fixture.Seed);
        var from = BankScopeDate.Date;
        var to = reversed ? from.AddDays(-1) : from.AddDays(400);
        var facts = await CaptureReconciliationMoneyFactsAsync(db);
        var action = await CreateController(db, fixture.Seed.Actor.Id, false)
            .RunBalanceReconciliation(from, to, MoneyOperationSource.Bank, null);
        Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(db));
        if (reversed)
        {
            Assert.IsType<BadRequestObjectResult>(action.Result);
            Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        }
        else
        {
            var result = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
            Assert.Equal(401, result.Created);
            Assert.Equal(401, result.Items.Count);
            Assert.Equal(100m, result.Items.Sum(item => item.Income));
            Assert.Equal(100m, result.Items[^1].ClosingBalance);
            Assert.Equal(401, await db.MoneyBalanceReconciliationResults.CountAsync());
        }
    }

    public static IEnumerable<object[]> ReconciliationManualCallerCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "success", "role", "inactive", "approval", "failure" }
        from afterSave in reason == "failure" ? new[] { true }
            : reason == "success" ? new[] { false }
            : postgres ? new[] { false, true } : new[] { false }
        select new object[] { postgres, reason, afterSave };

    [Theory]
    [MemberData(nameof(ReconciliationManualCallerCases))]
    public async Task ReconciliationManualCaller_UsesCurrentAdminAndRecordsRealOutcome(bool postgres, string reason, bool afterSave)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        var day = MoneyBalanceReconciliationSchedulerService.ResolveReconciliationTargetDate(DateTime.UtcNow);
        string facts;
        await using (var setup = fixture.Open())
        {
            await SeedReconciliationFactAsync(setup, fixture.Seed);
            await setup.MoneyOperations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Date, day));
            facts = await CaptureReconciliationMoneyFactsAsync(setup);
        }
        var calls = 0;
        async Task RevokeOrFail()
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            if (reason == "failure") throw new InvalidOperationException("synthetic manual reconciliation failure");
            await using var other = fixture.Open();
            if (reason == "role")
                await other.Roles.Where(item => item.Id == fixture.Seed.Actor.RoleId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Name, "revoked-admin"));
            else if (reason == "inactive")
                await other.Users.Where(item => item.Id == fixture.Seed.Actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
            else
                await other.Users.Where(item => item.Id == fixture.Seed.Actor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        }
        IInterceptor[] interceptors = reason == "success" ? []
            : [afterSave ? new ReconciliationAfterSave(RevokeOrFail) : new ReconciliationBeforeTransaction(RevokeOrFail)];
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.Open(interceptors));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        await using var provider = services.BuildServiceProvider();
        var runner = new MoneyBalanceReconciliationSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoneyBalanceReconciliationSchedulerService>.Instance);
        await using var requestDb = fixture.Open();
        var states = new ReconciliationJobStates();
        var controller = new SystemJobsController(requestDb, new ConfigurationBuilder().Build(),
            new ReconciliationOptions(), jobRunState: states, moneyBalanceRunner: runner)
        {
            ControllerContext = CreateController(requestDb, fixture.Seed.Actor.Id, isAdminClaim: true).ControllerContext,
        };
        // A normal HTTP request has already passed the database-backed admin
        // action filter. The interceptors revoke access after this entry point.
        var result = await controller.RunJob(SystemJobKeys.MoneyBalanceReconciliation, default);
        await using var verify = fixture.Open();
        Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(verify));
        if (reason == "success")
        {
            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(1, states.Successes);
            Assert.Equal(0, states.Failures);
            Assert.Equal(100m, Assert.Single(await verify.MoneyBalanceReconciliationResults.ToListAsync()).ClosingBalance);
        }
        else
        {
            Assert.Equal(1, calls);
            Assert.Equal(0, states.Successes);
            Assert.Equal(1, states.Failures);
            Assert.Empty(await verify.MoneyBalanceReconciliationResults.ToListAsync());
            if (reason == "failure")
                Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result.Result).StatusCode);
            else Assert.IsType<ForbidResult>(result.Result);
        }
    }

    private sealed class ReconciliationOptions : IOptionsMonitor<AppFeatureFlagsOptions>
    {
        public AppFeatureFlagsOptions CurrentValue { get; } = new();
        public AppFeatureFlagsOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppFeatureFlagsOptions, string?> listener) => null;
    }

    private sealed class ReconciliationJobStates : ISystemJobRunStateService
    {
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public Task RecordSuccessAsync(string jobKey, DateTime startedAt, int? processedCount, int? skippedCount,
            string summary, CancellationToken cancellationToken = default)
        {
            Assert.Equal(SystemJobKeys.MoneyBalanceReconciliation, jobKey);
            Successes++;
            return Task.CompletedTask;
        }
        public Task RecordFailureAsync(string jobKey, DateTime startedAt, Exception exception, string? summary = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(SystemJobKeys.MoneyBalanceReconciliation, jobKey);
            Failures++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, SystemJobRunState>> GetLastRunsAsync(IEnumerable<string> jobKeys,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, SystemJobRunState>>(new Dictionary<string, SystemJobRunState>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconciliationContract_PreservesExplicitUnknownIdentityAndCurrency(bool saved)
    {
        object dto = saved ? new MoneyBalanceReconciliationResultDto() : new MoneyOperationDailyBalanceDto();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(dto, dto.GetType(), options));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("balanceKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("currency").ValueKind);
    }

    private static async Task SeedReconciliationFactAsync(PaymentContext db, AccountScopeSeed seed)
    {
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == seed.Actor.RoleId
            && item.Section == RolePermissionCatalog.Payments);
        permission.CanView = true;
        permission.CanEdit = true;
        permission.ViewScope = DataVisibilityScope.All;
        db.MoneyOperations.Add(new MoneyOperation
        {
            MoneyAccountId = seed.Own.Id,
            AccountName = seed.Own.Name,
            Currency = "RUB",
            Amount = 100m,
            Date = BankScopeDate.Date,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            ResponsibleUserId = seed.Actor.Id,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationConcurrency_IndependentSessionsDoNotDuplicateSavedBalance(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        string facts;
        await using (var setup = fixture.Open())
        {
            await SeedReconciliationFactAsync(setup, fixture.Seed);
            facts = await CaptureReconciliationMoneyFactsAsync(setup);
        }
        async Task<IActionResult?> Run(PaymentContext db) => (await CreateController(db, fixture.Seed.Actor.Id, false)
            .RunBalanceReconciliation(BankScopeDate.Date, BankScopeDate.Date, MoneyOperationSource.Bank, null)).Result;
        var pair = await AccountConcurrentPairAsync(fixture, Run, Run);
        var first = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(pair.First).Value);
        var second = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(pair.Second).Value);
        Assert.Equal(1, first.Created);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Updated);
        await using var verify = fixture.Open();
        Assert.Equal(100m, Assert.Single(await verify.MoneyBalanceReconciliationResults.ToListAsync()).ClosingBalance);
        Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(verify));
    }

    public static IEnumerable<object[]> ReconciliationRevocationCases =>
        from postgres in new[] { false, true }
        from reason in new[] { "permission", "inactive", "company" }
        from afterSave in postgres ? new[] { false, true } : new[] { false }
        select new object[] { postgres, reason, afterSave };

    [Theory]
    [MemberData(nameof(ReconciliationRevocationCases))]
    public async Task ReconciliationRun_RejectsAuthorityRevokedAfterRequestAuthorization(bool postgres, string reason, bool afterSave)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        string facts;
        await using (var setup = fixture.Open())
        {
            await SeedReconciliationFactAsync(setup, fixture.Seed);
            facts = await CaptureReconciliationMoneyFactsAsync(setup);
        }
        var calls = 0;
        async Task Revoke()
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            await using var other = fixture.Open();
            var seed = fixture.Seed;
            if (reason == "permission")
                await other.RolePermissions.Where(item => item.RoleId == seed.Actor.RoleId && item.Section == RolePermissionCatalog.Payments)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanEdit, false));
            else if (reason == "inactive")
                await other.Users.Where(item => item.Id == seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
            else
                await other.UserCompanyAccesses.Where(item => item.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        }
        await using var db = fixture.Open(afterSave ? new ReconciliationAfterSave(Revoke) : new ReconciliationBeforeTransaction(Revoke));
        var action = await CreateController(db, fixture.Seed.Actor.Id, false)
            .RunBalanceReconciliation(BankScopeDate.Date, BankScopeDate.Date, MoneyOperationSource.Bank, null);
        Assert.IsType<ForbidResult>(action.Result);
        Assert.Equal(1, calls);
        await using var verify = fixture.Open();
        Assert.Empty(await verify.MoneyBalanceReconciliationResults.ToListAsync());
        Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(verify));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationScheduler_SaveFailureRollsBackAndNextRunCanRetry(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using (var setup = fixture.Open()) await SeedReconciliationFactAsync(setup, fixture.Seed);
        await using var failingDb = fixture.Open(new ReconciliationAfterSave(() => throw new InvalidOperationException("synthetic reconciliation save failure")));
        var services = new ServiceCollection();
        services.AddSingleton(failingDb);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        await using var provider = services.BuildServiceProvider();
        var runner = new MoneyBalanceReconciliationSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoneyBalanceReconciliationSchedulerService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunForDateAsync(BankScopeDate.Date, default));
        await using var verify = fixture.Open();
        Assert.Empty(await verify.MoneyBalanceReconciliationResults.ToListAsync());
        Assert.Equal(100m, (await verify.MoneyOperations.SingleAsync()).Amount);
        var retry = await RunIdentityReconciliationAsync(verify, fixture.Seed.Actor.Id, BankScopeDate.Date);
        Assert.Equal(1, retry.Created);
        Assert.Equal(100m, Assert.Single(retry.Items).ClosingBalance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationHistory_ProtectsAccountCurrencyAfterMoneyRowsAreRemoved(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        await SeedReconciliationFactAsync(db, fixture.Seed);
        await RunIdentityReconciliationAsync(db, fixture.Seed.Actor.Id, BankScopeDate.Date);
        await db.MoneyOperations.ExecuteDeleteAsync();
        var account = await db.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == fixture.Seed.Own.Id);
        account.BankConnectionAccountId = null;
        account.Currency = "USD";
        var action = await CreateMoneyAccountsController(db, fixture.Seed.Actor.Id).Update(account.Id, account, default);
        Assert.IsType<ConflictObjectResult>(action.Result);
        db.ChangeTracker.Clear();
        Assert.Equal("RUB", (await db.MoneyAccounts.SingleAsync(item => item.Id == account.Id)).Currency);
        Assert.Equal(100m, (await db.MoneyBalanceReconciliationResults.SingleAsync()).ClosingBalance);
    }

    private sealed class ReconciliationBeforeTransaction(Func<Task> callback) : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            await callback();
            return result;
        }
    }

    private sealed class ReconciliationAfterSave(Func<Task> callback) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            await callback();
            return result;
        }
    }

    public static IEnumerable<object[]> ReconciliationIdentityCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "same-name", "currency", "rename", "opening-only", "legacy-read", "legacy-write", "repeat" }
        select new object[] { postgres, scenario };

    [Theory]
    [MemberData(nameof(ReconciliationIdentityCases))]
    public async Task ReconciliationIdentity_SeparatesAccountsAndProtectsSavedHistory(bool postgres, string scenario)
    {
        var restricted = scenario is "legacy-read" or "legacy-write";
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: !restricted);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == seed.Actor.RoleId
            && item.Section == RolePermissionCatalog.Payments);
        permission.CanEdit = true;
        permission.ViewScope = DataVisibilityScope.All;
        await db.SaveChangesAsync();

        var day = BankScopeDate.Date;
        var accountName = seed.Own.Name;
        MoneyOperation Operation(int? accountId, decimal amount, string currency = "RUB", DateTime? date = null) => new()
        {
            MoneyAccountId = accountId,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = accountName,
            Amount = amount,
            Currency = currency,
            Date = date ?? day,
            ResponsibleUserId = seed.Actor.Id,
        };

        if (restricted)
        {
            var historical = new MoneyBalanceReconciliationResult
            {
                Date = day,
                Source = MoneyOperationSource.Bank,
                AccountName = accountName,
                Income = 900m,
                ClosingBalance = 900m,
                IncomeCount = 1,
            };
            db.MoneyBalanceReconciliationResults.Add(historical);
            if (scenario == "legacy-write") db.MoneyOperations.Add(Operation(seed.Own.Id, 100m));
            await db.SaveChangesAsync();
            var controller = CreateController(db, seed.Actor.Id, isAdminClaim: false);
            if (scenario == "legacy-read")
            {
                var action = await controller.GetBalanceReconciliationResults(day, day, MoneyOperationSource.Bank, null);
                var ok = Assert.IsType<OkObjectResult>(action.Result);
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<MoneyBalanceReconciliationResultDto>>(ok.Value));
            }
            else
            {
                var facts = await CaptureReconciliationMoneyFactsAsync(db);
                var run = await RunIdentityReconciliationAsync(db, seed.Actor.Id, day);
                var original = await db.MoneyBalanceReconciliationResults.AsNoTracking().SingleAsync(item => item.Id == historical.Id);
                Assert.Equal(900m, original.Income);
                Assert.Equal(900m, original.ClosingBalance);
                Assert.Equal(1, run.Created);
                Assert.Equal(0, run.Updated);
                Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(db));
            }
            return;
        }

        if (scenario is "same-name" or "repeat")
        {
            var otherAccount = await db.MoneyAccounts.SingleAsync(item => item.Id == seed.Foreign.Id);
            otherAccount.Name = accountName;
            db.MoneyOperations.AddRange(Operation(seed.Own.Id, 100m), Operation(seed.Foreign.Id, 900m));
        }
        else if (scenario == "currency")
        {
            // Historical operations without account IDs must still keep currencies apart.
            db.MoneyOperations.AddRange(Operation(null, 100m, "RUB"), Operation(null, 900m, "USD"));
        }
        else
        {
            db.MoneyOperations.Add(Operation(seed.Own.Id, 100m, date: scenario == "opening-only" ? day.AddDays(-1) : day));
        }
        await db.SaveChangesAsync();
        var before = await CaptureReconciliationMoneyFactsAsync(db);
        var first = await RunIdentityReconciliationAsync(db, seed.Actor.Id, day);
        Assert.Equal(before, await CaptureReconciliationMoneyFactsAsync(db));

        if (scenario == "opening-only")
        {
            var result = Assert.Single(first.Items);
            Assert.Equal(100m, result.OpeningBalance);
            Assert.Equal(0m, result.Income);
            Assert.Equal(0m, result.Expense);
            Assert.Equal(100m, result.ClosingBalance);
            return;
        }
        if (scenario == "rename")
        {
            Assert.Single(first.Items);
            var account = await db.MoneyAccounts.SingleAsync(item => item.Id == seed.Own.Id);
            account.Name = "Новое название того же счёта";
            accountName = account.Name;
            db.MoneyOperations.Add(Operation(seed.Own.Id, 50m));
            await db.SaveChangesAsync();
            var facts = await CaptureReconciliationMoneyFactsAsync(db);
            var second = await RunIdentityReconciliationAsync(db, seed.Actor.Id, day);
            var result = Assert.Single(second.Items);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Updated);
            Assert.Equal(150m, result.Income);
            Assert.Single(await db.MoneyBalanceReconciliationResults.AsNoTracking().ToListAsync());
            Assert.Equal(facts, await CaptureReconciliationMoneyFactsAsync(db));
            return;
        }

        Assert.Equal(2, first.Created);
        Assert.Equal(new[] { 100m, 900m }, first.Items.Select(item => item.Income).Order().ToArray());
        Assert.Equal(2, await db.MoneyBalanceReconciliationResults.CountAsync());
        if (scenario == "repeat")
        {
            var second = await RunIdentityReconciliationAsync(db, seed.Actor.Id, day);
            Assert.Equal(0, second.Created);
            Assert.Equal(2, second.Updated);
            Assert.Equal(new[] { 100m, 900m }, second.Items.Select(item => item.Income).Order().ToArray());
            Assert.Equal(2, await db.MoneyBalanceReconciliationResults.CountAsync());
            Assert.Equal(before, await CaptureReconciliationMoneyFactsAsync(db));
        }
    }

    private static async Task<MoneyBalanceReconciliationRunResultDto> RunIdentityReconciliationAsync(
        PaymentContext db, int userId, DateTime day)
    {
        var action = await CreateController(db, userId, isAdminClaim: false)
            .RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null);
        return Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
    }

    private static async Task<string> CaptureReconciliationMoneyFactsAsync(PaymentContext db)
        => JsonSerializer.Serialize(await db.MoneyOperations.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.MoneyAccountId, item.Amount, item.Currency, item.Date, item.Source, item.Direction }).ToArrayAsync());
}

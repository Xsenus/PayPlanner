using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    public static IEnumerable<object[]> NativeUnsupportedAmountCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "fractional", "over-schema", "decimal-max", "decimal-min" }
        from booked in new[] { false, true }
        select new object[] { postgres, scenario, booked };

    public static IEnumerable<object[]> NativeSupportedAmountCases =>
        from postgres in new[] { false, true }
        from scenario in new[] { "zero", "cent", "negative-cent", "standard", "negative-standard", "trailing-zero", "safe-large-integer", "large-integer", "maximum" }
        select new object[] { postgres, scenario };

    private static decimal StructuredNativeAmount(string scenario) => scenario switch
    {
        "fractional" => 1.234m,
        "over-schema" => 10000000000000000m,
        "decimal-max" => decimal.MaxValue,
        "decimal-min" => decimal.MinValue,
        "zero" => 0m,
        "cent" => 0.01m,
        "negative-cent" => -0.01m,
        "standard" => 100.12m,
        "negative-standard" => -100.12m,
        "trailing-zero" => 1.2300m,
        "safe-large-integer" => 999999999999999m,
        "large-integer" => 9999999999999999m,
        "maximum" => 9999999999999999.99m,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static async Task AssertNativeAmountFailureAsync(PaymentContext db, BankStatementSyncResult result,
        decimal amount, int expectedPreserved = 0)
    {
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.CountAsync();
        var money = await db.MoneyOperations.IgnoreQueryFilters().CountAsync();
        Assert.False(string.IsNullOrWhiteSpace(result.Error),
            $"Expected a controlled amount error for {amount.ToString(CultureInfo.InvariantCulture)}; persisted raw={raw}, money={money}");
        Assert.InRange(result.Error!.Length, 1, 1000);
        Assert.Equal(expectedPreserved, raw);
        Assert.Equal(expectedPreserved, money);
        Assert.Equal(expectedPreserved, await db.BankStatementImportRows.CountAsync());
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
        Assert.NotNull(result.BatchId);
        var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == result.BatchId);
        Assert.Null(batch.CompletedAt);
        Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, batch.Status);
        Assert.Equal("error", (await db.BankSyncLogs.AsNoTracking().Where(item => item.Kind == "statement")
            .OrderByDescending(item => item.Id).FirstAsync()).Result);
    }

    [Theory]
    [MemberData(nameof(NativeUnsupportedAmountCases))]
    public async Task SharedNative_InvalidAmountCannotRoundRawOrCommitAnyNeighbor(bool postgres, string scenario, bool booked)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var amount = StructuredNativeAmount(scenario);
        fixture.Bank.Pages["shared"] = [[Op("must-not-commit-amount-neighbor", 8, 7m), Op("invalid-native-amount", 2, amount, booked)]];
        var invocation = BankSyncInvocation.Scheduled("invalid-native-amount");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
            await AssertNativeAmountFailureAsync(database.Db, result, amount);
        }
        Assert.Equal(1, await database.Db.BankStatementImportBatches.CountAsync());
    }

    [Theory]
    [MemberData(nameof(NativeSupportedAmountCases))]
    public async Task SharedNative_AmountBoundaryIsExactOrRejectedBeforeRawWrite(bool postgres, string scenario)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var amount = StructuredNativeAmount(scenario);
        fixture.Bank.Pages["shared"] = [[Op("native-amount-boundary", 2, amount)]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("native-amount-boundary-first"), default);
        if (!postgres && scenario is ("maximum" or "large-integer"))
        {
            var persisted = await db.BankStatementOperations.AsNoTracking().Select(item => item.Amount).Take(1).ToArrayAsync();
            Assert.False(string.IsNullOrWhiteSpace(result.Error),
                $"SQLite must reject loss before raw write; requested={amount.ToString(CultureInfo.InvariantCulture)}; raw="
                + string.Join(",", persisted.Select(item => item.ToString(CultureInfo.InvariantCulture))));
            await AssertNativeAmountFailureAsync(db, result, amount);
            return;
        }
        Assert.Null(result.Error);
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.AsNoTracking().SingleAsync();
        Assert.Equal(Math.Abs(amount), raw.Amount);
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        if (amount == 0m) Assert.Empty(money);
        else Assert.Equal(Math.Abs(amount), Assert.Single(money).Amount);
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("native-amount-boundary-repeat"), default)).Error);
        Assert.Equal(raw.Id, (await db.BankStatementOperations.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(Math.Abs(amount), (await db.BankStatementOperations.AsNoTracking().SingleAsync()).Amount);
        var repeatedMoney = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        if (amount == 0m) Assert.Empty(repeatedMoney);
        else
        {
            Assert.Equal(Assert.Single(money).Id, Assert.Single(repeatedMoney).Id);
            Assert.Equal(Math.Abs(amount), Assert.Single(repeatedMoney).Amount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_CorrectedAmountResumesOriginalFailedRequestExactlyOnce(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var source = Op("corrected-native-amount", 2, 1.234m);
        var neighbor = Op("corrected-native-neighbor", 3, 7m);
        fixture.Bank.Pages["shared"] = [[neighbor, source]];
        var invocation = BankSyncInvocation.Scheduled("correct-native-amount");
        var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        await AssertNativeAmountFailureAsync(database.Db, failed, source.Amount);
        fixture.Bank.Pages["shared"] = [[neighbor, source with { Amount = 12.34m }]];
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(failed.BatchId, resumed.BatchId);
        Assert.Equal(2, await database.Db.BankStatementOperations.CountAsync());
        var before = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(new[] { 7m, 12.34m }, before.Select(item => item.Amount).OrderBy(item => item));
        Assert.NotNull((await database.Db.BankStatementImportBatches.AsNoTracking().SingleAsync()).CompletedAt);
        var replay = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(replay.Error);
        Assert.Equal(resumed.BatchId, replay.BatchId);
        Assert.Equal(before.Select(item => item.Id), await database.Db.MoneyOperations.IgnoreQueryFilters().OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(2, await database.Db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_InvalidLaterAmountPreservesEarlierPageAndCanResume(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var first = Op("preserved-before-invalid-amount", 1, 9m);
        var invalid = Op("later-invalid-amount", 2, 1.234m);
        var neighbor = Op("later-amount-neighbor", 3, 7m);
        fixture.Bank.Pages["shared"] = [[first], [neighbor, invalid]];
        var invocation = BankSyncInvocation.Scheduled("later-invalid-amount");
        var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        await AssertNativeAmountFailureAsync(database.Db, failed, invalid.Amount, expectedPreserved: 1);
        var rawId = (await database.Db.BankStatementOperations.AsNoTracking().SingleAsync()).Id;
        var moneyId = (await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;
        fixture.Bank.Pages["shared"] = [[first], [neighbor, invalid with { Amount = 12.34m }]];
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(failed.BatchId, resumed.BatchId);
        Assert.Equal(3, await database.Db.BankStatementOperations.CountAsync());
        Assert.Equal(3, await database.Db.MoneyOperations.IgnoreQueryFilters().CountAsync());
        Assert.True(await database.Db.BankStatementOperations.AnyAsync(item => item.Id == rawId && item.Amount == first.Amount));
        Assert.True(await database.Db.MoneyOperations.IgnoreQueryFilters().AnyAsync(item => item.Id == moneyId && item.Amount == first.Amount));
        Assert.Equal(3, await database.Db.BankStatementImportRows.CountAsync());
        Assert.NotNull((await database.Db.BankStatementImportBatches.AsNoTracking().SingleAsync()).CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_AmountValidationCrossesChunkBoundaryAndKeepsReplayExact(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var expected = Enumerable.Range(0, 501).Select(index => index + 0.01m).ToArray();
        fixture.Bank.Pages["shared"] = [expected.Select((amount, index) => Op($"native-amount-chunk-{index}", 2, amount)).ToList()];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("native-amount-chunk-first"), default)).Error);
        var rawAmounts = await database.Db.BankStatementOperations.AsNoTracking().Select(item => item.Amount).ToArrayAsync();
        var moneyAmounts = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().Select(item => item.Amount).ToArrayAsync();
        Assert.Equal(expected, rawAmounts.OrderBy(item => item));
        Assert.Equal(expected, moneyAmounts.OrderBy(item => item));
        var originalIds = await database.Db.MoneyOperations.IgnoreQueryFilters().OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("native-amount-chunk-repeat"), default)).Error);
        Assert.Equal(501, await database.Db.BankStatementOperations.CountAsync());
        Assert.Equal(originalIds, await database.Db.MoneyOperations.IgnoreQueryFilters().OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal(1002, await database.Db.BankStatementImportRows.CountAsync());
    }
}

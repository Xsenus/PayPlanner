using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    public static IEnumerable<object[]> NativeBookedConflictCases =>
        from postgres in new[] { false, true }
        from change in new[] { "amount", "date", "direction" }
        from manual in new[] { false, true }
        select new object[] { postgres, change, manual };

    public static IEnumerable<object[]> NativeSamePageConflictCases =>
        from postgres in new[] { false, true }
        from change in new[] { "amount", "date", "direction" }
        from reverse in new[] { false, true }
        select new object[] { postgres, change, reverse };

    public static IEnumerable<object[]> NativeCrossPageConflictCases =>
        from postgres in new[] { false, true }
        from change in new[] { "amount", "date", "direction" }
        select new object[] { postgres, change };

    private static BankOperationInfo ChangeNativeFinancialFact(BankOperationInfo original, string change)
        => change switch
        {
            "amount" => original with { Amount = original.Amount + 50m },
            "date" => original with { Date = original.Date.AddDays(1) },
            "direction" => original with { IsIncome = !original.IsIncome },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

    private static async Task AssertNativeConflictAsync(PaymentContext db, BankStatementSyncResult response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error)) return;
        var rawCount = await db.BankStatementOperations.CountAsync();
        var moneyCount = await db.MoneyOperations.IgnoreQueryFilters().CountAsync();
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
            .Take(5).Select(item => new { item.Id, item.Amount, item.Date, item.Direction }).ToArrayAsync();
        Assert.Fail($"Expected a controlled native conflict; persisted raw={rawCount}, money={moneyCount}; facts="
            + string.Join(";", money.Select(item => $"{item.Id}:{item.Amount}:{item.Date:O}:{item.Direction}")));
    }

    [Theory]
    [MemberData(nameof(NativeBookedConflictCases))]
    public async Task SharedNative_BookedConflictAcrossRunsPreservesFinancialFactAndReportsFailure(
        bool postgres, string change, bool manual)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var original = Op("authoritative-one", 2);
        fixture.Bank.Pages["shared"] = [[original]];
        var principal = manual ? (await AddNativeAdminAsync(db)).Principal : null;
        BankSyncInvocation Invocation(string execution)
            => principal is null ? BankSyncInvocation.Scheduled(execution)
                : BankSyncInvocation.ForUser(principal, Guid.NewGuid());
        var first = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, Invocation("original"), default);
        Assert.Null(first.Error);
        var rawId = (await db.BankStatementOperations.AsNoTracking().SingleAsync()).Id;
        var moneyId = (await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;
        var watermark = (await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark;

        // The later unrelated row must not be committed before discovering the conflicting ID in this page.
        fixture.Bank.Pages["shared"] = [[Op("must-not-commit", 8), ChangeNativeFinancialFact(original, change)]];
        var invocation = Invocation("conflicting-reply");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var conflict = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
            await AssertNativeConflictAsync(db, conflict);
            Assert.NotNull(conflict.BatchId);
            db.ChangeTracker.Clear();
            var raw = await db.BankStatementOperations.AsNoTracking().SingleAsync();
            Assert.Equal(rawId, raw.Id);
            Assert.Equal(original.Amount, raw.Amount);
            Assert.Equal(original.Date, raw.OperationDate);
            Assert.Equal(original.IsIncome, raw.IsIncome);
            var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            Assert.Equal(moneyId, money.Id);
            Assert.Equal(original.Amount, money.Amount);
            Assert.Equal(original.Date, money.Date);
            Assert.Equal(MoneyOperationDirection.Income, money.Direction);
            Assert.Equal(watermark, (await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
            var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == conflict.BatchId);
            Assert.Null(batch.CompletedAt);
            Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, batch.Status);
            Assert.Empty(await db.BankStatementImportRows.AsNoTracking().Where(item => item.BatchId == batch.Id).ToArrayAsync());
            Assert.Equal("error", (await db.BankSyncLogs.AsNoTracking().Where(item => item.Kind == "statement").OrderByDescending(item => item.Id).FirstAsync()).Result);
        }
        Assert.Equal(2, await db.BankStatementImportBatches.CountAsync());
        Assert.Equal(1, await db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [MemberData(nameof(NativeSamePageConflictCases))]
    public async Task SharedNative_ContradictorySamePageIdsNeverPickAnArbitraryFinancialFact(
        bool postgres, string change, bool reverse)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var original = Op("same-page", 2);
        var changed = ChangeNativeFinancialFact(original, change);
        fixture.Bank.Pages["shared"] = [reverse ? [changed, original] : [original, changed]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("page-conflict"), default);
        await AssertNativeConflictAsync(db, result);
        Assert.NotNull(result.BatchId);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.BankStatementOperations.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.BankStatementImportRows.AsNoTracking().ToArrayAsync());
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
        var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync();
        Assert.Null(batch.CompletedAt);
        Assert.Equal(BankStatementImportBatchStatus.AppliedWithErrors, batch.Status);
        Assert.Equal("error", (await db.BankSyncLogs.AsNoTracking().SingleAsync(item => item.Kind == "statement")).Result);
    }

    [Theory]
    [MemberData(nameof(NativeCrossPageConflictCases))]
    public async Task SharedNative_ConflictingLaterPagePreservesFirstPageAndCanResumeSafely(bool postgres, string change)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var db = database.Db;
        var original = Op("first-page", 2);
        fixture.Bank.Pages["shared"] = [[original], [Op("must-not-commit", 8), ChangeNativeFinancialFact(original, change)]];
        var invocation = BankSyncInvocation.Scheduled("resume-after-conflict");
        var failed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        await AssertNativeConflictAsync(db, failed);
        db.ChangeTracker.Clear();
        var raw = await db.BankStatementOperations.AsNoTracking().SingleAsync();
        var money = await db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(original.Amount, raw.Amount);
        Assert.Equal(original.Amount, money.Amount);
        Assert.Equal(original.Date, raw.OperationDate);
        Assert.Equal(original.Date, money.Date);
        Assert.Equal(original.IsIncome, raw.IsIncome);
        Assert.Equal(MoneyOperationDirection.Income, money.Direction);
        Assert.Null((await db.BankConnectionAccounts.AsNoTracking().SingleAsync()).StatementWatermark);
        Assert.Null((await db.BankStatementImportBatches.AsNoTracking().SingleAsync()).CompletedAt);

        fixture.Bank.Pages["shared"] = [[original], [original, Op("valid-later", 3)]];
        var resumed = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(failed.BatchId, resumed.BatchId);
        Assert.Equal(2, await db.BankStatementOperations.CountAsync());
        Assert.Equal(2, await db.MoneyOperations.IgnoreQueryFilters().CountAsync());
        Assert.True(await db.BankStatementOperations.AnyAsync(item => item.Id == raw.Id && item.Amount == original.Amount));
        Assert.True(await db.MoneyOperations.IgnoreQueryFilters().AnyAsync(item => item.Id == money.Id && item.Amount == original.Amount));
        Assert.Equal(2, await db.BankStatementImportRows.CountAsync());
        Assert.NotNull((await db.BankStatementImportBatches.AsNoTracking().SingleAsync()).CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_DistinctAuthoritativeIdsMayShareEveryFinancialAttribute(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var first = Op("different-one", 2);
        fixture.Bank.Pages["shared"] = [[first, first with { ExternalId = "different-two" }]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("distinct-facts"), default);
        Assert.Null(result.Error);
        Assert.Equal(2, await database.Db.BankStatementOperations.CountAsync());
        Assert.Equal(2, await database.Db.MoneyOperations.IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, await database.Db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_ExactSamePageDuplicateAndNewRunKeepOneFinancialFact(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var first = Op("exact-one", 2);
        fixture.Bank.Pages["shared"] = [[first, first with { ExternalId = " exact-one " }]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("exact-first"), default)).Error);
        var originalId = (await database.Db.MoneyOperations.SingleAsync()).Id;
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("exact-repeat"), default)).Error);
        Assert.Equal(originalId, (await database.Db.MoneyOperations.IgnoreQueryFilters().SingleAsync()).Id);
        Assert.Equal(1, await database.Db.BankStatementOperations.CountAsync());
        Assert.Equal(2, await database.Db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_PendingFinancialUpdateCanBookExactlyOnce(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var pending = Op("pending-change", 2, booked: false);
        fixture.Bank.Pages["shared"] = [[pending]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("pending-first"), default)).Error);
        var rawId = (await database.Db.BankStatementOperations.SingleAsync()).Id;
        Assert.Empty(await database.Db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        fixture.Bank.Pages["shared"] = [[pending with { Amount = 150m, IsBooked = true }]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("book-updated"), default)).Error);
        Assert.Equal(rawId, (await database.Db.BankStatementOperations.SingleAsync()).Id);
        Assert.Equal(150m, (await database.Db.MoneyOperations.IgnoreQueryFilters().SingleAsync()).Amount);
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("book-overlap"), default)).Error);
        Assert.Equal(1, await database.Db.MoneyOperations.IgnoreQueryFilters().CountAsync());
    }
}

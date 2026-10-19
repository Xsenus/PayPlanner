using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedNative_SamePagePendingSnapshotCannotReplaceConfirmedFinancialFact(bool postgres, bool reverse)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var pending = Op("same-page-pending", 2, booked: false);
        var booked = pending with { Amount = 150m, IsBooked = true };
        fixture.Bank.Pages["shared"] = [reverse ? [booked, pending] : [pending, booked]];
        var result = await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("pending-and-confirmed"), default);
        Assert.Null(result.Error);
        database.Db.ChangeTracker.Clear();
        var raw = await database.Db.BankStatementOperations.AsNoTracking().SingleAsync();
        var money = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().ToArrayAsync();
        Assert.True(raw.IsBooked, $"Expected the confirmed snapshot; persisted amount={raw.Amount}, money={money.Length}");
        Assert.Equal(150m, raw.Amount);
        Assert.Equal(150m, Assert.Single(money).Amount);
        Assert.Equal(1, await database.Db.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_StalePendingReplyCannotUndoConfirmedFinancialFact(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var booked = Op("stale-pending", 2);
        fixture.Bank.Pages["shared"] = [[booked]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("confirmed-first"), default)).Error);
        var moneyId = (await database.Db.MoneyOperations.AsNoTracking().SingleAsync()).Id;
        fixture.Bank.Pages["shared"] = [[booked with { Amount = 150m, Date = booked.Date.AddDays(1), IsBooked = false }]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("stale-pending-later"), default)).Error);
        database.Db.ChangeTracker.Clear();
        var raw = await database.Db.BankStatementOperations.AsNoTracking().SingleAsync();
        Assert.True(raw.IsBooked);
        Assert.Equal(booked.Amount, raw.Amount);
        Assert.Equal(booked.Date, raw.OperationDate);
        var money = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(moneyId, money.Id);
        Assert.Equal(booked.Amount, money.Amount);
        Assert.Equal(booked.Date, money.Date);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_EquivalentSignedAmountsKeepOneConfirmedFinancialFact(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var original = Op("signed-replay", 2);
        var signed = original with { Amount = -original.Amount };
        fixture.Bank.Pages["shared"] = [[original, signed]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("signed-first"), default)).Error);
        var moneyId = (await database.Db.MoneyOperations.AsNoTracking().SingleAsync()).Id;
        fixture.Bank.Pages["shared"] = [[signed]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("signed-repeat"), default)).Error);
        Assert.Equal(original.Amount, (await database.Db.BankStatementOperations.AsNoTracking().SingleAsync()).Amount);
        var money = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(moneyId, money.Id);
        Assert.Equal(original.Amount, money.Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_DescriptiveReplayKeepsOriginalConfirmedFinancialSnapshot(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var original = Op("descriptive-replay", 2);
        fixture.Bank.Pages["shared"] = [[original]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("description-first"), default)).Error);
        var moneyId = (await database.Db.MoneyOperations.AsNoTracking().SingleAsync()).Id;
        fixture.Bank.Pages["shared"] = [[original with { CounterpartyName = "Уточнённое название", Purpose = "Уточнённое описание" }]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("description-repeat"), default)).Error);
        database.Db.ChangeTracker.Clear();
        var raw = await database.Db.BankStatementOperations.AsNoTracking().SingleAsync();
        Assert.Equal(original.CounterpartyName, raw.CounterpartyName);
        Assert.Equal(original.Purpose, raw.Purpose);
        Assert.Equal(moneyId, (await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedNative_ExactTimestampReplayRespectsDatabasePrecision(bool postgres)
    {
        var fixture = await CreateSharedAsync(postgres);
        await using var database = fixture.Database;
        var original = Op("precise-timestamp", 2);
        original = original with { Date = original.Date.AddTicks(7) };
        fixture.Bank.Pages["shared"] = [[original]];
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("timestamp-first"), default)).Error);
        database.Db.ChangeTracker.Clear();
        var beforeRaw = await database.Db.BankStatementOperations.AsNoTracking().SingleAsync();
        var beforeMoney = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Null((await fixture.Service.SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo,
            BankSyncInvocation.Scheduled("timestamp-repeat"), default)).Error);
        database.Db.ChangeTracker.Clear();
        var afterRaw = await database.Db.BankStatementOperations.AsNoTracking().SingleAsync();
        var afterMoney = await database.Db.MoneyOperations.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(beforeRaw.Id, afterRaw.Id);
        Assert.Equal(beforeRaw.OperationDate, afterRaw.OperationDate);
        Assert.Equal(beforeMoney.Id, afterMoney.Id);
        Assert.Equal(beforeMoney.Date, afterMoney.Date);
    }
}

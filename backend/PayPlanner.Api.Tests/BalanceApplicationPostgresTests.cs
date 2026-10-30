using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInspect_SeesCommittedOnlyAfterThePendingWriterFinishes(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var request = Request(fixture);
        var barrier = new HoldFirstBalanceRead();
        await using var writer = Open(database.ConnectionString, barrier);
        await using var reader = Open(database.ConnectionString);
        var pending = ApplyAsync(writer, fixture, request);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.IsType<ConflictObjectResult>(await BalanceApplicationInspectionTests.InspectAsync(reader, fixture, request.RequestId!.Value).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { barrier.Release.TrySetResult(); }
        var applied = Result(await pending);
        var status = BalanceApplicationInspectionTests.InspectResult(await BalanceApplicationInspectionTests.InspectAsync(reader, fixture, request.RequestId!.Value));
        Assert.Equal("Committed", status.State); Assert.NotNull(status.Result);
        Assert.Equal(applied.AppliedAmount, status.Result.AppliedAmount); Assert.Equal(applied.Balance, status.Result.Balance);
        Assert.Equal(25m, (await reader.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await reader.BalanceApplicationReceipts.ToArrayAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReplay_UsesOneReceiptAndOneDebit(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        var first = Result(await ApplyAsync(db, fixture, request));
        var repeated = Result(await ApplyAsync(db, fixture, request));
        Assert.False(first.IsReplay); Assert.True(repeated.IsReplay);
        Assert.Equal(first.AppliedAmount, repeated.AppliedAmount); Assert.Equal(first.Balance, repeated.Balance);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Single(await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTwoConnections_PendingReplayCannotApplyTwice(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var barrier = new HoldFirstBalanceRead();
        await using var first = Open(database.ConnectionString, barrier);
        await using var second = Open(database.ConnectionString);
        var request = Request(fixture);
        var pending = ApplyAsync(first, fixture, request);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(second, fixture, request).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.False(Result(await pending).IsReplay);
        Assert.True(Result(await ApplyAsync(second, fixture, request)).IsReplay);
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(25m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task NativeCommitUnknownAcknowledgement_RecoversWithoutAnotherDebit()
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, false);
        var request = Request(fixture);
        await using (var failing = Open(database.ConnectionString, new LoseAcknowledgement()))
            await Assert.ThrowsAsync<IOException>(() => ApplyAsync(failing, fixture, request));
        await using var fresh = Open(database.ConnectionString);
        Assert.True(Result(await ApplyAsync(fresh, fixture, request)).IsReplay);
        Assert.Equal(25m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    public async Task NativeCommitDefiniteAbort_ConflictsAndRollsBackBeforeRetry(string sqlState)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, false);
        var request = Request(fixture);
        await using (var failing = Open(database.ConnectionString, new AbortCommit(sqlState)))
            Assert.IsType<ConflictObjectResult>(await ApplyAsync(failing, fixture, request));
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(0m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.False(Result(await ApplyAsync(fresh, fixture, request)).IsReplay);
        Assert.Equal(25m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Fact]
    public async Task NativeDifferentTargets_CannotOverspendTheSameCounterpartyBalance()
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        int otherId;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, false);
            var other = PaymentFor(fixture); seed.Payments.Add(other); await seed.SaveChangesAsync(); otherId = other.Id;
        }
        var barrier = new HoldFirstBalanceRead();
        await using var first = Open(database.ConnectionString, barrier);
        await using var second = Open(database.ConnectionString);
        var firstRequest = Request(fixture); firstRequest.Amount = 75m;
        var pending = ApplyAsync(first, fixture, firstRequest);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var secondRequest = Request(fixture); secondRequest.PaymentId = otherId; secondRequest.Amount = 75m;
            Assert.Equal(75m, Result(await ApplyAsync(second, fixture, secondRequest)).AppliedAmount);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(await pending);
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(75m, await fresh.Payments.SumAsync(p => p.PaidAmount));
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(25m, await PayPlanner.Api.Services.ClientBalanceService.GetBalanceAsync(fresh, fixture.OwnerId));
    }

    [Fact]
    public async Task NativeReceiptFailure_RollsBackFinancialState()
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, false);
        await using var failing = Open(database.ConnectionString, new FailReceipt());
        await Assert.ThrowsAsync<DbUpdateException>(() => ApplyAsync(failing, fixture, Request(fixture)));
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(0m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Single(await fresh.ClientBalanceTransactions.ToArrayAsync());
    }

    private sealed class HoldFirstBalanceRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("ClientBalanceTransactions", StringComparison.Ordinal) && command.CommandText.Contains("sum", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref arrivals) == 1)
            {
                Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class FailReceipt : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"BalanceApplicationReceipts\"", StringComparison.Ordinal)) throw new IOException("Synthetic receipt insert failure");
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class AbortCommit(string sqlState) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken ct = default)
            => throw new PostgresException("Synthetic definite server abort", "ERROR", "ERROR", sqlState);
    }
    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
            => throw new IOException("Synthetic lost PostgreSQL commit acknowledgement");
    }
}

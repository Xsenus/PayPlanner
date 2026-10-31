using Microsoft.EntityFrameworkCore;
using System.Data;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceSourceMutationTests
{
    [Fact]
    public async Task MissingCreditWithExistingApplicationHistoryCannotBeRecreated()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        await db.ClientBalanceTransactions.Where(row => row.Type == ClientBalanceTransactionType.UnallocatedAdvance).ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, source, 100m, "Do not restore by guessing", DateTime.UtcNow, null));
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInvalidOverpaymentOriginReturnsABusinessErrorThroughTheRealController(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var credit = await db.ClientBalanceTransactions.SingleAsync(); credit.Type = ClientBalanceTransactionType.ManualAdjustment;
        credit.ExternalKey = ClientBalanceService.BuildPaymentOverpaymentKey(seed.Source.PaymentId);
        (await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored;
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await BalanceApplicationPaymentWriterTests.Controller(db, seed.Source).UpdateStatus(seed.Source.PaymentId,
            new() { Status = "Completed", Reason = "Synthetic repeat" }, CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ADisappearedTrackedCreditIsNotSilentlyCreatedAgain()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); _ = await db.ClientBalanceTransactions.SingleAsync();
        await using (var newer = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options))
            await newer.ClientBalanceTransactions.ExecuteDeleteAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 100m, null, DateTime.UtcNow, null));
        Assert.Equal(0, await db.ClientBalanceTransactions.AsNoTracking().CountAsync()); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnusedCreditCanStillBeReducedOrRemoved(bool legal, bool remove)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (remove) await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id);
        else await ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 60m, "Synthetic unused correction", DateTime.UtcNow, null);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        Assert.Equal(remove ? 0m : 60m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(db, seed.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstRefundKeepsTheOriginalCreditAndCreatesOneDebit(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await ClientBalanceService.RefundUnallocatedAdvanceAsync(db, payment, "First synthetic refund", DateTime.UtcNow, null);
        Assert.True(result.Success); Assert.Equal(100m, result.RefundedAmount);
        await db.SaveChangesAsync(); await transaction.CommitAsync();
        Assert.Equal(0m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(db, seed.Source));
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
        Assert.Equal(100m, (await db.ClientBalanceTransactions.SingleAsync(row => row.Direction == ClientBalanceDirection.Credit)).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARepeatedAmountPreservesTheOriginalLedgerExplanation(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 100m, "Must not replace ledger origin", DateTime.UtcNow, null);
        Assert.False(db.ChangeTracker.HasChanges()); await db.SaveChangesAsync(); await transaction.CommitAsync();
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData("advance")]
    [InlineData("overpayment")]
    [InlineData("remove")]
    [InlineData("refund")]
    public async Task DirectWritesRequireTheCallersTransaction(string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            if (action == "advance") await ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 90m, null, DateTime.UtcNow, null);
            if (action == "overpayment") await ClientBalanceService.UpsertPaymentOverpaymentAsync(db, payment, 120m, DateTime.UtcNow, null);
            if (action == "remove") await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id);
            if (action == "refund") await ClientBalanceService.RefundUnallocatedAdvanceAsync(db, payment, null, DateTime.UtcNow, null);
        });
        Assert.Contains("транзакц", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }

    [Fact]
    public async Task PendingLedgerWorkIsRejectedWithoutDiscardingTheCallersEdit()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        var credit = await db.ClientBalanceTransactions.SingleAsync(); credit.Description = "Pending caller explanation";
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 90m, null, DateTime.UtcNow, null));
        Assert.Equal(EntityState.Modified, db.Entry(credit).State); Assert.Equal("Pending caller explanation", credit.Description);
        await transaction.RollbackAsync(); db.ChangeTracker.Clear();
        Assert.Equal("Synthetic advance basis", (await db.ClientBalanceTransactions.SingleAsync()).Description);
    }

    [Fact]
    public async Task AStaleCleanLedgerRowIsRefreshedUnderTheSourceLock()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false);
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); var credit = await db.ClientBalanceTransactions.SingleAsync();
        await using (var newer = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options))
            await newer.ClientBalanceTransactions.ExecuteUpdateAsync(set => set.SetProperty(row => row.Amount, 80m).SetProperty(row => row.Description, "Newer stored explanation"));
        Assert.Equal(100m, credit.Amount);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, 80m, "Old caller must not overwrite", DateTime.UtcNow, null);
        Assert.Equal(80m, credit.Amount); Assert.Equal("Newer stored explanation", credit.Description); Assert.False(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    public static IEnumerable<object[]> ProtectedCases()
    {
        foreach (var legal in new[] { false, true })
            foreach (var action in new[] { "remove", "reduce", "increase-used", "move-owner", "overpayment-remove" }) yield return [legal, action];
    }

    [Theory]
    [MemberData(nameof(ProtectedCases))]
    public async Task DirectSourceMutationCannotEraseUsedMoneyOrMoveItsOwner(bool legal, string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var credit = await db.ClientBalanceTransactions.SingleAsync();
        if (action == "overpayment-remove")
        {
            credit.Type = ClientBalanceTransactionType.Overpayment; credit.ExternalKey = ClientBalanceService.BuildPaymentOverpaymentKey(seed.Source.PaymentId); await db.SaveChangesAsync();
        }
        var request = Request(seed.Target);
        if (action != "overpayment-remove") request.SourcePaymentId = seed.Source.PaymentId;
        Result(await ApplyAsync(db, seed.Target, request));
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Source.PaymentId);
        if (action == "move-owner")
        {
            if (legal) { var owner = new LegalEntity { FullName = "Other synthetic owner", ShortName = "Other" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); payment.LegalEntityId = owner.Id; }
            else { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); payment.ClientId = owner.Id; }
        }
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => action == "remove"
            ? ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id)
            : action == "overpayment-remove" ? ClientBalanceService.UpsertPaymentOverpaymentAsync(db, payment, payment.Amount, DateTime.UtcNow, null)
            : ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, payment, action == "increase-used" ? 150m : action == "move-owner" ? 100m : 10m, "Synthetic change", DateTime.UtcNow, null));
        Assert.Contains("истори", error.Message, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync(); db.ChangeTracker.Clear();
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPartialRefundCannotBeOverwrittenByTheLowLevelService(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = legal ? null : seed.Source.OwnerId,
            LegalEntityId = legal ? seed.Source.OwnerId : null,
            PaymentId = seed.Source.PaymentId,
            Amount = 25m,
            Type = ClientBalanceTransactionType.Refund,
            Direction = ClientBalanceDirection.Debit,
            ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceRefundKey(seed.Source.PaymentId),
            Description = "Original partial refund"
        });
        await db.SaveChangesAsync(); var payment = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await ClientBalanceService.RefundUnallocatedAdvanceAsync(db, payment, "Must not rewrite", DateTime.UtcNow, null);
        Assert.False(result.Success); await db.SaveChangesAsync(); await transaction.CommitAsync();
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
}

public sealed class BalanceSourceMutationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMissingCreditCannotBeRestoredOverUsedHistory(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal); var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId;
        Result(await ApplyAsync(db, seed.Target, request));
        await db.ClientBalanceTransactions.Where(row => row.Type == ClientBalanceTransactionType.UnallocatedAdvance).ExecuteDeleteAsync(); db.ChangeTracker.Clear();
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, source, 100m, null, DateTime.UtcNow, null));
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceMutationHoldsTheSameLockAsSelectedCredit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        await using var writer = Open(database.ConnectionString); await using var applying = Open(database.ConnectionString);
        await using (var transaction = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var source = await writer.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
            await ClientBalanceService.UpsertUnallocatedAdvanceAsync(writer, source, 60m, "Synthetic unused revision", DateTime.UtcNow, null);
            await writer.SaveChangesAsync();
            var attempt = Request(seed.Target); attempt.SourcePaymentId = source.Id;
            Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await ApplyAsync(applying, seed.Target, attempt).WaitAsync(TimeSpan.FromSeconds(10)));
            await transaction.CommitAsync();
        }
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; request.Amount = 75m;
        Assert.Equal(60m, Result(await ApplyAsync(applying, seed.Target, request)).AppliedAmount);
        Assert.Equal(0m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(applying, seed.Source));
        Assert.Equal(60m, (await applying.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Target.PaymentId)).PaidAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedCreditBlocksDirectMutationThenItsHistoryRejectsRevision(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var applying = Open(database.ConnectionString, barrier); await using var writer = Open(database.ConnectionString);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId;
        var pending = ApplyAsync(applying, seed.Target, request); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var transaction = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var source = await writer.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(writer, source, 60m, null, DateTime.UtcNow, null));
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState); await transaction.RollbackAsync(); writer.ChangeTracker.Clear();
        }
        finally { barrier.Release.TrySetResult(); }
        Result(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writer);
        await using (var transaction = await writer.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var source = await writer.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(writer, source, 60m, null, DateTime.UtcNow, null));
            await transaction.RollbackAsync(); writer.ChangeTracker.Clear();
        }
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writer));
        Assert.Equal(75m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(writer, seed.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOlderCleanTrackedCreditIsRefreshedBeforeAnIdempotentRevision(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal); var credit = await db.ClientBalanceTransactions.SingleAsync();
        await using (var newer = Open(database.ConnectionString))
            await newer.ClientBalanceTransactions.ExecuteUpdateAsync(set => set.SetProperty(row => row.Amount, 80m).SetProperty(row => row.Description, "Latest stored explanation"));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        await ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, source, 80m, "Do not replace", DateTime.UtcNow, null);
        Assert.Equal(80m, credit.Amount); Assert.Equal("Latest stored explanation", credit.Description); Assert.False(db.ChangeTracker.HasChanges());
        await db.SaveChangesAsync(); await transaction.CommitAsync();
    }

    [Fact]
    public async Task AWeakerTransactionCannotMutateTheLedger()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, false); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ClientBalanceService.UpsertUnallocatedAdvanceAsync(db, source, 60m, null, DateTime.UtcNow, null));
        Assert.Contains("Serializable", error.Message); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
        await transaction.RollbackAsync();
    }

    private static PostgreSqlPaymentContext Open(string connection, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
}

using System.Data.Common;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InvoiceReconciliationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationOwnsTheGraph_BalanceApplyConflictsThenUsesTheCommittedState(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal); var barrier = new HoldSave(false);
        var pending = Runner(database.ConnectionString, barrier).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await using var applying = Open(database.ConnectionString); Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, f).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).Applied);
        await using var retry = Open(database.ConnectionString); BalanceApplicationIdempotencyTests.Result(await ApplyAsync(retry, f)); Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount); Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.ChildId)).PaidAmount); Assert.Equal(0, (await Runner(database.ConnectionString).RunOnceAsync()).Applied);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BalanceApplyOwnsTheGraph_ReconciliationSkipsThenReadsTheCommittedCredit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal); var barrier = new HoldSave(true); await using var applying = Open(database.ConnectionString, barrier);
        var pending = ApplyAsync(applying, f); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { var result = await Runner(database.ConnectionString).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, result.Applied); Assert.Equal(1, result.Conflicted); }
        finally { barrier.Release.TrySetResult(); }
        BalanceApplicationIdempotencyTests.Result(await pending.WaitAsync(TimeSpan.FromSeconds(20))); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); Assert.Equal(0, (await Runner(database.ConnectionString).RunOnceAsync()).Applied); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCreditMinimumRejectsTheWholeAggregateWithoutAudit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal); InvoiceReconciliationBoundaryTests.AddDebit(db, f, f.HeaderId, 40m); await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        var result = await Runner(database.ConnectionString).RunOnceAsync(); Assert.Equal(0, result.Applied); Assert.Equal(1, result.Skipped); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeErrorAfterSqlRollsBackStateAndAudit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        await Assert.ThrowsAsync<IOException>(() => Runner(database.ConnectionString, new InvoiceReconciliationBoundaryTests.FailAfterSql { Armed = true }).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitDoesNotReapplyTheAlreadyCommittedRepair(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal);
        await Assert.ThrowsAsync<IOException>(() => Runner(database.ConnectionString, new LostAck()).RunOnceAsync()); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.Equal(0, (await Runner(database.ConnectionString).RunOnceAsync()).Applied); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Fact]
    public async Task BusyGraphDoesNotBlockTheNextInvoice_AndLaterEditsRemainVisible()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, false); var next = await InvoiceReconciliationBoundaryTests.AddGraphAsync(db, f); await using var writer = Open(database.ConnectionString); await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {f.HeaderId} FOR UPDATE");
        var result = await Runner(database.ConnectionString).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, result.Conflicted); Assert.Equal(1, result.Applied); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == next.HeaderId)).PaidAmount);
        var child = await writer.Payments.SingleAsync(p => p.Id == f.ChildId); child.PaidAmount = 30m; await writer.SaveChangesAsync(); await transaction.CommitAsync(); Assert.Equal(1, (await Runner(database.ConnectionString).RunOnceAsync()).Applied); Assert.Equal(30m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount);
    }
    [Fact]
    public async Task NewInvoiceBeyondTheInitialBoundaryWaitsForTheNextFiniteSweep()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, false); var barrier = new HoldSave(false); var pending = Runner(database.ConnectionString, barrier).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); InvoiceReconciliationBoundaryTests.Fixture? next = null;
        try { await using var adding = Open(database.ConnectionString); next = await InvoiceReconciliationBoundaryTests.AddGraphAsync(adding, f); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).Applied); Assert.NotNull(next); Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == next.HeaderId)).PaidAmount); Assert.Equal(1, (await Runner(database.ConnectionString).RunOnceAsync()).Applied);
    }
    [Fact]
    public async Task NativeStandaloneInvoiceUsesItsOwnLockedTransaction()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, false); var header = await db.Payments.SingleAsync(p => p.Id == f.HeaderId); header.InvoiceId = null; header.PaidAmount = 25m; header.Status = PaymentStatus.Pending; header.IsPaid = false; await db.SaveChangesAsync();
        Assert.Equal(1, (await Runner(database.ConnectionString).RunOnceAsync()).Applied); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId); Assert.Equal(25m, stored.PaidAmount); Assert.Equal(PaymentStatus.Processing, stored.Status); Assert.Null(stored.InvoiceId);
    }
    [Fact]
    public async Task CancellationUnderTheSharedLockIsNotOverwrittenAfterCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await InvoiceReconciliationBoundaryTests.SeedAsync(db, false); await using var cancelling = Open(database.ConnectionString); await using var transaction = await cancelling.Database.BeginTransactionAsync(); await cancelling.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {f.HeaderId} FOR UPDATE");
        var header = await cancelling.Payments.SingleAsync(p => p.Id == f.HeaderId); header.Status = PaymentStatus.Cancelled; header.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled; await cancelling.SaveChangesAsync(); Assert.Equal(1, (await Runner(database.ConnectionString).RunOnceAsync()).Conflicted); await transaction.CommitAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await Runner(database.ConnectionString).RunOnceAsync(); Assert.Equal(0, result.Applied); Assert.Equal(1, result.Skipped); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    private static Task<IActionResult> ApplyAsync(PaymentContext db, InvoiceReconciliationBoundaryTests.Fixture f)
    {
        var target = new BalanceApplicationIdempotencyTests.Fixture(f.ActorId, f.Legal, f.OwnerId, f.ChildId);
        return BalanceApplicationIdempotencyTests.ApplyAsync(db, target, BalanceApplicationIdempotencyTests.Request(target));
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private static InvoicePaymentStateReconciliationService Runner(string connection, IInterceptor? interceptor = null)
        => new(new FactoryScopes(() => Open(connection, interceptor)), new ConfigurationBuilder().Build(), NullLogger<InvoicePaymentStateReconciliationService>.Instance);
    private sealed class FactoryScopes(Func<PaymentContext> open) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FactoryScope(open());
    }
    private sealed class FactoryScope(PaymentContext db) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => type == typeof(PaymentContext) ? db : null;
        public void Dispose() => db.Dispose();
    }
    private sealed class HoldSave(bool balance) : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var hold = balance ? eventData.Context!.ChangeTracker.Entries<ClientBalanceTransaction>().Any(e => e.State == EntityState.Added && e.Entity.Type == ClientBalanceTransactionType.CreditApplied)
                : eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Modified);
            if (hold) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            return result;
        }
    }
    private sealed class LostAck : DbTransactionInterceptor
    {
        private bool armed = true;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        { if (armed) { armed = false; throw new IOException("Synthetic unknown reconciliation commit"); } return Task.CompletedTask; }
    }
}

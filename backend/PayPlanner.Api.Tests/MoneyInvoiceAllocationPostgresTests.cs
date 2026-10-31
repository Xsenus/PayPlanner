using System.Data.Common;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MoneyInvoiceAllocationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BankAllocationOwnsTheGraph_BalanceConflictsThenBothReceiptsRemain(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db, legal); var barrier = new HoldSave(false); await using var linking = Open(database.ConnectionString, barrier);
        var pending = MoneyInvoiceAllocationBoundaryTests.LinkAsync(linking, f); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await using var applying = Open(database.ConnectionString); Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, f).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20))); await using var retry = Open(database.ConnectionString); BalanceApplicationIdempotencyTests.Result(await ApplyAsync(retry, f));
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.ChildId)).PaidAmount); Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.HeaderId)).PaidAmount); Assert.Equal(25m, (await db.MoneyOperations.SingleAsync()).Amount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BalanceOwnsTheGraph_BankConflictsThenUsesTheCurrentAmount(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db, legal); var barrier = new HoldSave(true); await using var applying = Open(database.ConnectionString, barrier);
        var pending = ApplyAsync(applying, f); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await using var linking = Open(database.ConnectionString); Assert.IsType<ConflictObjectResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(linking, f).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        BalanceApplicationIdempotencyTests.Result(await pending.WaitAsync(TimeSpan.FromSeconds(20))); await using var retry = Open(database.ConnectionString); Assert.IsType<OkObjectResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(retry, f));
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.ChildId)).PaidAmount); Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.HeaderId)).PaidAmount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeErrorAfterSqlRollsBackMoneyLinkPaymentsAndAudit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db, legal); var before = await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync(); await using var writing = Open(database.ConnectionString, new InvoiceReconciliationBoundaryTests.FailAfterSql { Armed = true });
        await Assert.ThrowsAsync<IOException>(() => MoneyInvoiceAllocationBoundaryTests.LinkAsync(writing, f)); Assert.Equal(before, await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommit_RetryOfTheKnownOperationDoesNotApplyItAgain(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db, legal); await using var writing = Open(database.ConnectionString, new LostAck());
        await Assert.ThrowsAsync<IOException>(() => MoneyInvoiceAllocationBoundaryTests.LinkAsync(writing, f)); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.ChildId)).PaidAmount); var before = await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        await using var retry = Open(database.ConnectionString); Assert.IsType<OkObjectResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(retry, f)); Assert.Equal(before, await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Fact]
    public async Task AStaleTrackedGraphIsRefreshedInBulk_WithoutLosingTheLaterNote()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var stale = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(stale);
        await using (var current = Open(database.ConnectionString)) { foreach (var p in await current.Payments.ToArrayAsync()) { p.PaidAmount = 25m; p.Status = PaymentStatus.Processing; p.SystemNotes = "Later synthetic receipt note"; } await current.SaveChangesAsync(); }
        await using var transaction = await stale.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable); var operation = await stale.MoneyOperations.SingleAsync(o => o.Id == f.OperationId); operation.LinkedInvoiceId = f.Payment.InvoiceId; operation.MatchStatus = MoneyOperationMatchStatus.Matched;
        await MoneyOperationInvoiceAllocationService.ApplyAsync(stale, operation, f.Payment.InvoiceId); await transaction.CommitAsync(); stale.ChangeTracker.Clear(); var payments = await stale.Payments.ToArrayAsync(); Assert.All(payments, p => Assert.Equal(50m, p.PaidAmount)); Assert.All(payments, p => Assert.Contains("Later synthetic receipt note", p.SystemNotes));
    }
    [Fact]
    public async Task HiddenInvoiceDoesNotDiscloseItsPaymentLock()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db); (await db.RolePermissions.SingleAsync(p => p.Section == "accounts")).CanView = false; await db.SaveChangesAsync(); await using var busy = Open(database.ConnectionString); await using var transaction = await busy.Database.BeginTransactionAsync(); await busy.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {f.Payment.HeaderId} FOR UPDATE");
        await using var linking = Open(database.ConnectionString); Assert.IsType<ForbidResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(linking, f).WaitAsync(TimeSpan.FromSeconds(10))); Assert.Null((await db.MoneyOperations.SingleAsync()).LinkedInvoiceId);
    }
    [Fact]
    public async Task RecipientPermissionsCannotBeRevokedDuringTheFinancialCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db); var barrier = new HoldSave(false); await using var linking = Open(database.ConnectionString, barrier); var pending = MoneyInvoiceAllocationBoundaryTests.LinkAsync(linking, f); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await using var editor = Open(database.ConnectionString); await using var transaction = await editor.Database.BeginTransactionAsync(); var roleId = (await db.Users.SingleAsync()).RoleId; var error = await Assert.ThrowsAsync<PostgresException>(() => editor.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {roleId} FOR UPDATE NOWAIT")); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
    }
    [Fact]
    public async Task CancellationOwnsTheGraph_BankCannotWriteBeforeOrAfterItsCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var f = await MoneyInvoiceAllocationBoundaryTests.SeedAsync(db); await using var cancelling = Open(database.ConnectionString); await using var transaction = await cancelling.Database.BeginTransactionAsync(); await cancelling.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {f.Payment.HeaderId} FOR UPDATE"); var header = await cancelling.Payments.SingleAsync(p => p.Id == f.Payment.HeaderId); header.Status = PaymentStatus.Cancelled; header.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled; await cancelling.SaveChangesAsync();
        await using (var linking = Open(database.ConnectionString)) Assert.IsType<ConflictObjectResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(linking, f).WaitAsync(TimeSpan.FromSeconds(10))); await transaction.CommitAsync(); var before = await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db);
        await using var retry = Open(database.ConnectionString); Assert.IsType<ForbidResult>(await MoneyInvoiceAllocationBoundaryTests.LinkAsync(retry, f)); Assert.Equal(before, await MoneyInvoiceAllocationBoundaryTests.SnapshotAsync(db));
    }
    private static Task<IActionResult> ApplyAsync(PaymentContext db, MoneyInvoiceAllocationBoundaryTests.Fixture f)
    {
        var target = new BalanceApplicationIdempotencyTests.Fixture(f.Payment.ActorId, f.Payment.Legal, f.Payment.OwnerId, f.Payment.ChildId); return BalanceApplicationIdempotencyTests.ApplyAsync(db, target, BalanceApplicationIdempotencyTests.Request(target));
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
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
        { if (armed) { armed = false; throw new IOException("Synthetic unknown bank allocation commit"); } return Task.CompletedTask; }
    }
}

using System.IO;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class AdvanceReviewBatchBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_ObservesCommittedDecisionAndDisposedTransaction(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = async _ =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
        };
        Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications).BackfillAdvanceReviewCandidates(true, 10));
        Assert.Single(probe.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationFailure_DoesNotUndoCommittedDecision(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = _ => throw new IOException("Synthetic delivery failure after commit");
        await Assert.ThrowsAsync<IOException>(() => BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications).BackfillAdvanceReviewCandidates(true, 10));
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Batch_PreservesForeignPendingChangesAndTransactions(bool rollback, bool external)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, false);
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        if (external)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.IsType<ConflictObjectResult>(await RunAsync(controller, rollback)); Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
        }
        else
        {
            var owner = await db.Clients.SingleAsync(); owner.Name = "Pending unrelated edit";
            Assert.IsType<ConflictObjectResult>(await RunAsync(controller, rollback)); Assert.Equal(EntityState.Modified, db.Entry(owner).State);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnapprovedActor_CannotChangeBatch(bool rollback)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, false);
        (await db.Users.SingleAsync()).IsApproved = false; await db.SaveChangesAsync();
        Assert.IsType<UnauthorizedResult>(await RunAsync(BalanceApplicationPaymentWriterTests.Controller(db, fixture), rollback));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PermissionRevokedAfterControllerCreation_LeavesBatchUnchanged(bool rollback, bool inactive)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, false); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        if (rollback) Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10));
        if (inactive) (await db.Users.SingleAsync()).IsActive = false;
        else (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await RunAsync(controller, rollback); if (inactive) Assert.IsType<UnauthorizedResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackOwnScope_DoesNotTouchNewlyHiddenPayment(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10));
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        if (legal) (await db.LegalEntities.SingleAsync()).ResponsibleUserId = null; else (await db.Clients.SingleAsync()).ResponsibleUserId = null;
        await db.SaveChangesAsync();
        var result = Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await controller.RollbackAdvanceReviewBackfill(true, 10)).Value);
        Assert.Equal(0, result.Eligible); Assert.Equal(0, result.Updated);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_DoesNotEraseLaterManualWork(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10));
        var payment = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); payment.Notes = "Later manual work"; await db.SaveChangesAsync();
        var response = Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await controller.RollbackAdvanceReviewBackfill(true, 10)).Value);
        Assert.Equal(0, response.Updated); Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_LimitsAndOrdersRowsAndPreservesAllMoney(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal);
        var extra = await AddLegacyAsync(db, fixture); extra.Date = extra.Date.AddDays(-1); await db.SaveChangesAsync();
        var before = await MoneyAsync(db); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        var response = Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 0)).Value);
        Assert.Equal(2, response.Eligible); Assert.Equal(200m, response.TotalAmount); Assert.Equal(1, response.Limit); Assert.Equal(extra.Id, Assert.Single(response.Items).Id);
        Assert.Equal(before, await MoneyAsync(db));
        var rollback = Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await controller.RollbackAdvanceReviewBackfill(true, 999)).Value);
        Assert.Equal(500, rollback.Limit); Assert.Equal(1, rollback.Updated); Assert.Equal(before, await MoneyAsync(db));
    }
    [Theory]
    [InlineData(false, "Updated")]
    [InlineData(true, "Updated")]
    [InlineData(false, "Restored")]
    [InlineData(true, "Restored")]
    public async Task Rollback_UsesLatestAuditIdWhenTimestampsTie(bool legal, string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10));
        var original = await db.ObjectAuditEvents.Where(a => a.ObjectType == "payment" && a.ObjectId == fixture.PaymentId && a.Action == "Updated").OrderByDescending(a => a.Id).FirstAsync();
        db.ObjectAuditEvents.Add(new() { ObjectType = "payment", ObjectId = fixture.PaymentId, Action = action, CreatedAt = original.CreatedAt, MetadataJson = "{\"fields\":[\"Notes\"],\"reason\":\"Manual work\"}" }); await db.SaveChangesAsync();
        Assert.Equal(0, Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await controller.RollbackAdvanceReviewBackfill(true, 10)).Value).Updated);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_DoesNotTreatMentionInNotesAsBackfill(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal);
        var payment = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision; payment.Notes = "Backfill mentioned by user"; await db.SaveChangesAsync();
        Assert.Equal(0, Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture).RollbackAdvanceReviewBackfill(true, 10)).Value).Updated);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveFailure_RollsBackWholeBatchAndAuditWithoutNotification(bool legal, bool rollback)
    {
        var fault = new FailAfterBatchSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); var fixture = await SeedLegacyAsync(db, legal); await AddLegacyAsync(db, fixture);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications);
        if (rollback) Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10));
        probe.Calls.Clear(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audits = await db.ObjectAuditEvents.CountAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(controller, rollback));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audits, await db.ObjectAuditEvents.CountAsync()); Assert.Empty(probe.Calls);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailure_RetryDoesNotRepeatCommittedBatch(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedLegacyAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = _ => throw new IOException("Synthetic post-commit failure"); var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications);
        await Assert.ThrowsAsync<IOException>(() => controller.BackfillAdvanceReviewCandidates(true, 10)); var count = await db.ObjectAuditEvents.CountAsync();
        var retry = Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(Assert.IsType<OkObjectResult>(await controller.BackfillAdvanceReviewCandidates(true, 10)).Value);
        Assert.Equal(0, retry.Updated); Assert.Equal(count, await db.ObjectAuditEvents.CountAsync()); Assert.Single(probe.Calls);
    }
    internal static async Task<Payment> AddLegacyAsync(PaymentContext db, Fixture fixture)
    {
        var payment = PaymentFor(fixture); payment.Amount = 100m; payment.PaidAmount = 100m; payment.IsPaid = true; payment.Status = PaymentStatus.Completed;
        db.Payments.Add(payment); await db.SaveChangesAsync(); return payment;
    }
    private static async Task<string> MoneyAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Amount, p.PaidAmount, p.Status, p.IsPaid, p.ClientId, p.LegalEntityId, p.Account, p.InvoiceId }).ToArrayAsync(),
            Ledger = await db.ClientBalanceTransactions.AsNoTracking().CountAsync(),
            Operations = await db.MoneyOperations.AsNoTracking().CountAsync(),
        });
    private sealed class FailAfterBatchSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => Armed ? throw new IOException("Synthetic batch failure after SQL write") : ValueTask.FromResult(result);
    }
    internal static async Task<Fixture> SeedLegacyAsync(PaymentContext db, bool legal)
    {
        var seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        db.ClientBalanceTransactions.RemoveRange(await db.ClientBalanceTransactions.ToArrayAsync());
        (await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.None;
        if (legal) (await db.LegalEntities.SingleAsync()).ResponsibleUserId = seed.Source.ActorId;
        else (await db.Clients.SingleAsync()).ResponsibleUserId = seed.Source.ActorId;
        await db.SaveChangesAsync(); return seed.Source;
    }
    internal static Task<IActionResult> RunAsync(PayPlanner.Api.Controllers.PaymentsController controller, bool rollback)
        => rollback ? controller.RollbackAdvanceReviewBackfill(true, 10) : controller.BackfillAdvanceReviewCandidates(true, 10);
}

public sealed class AdvanceReviewBatchPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BusyLaterRow_RejectsWholeBatchWithoutPartialChanges(bool legal, bool rollback)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int laterId;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal); laterId = (await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture)).Id;
            if (rollback) Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(db, fixture), false));
        }
        await using var blocker = Open(database.ConnectionString); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {laterId} FOR UPDATE");
        var commands = new BatchLockCommands(); await using var writing = Open(database.ConnectionString, commands); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writing); var audits = await writing.ObjectAuditEvents.CountAsync();
        Assert.IsType<ConflictObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(writing, fixture), rollback).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, commands.Count);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writing)); Assert.Equal(audits, await writing.ObjectAuditEvents.CountAsync());
        Assert.Null(writing.Database.CurrentTransaction); Assert.False(writing.ChangeTracker.HasChanges()); await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConcurrentBatches_CommitOnlyOnceAndRetryIsEmpty(bool legal, bool rollback)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal);
            if (rollback) Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(db, fixture), false));
        }
        var barrier = new HoldBatchSave(); await using var first = Open(database.ConnectionString, barrier); await using var second = Open(database.ConnectionString);
        var pending = AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(first, fixture), rollback);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(second, fixture), rollback).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20))); var auditCount = await second.ObjectAuditEvents.CountAsync();
        var retry = Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(second, fixture), rollback));
        Assert.Equal(0, rollback ? Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(retry.Value).Updated : Assert.IsType<PaymentAdvanceReviewBackfillResponseDto>(retry.Value).Updated);
        Assert.Equal(auditCount, await second.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackHoldingPayment_BlocksManualReviewThenPreservesItsDecision(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal);
            Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(db, fixture), false));
        }
        var barrier = new HoldBatchSave(); await using var rollingBack = Open(database.ConnectionString, barrier); await using var reviewing = Open(database.ConnectionString);
        var pending = AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(rollingBack, fixture), true);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, "accept").WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<OkObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, "accept"));
        Assert.Equal(0, Assert.IsType<PaymentAdvanceReviewBackfillRollbackResponseDto>(Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(reviewing, fixture), true)).Value).Updated);
        Assert.Equal(100m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(reviewing, fixture));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_AnotherConnectionSeesCommittedDecision(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = async _ =>
        {
            Assert.Null(db.Database.CurrentTransaction); await using var observer = Open(database.ConnectionString);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await observer.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
            Assert.Contains(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "payment" && a.ObjectId == fixture.PaymentId).ToArrayAsync(), a => a.MetadataJson?.Contains("Backfill ", StringComparison.Ordinal) == true);
        };
        Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture, notifications).BackfillAdvanceReviewCandidates(true, 10)); Assert.Single(probe.Calls);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveFailure_RollsBackReviewAndAuditOnPostgres(bool legal, bool rollback)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
            if (rollback) Assert.IsType<OkObjectResult>(await AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(db, fixture), false));
        }
        await using var writing = Open(database.ConnectionString, new FailAfterSave()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writing); var audits = await writing.ObjectAuditEvents.CountAsync();
        await Assert.ThrowsAsync<IOException>(() => AdvanceReviewBatchBoundaryTests.RunAsync(BalanceApplicationPaymentWriterTests.Controller(writing, fixture), rollback));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(writing)); Assert.Equal(audits, await writing.ObjectAuditEvents.CountAsync()); Assert.Null(writing.Database.CurrentTransaction);
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldBatchSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
    private sealed class BatchLockCommands : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("FROM \"Payments\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE NOWAIT", StringComparison.Ordinal))
            {
                Count++; Assert.Contains("= ANY (", command.CommandText); Assert.Contains("ORDER BY \"Id\"", command.CommandText);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new IOException("Synthetic failure after batch SQL write");
    }
}

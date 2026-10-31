using System.IO;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class AdvanceReviewMonitoringBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Notifications_ObserveSavedDecisionOutsideTransaction(bool legal, bool escalation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal, escalation);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = async _ =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
        };
        var result = await Runner(db, notifications).RunOnceAsync();
        Assert.Equal(1, result.ProcessedCount); Assert.Equal(1, result.MarkedCount); Assert.Equal(1, result.NotifiedCount); Assert.Equal(escalation ? 1 : 0, result.EscalatedCount); Assert.Equal(escalation ? 2 : 1, probe.Calls.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailure_DoesNotUndoDecision(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = _ => throw new IOException("Synthetic notification failure");
        await Assert.ThrowsAsync<IOException>(() => Runner(db, notifications).RunOnceAsync());
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailure_RollsBackDecisionAndAuditAndSendsNothing(bool legal)
    {
        var fault = new FailAfterSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); var fixture = await SeedAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var auditCount = await db.ObjectAuditEvents.CountAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Runner(db, notifications).RunOnceAsync());
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(auditCount, await db.ObjectAuditEvents.CountAsync()); Assert.Empty(probe.Calls); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monitor_PreservesForeignTransactionAndPendingChanges(bool external)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false);
        if (external)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Runner(db).RunOnceAsync());
            Assert.Equal("PayPlanner.Api.Services.Payments.BalanceApplicationConflictException", error.GetType().FullName);
            Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
        }
        else
        {
            var client = await db.Clients.SingleAsync(); client.Name = "Unrelated pending change";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Runner(db).RunOnceAsync());
            Assert.Equal("PayPlanner.Api.Services.Payments.BalanceApplicationConflictException", error.GetType().FullName); Assert.Equal(EntityState.Modified, db.Entry(client).State);
        }
    }
    [Fact]
    public async Task DisabledMonitor_DoesNotWriteOrNotify()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false);
        db.SystemSettings.Add(new() { Key = PaymentAdvanceReviewMonitoringService.EnabledSettingKey, Value = "false" }); await db.SaveChangesAsync();
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create();
        var result = await Runner(db, notifications).RunOnceAsync(); Assert.False(result.Enabled); Assert.Equal(0, result.MarkedCount); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchLimit_WorksWithoutNotificationServiceAndKeepsMoney(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        db.SystemSettings.Add(new() { Key = PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey, Value = "1" }); await db.SaveChangesAsync();
        var before = await MoneyAsync(db); var result = await Runner(db).RunOnceAsync();
        Assert.True(result.Enabled); Assert.Equal(1, result.ProcessedCount); Assert.Equal(1, result.MarkedCount); Assert.Equal(0, result.NotifiedCount); Assert.Equal(before, await MoneyAsync(db));
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
        Assert.Equal(1, await db.Payments.CountAsync(p => p.PaidAmount > 0 && p.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryRetry_DoesNotRepeatSavedDecision(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications; var runner = Runner(db, notifications);
        probe.Before = _ => throw new IOException("Synthetic delivery failure"); await Assert.ThrowsAsync<IOException>(() => runner.RunOnceAsync()); var auditCount = await db.ObjectAuditEvents.CountAsync(); var before = await MoneyAsync(db);
        probe.Before = null; var retry = await runner.RunOnceAsync(); Assert.Equal(0, retry.MarkedCount); Assert.Equal(1, retry.NotifiedCount); Assert.Equal(auditCount, await db.ObjectAuditEvents.CountAsync()); Assert.Equal(before, await MoneyAsync(db)); Assert.Equal(2, probe.Calls.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeSave_RollsBackAndReleasesRunner(bool legal)
    {
        using var cancellation = new CancellationTokenSource(); var fault = new CancelBatchSave(cancellation); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await SeedAsync(db, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var runner = Runner(db, notifications); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); fault.Armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(cancellation.Token));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls);
        fault.Armed = false; Assert.Equal(1, (await runner.RunOnceAsync()).MarkedCount);
    }
    [Theory]
    [InlineData(false, PaymentAdvanceReviewStatus.AcceptedAsAdvance)]
    [InlineData(true, PaymentAdvanceReviewStatus.AcceptedAsAdvance)]
    [InlineData(false, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(true, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(false, PaymentAdvanceReviewStatus.Refunded)]
    [InlineData(true, PaymentAdvanceReviewStatus.Refunded)]
    public async Task ExistingDecision_IsNotOverwrittenOrNotified(bool legal, PaymentAdvanceReviewStatus status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus = status; await db.SaveChangesAsync();
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var result = await Runner(db, notifications).RunOnceAsync();
        Assert.Equal(0, result.ProcessedCount); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls);
    }
    internal static async Task<string> MoneyAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Amount, p.PaidAmount, p.Status, p.IsPaid, p.ClientId, p.LegalEntityId, p.InvoiceId }).ToArrayAsync(),
            Ledger = await db.ClientBalanceTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.Type, t.Direction, t.PaymentId, t.ClientId, t.LegalEntityId, t.ExternalKey }).ToArrayAsync(),
            Operations = await db.MoneyOperations.AsNoTracking().CountAsync(),
        });
    internal static async Task<Fixture> SeedAsync(PaymentContext db, bool legal, bool escalation = false)
    {
        var fixture = await AdvanceReviewBatchBoundaryTests.SeedLegacyAsync(db, legal);
        var payment = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); payment.Date = DateTime.UtcNow.Date.AddDays(-10); payment.PaidDate = payment.Date; payment.LastPaymentDate = payment.Date;
        if (escalation) (await db.Roles.SingleAsync()).Name = "admin";
        await db.SaveChangesAsync(); return fixture;
    }
    internal static PaymentAdvanceReviewMonitoringService Runner(PaymentContext db, INotificationService? notifications = null)
        => new(new BorrowedScopes(db, notifications), new ConfigurationBuilder().Build(), NullLogger<PaymentAdvanceReviewMonitoringService>.Instance);
    private sealed class BorrowedScopes(PaymentContext db, INotificationService? notifications) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new BorrowedScope(db, notifications);
    }
    private sealed class BorrowedScope(PaymentContext db, INotificationService? notifications) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => type == typeof(PaymentContext) ? db : type == typeof(INotificationService) ? notifications : null;
        public void Dispose() { }
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => Armed ? throw new IOException("Synthetic monitor failure after SQL write") : ValueTask.FromResult(result);
    }
    private sealed class CancelBatchSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class AdvanceReviewMonitoringPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "ignore")]
    [InlineData(true, "ignore")]
    public async Task MonitorHoldingPayment_ManualDecisionConflictsThenSucceeds(bool legal, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var monitoring = Open(database.ConnectionString, barrier); await using var reviewing = Open(database.ConnectionString);
        var pending = AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, action).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).MarkedCount);
        Assert.IsType<OkObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, action)); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing);
        Assert.Equal(0, (await AdvanceReviewMonitoringBoundaryTests.Runner(reviewing).RunOnceAsync()).ProcessedCount); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing));
    }
    [Theory]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "ignore")]
    [InlineData(true, "ignore")]
    public async Task ManualDecisionHoldingPayment_MonitorCannotOverwriteIt(bool legal, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var reviewing = Open(database.ConnectionString, barrier); await using var monitoring = Open(database.ConnectionString);
        var pending = PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, action); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(error).SqlState);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20))); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring);
        Assert.Equal(0, (await AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync()).MarkedCount); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring));
        Assert.Equal(action == "accept" ? 100m : 0m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(monitoring, fixture));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyLaterRow_RejectsWholeBatchBeforeAnyNotification(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int laterId;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal); laterId = (await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture)).Id;
        }
        await using var blocker = Open(database.ConnectionString); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {laterId} FOR UPDATE");
        await using var monitoring = Open(database.ConnectionString); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring); var auditCount = await monitoring.ObjectAuditEvents.CountAsync(); var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => AdvanceReviewMonitoringBoundaryTests.Runner(monitoring, notifications).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(error).SqlState); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring)); Assert.Equal(auditCount, await monitoring.ObjectAuditEvents.CountAsync()); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls); Assert.Null(monitoring.Database.CurrentTransaction);
        await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailure_RollsBackWholeNativeBatch(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using (var db = Open(database.ConnectionString)) { var fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); }
        await using var monitoring = Open(database.ConnectionString, new FailAfterSave()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring); var auditCount = await monitoring.ObjectAuditEvents.CountAsync();
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(monitoring, notifications).RunOnceAsync());
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring)); Assert.Equal(auditCount, await monitoring.ObjectAuditEvents.CountAsync()); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls); Assert.False(monitoring.ChangeTracker.HasChanges());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_AnotherConnectionSeesCommittedDecisionAndAudit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var monitoring = Open(database.ConnectionString); var fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(monitoring, legal, true);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications;
        probe.Before = async _ =>
        {
            Assert.Null(monitoring.Database.CurrentTransaction); await using var observer = Open(database.ConnectionString);
            Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await observer.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus);
            Assert.Contains(await observer.ObjectAuditEvents.Where(a => a.ObjectType == "payment" && a.ObjectId == fixture.PaymentId && a.Action == "Updated").ToArrayAsync(), a => a.MetadataJson?.Contains("AdvanceReviewStatus", StringComparison.Ordinal) == true);
        };
        var before = await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(monitoring); var result = await AdvanceReviewMonitoringBoundaryTests.Runner(monitoring, notifications).RunOnceAsync();
        Assert.Equal(1, result.MarkedCount); Assert.Equal(1, result.NotifiedCount); Assert.Equal(1, result.EscalatedCount); Assert.Equal(2, probe.Calls.Count); Assert.Equal(before, await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(monitoring));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailure_RetryOnlySendsTheMissingNotification(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var monitoring = Open(database.ConnectionString); await AdvanceReviewMonitoringBoundaryTests.SeedAsync(monitoring, legal);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var probe = (PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications; var runner = AdvanceReviewMonitoringBoundaryTests.Runner(monitoring, notifications);
        probe.Before = _ => throw new IOException("Synthetic native delivery failure"); await Assert.ThrowsAsync<IOException>(() => runner.RunOnceAsync()); var auditCount = await monitoring.ObjectAuditEvents.CountAsync();
        probe.Before = null; var result = await runner.RunOnceAsync(); Assert.Equal(0, result.MarkedCount); Assert.Equal(1, result.NotifiedCount); Assert.Equal(auditCount, await monitoring.ObjectAuditEvents.CountAsync()); Assert.Equal(2, probe.Calls.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentMonitors_CannotBothChangeTheSameBatch(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var db = Open(database.ConnectionString)) await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var first = Open(database.ConnectionString, barrier); await using var second = Open(database.ConnectionString);
        var pending = AdvanceReviewMonitoringBoundaryTests.Runner(first).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => AdvanceReviewMonitoringBoundaryTests.Runner(second).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(error).SqlState);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).MarkedCount); Assert.Equal(0, (await AdvanceReviewMonitoringBoundaryTests.Runner(second).RunOnceAsync()).MarkedCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractiveWriter_StillLocksActorPermissions(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        await using var changingRole = Open(database.ConnectionString); await using var transaction = await changingRole.Database.BeginTransactionAsync();
        await changingRole.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {fixture.ActorId}) FOR UPDATE");
        await using var reviewing = Open(database.ConnectionString); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing);
        Assert.IsType<ConflictObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, fixture, "accept").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing)); Assert.Null(reviewing.Database.CurrentTransaction); await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgement_RetryDoesNotRewriteDecision(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var db = Open(database.ConnectionString)) await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        var fault = new LoseCommitAcknowledgement(); await using var monitoring = Open(database.ConnectionString, fault);
        var notifications = PaymentCreateAttemptTests.CheckedNotificationProxy.Create(); var runner = AdvanceReviewMonitoringBoundaryTests.Runner(monitoring, notifications);
        await Assert.ThrowsAsync<IOException>(() => runner.RunOnceAsync()); Assert.Empty(((PaymentCreateAttemptTests.CheckedNotificationProxy)(object)notifications).Calls);
        Assert.Equal(1, await monitoring.Payments.CountAsync(p => p.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)); var auditCount = await monitoring.ObjectAuditEvents.CountAsync();
        fault.Armed = false; var retry = await runner.RunOnceAsync(); Assert.Equal(0, retry.MarkedCount); Assert.Equal(1, retry.NotifiedCount); Assert.Equal(auditCount, await monitoring.ObjectAuditEvents.CountAsync());
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new IOException("Synthetic monitor failure after native SQL write");
    }
    private sealed class LoseCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; } = true;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
            => Armed ? throw new IOException("Synthetic lost monitor commit acknowledgement") : Task.CompletedTask;
    }
}

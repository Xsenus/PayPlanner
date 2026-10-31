using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentOverdueDeliveryPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailedDelivery_RecoversWithoutASecondFinancialWrite(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueDeliveryTests.SeedAsync(db, legal, false);
        var realtime = new PaymentOverdueDeliveryTests.ProbeRealtime { Failures = 1 }; await using var provider = PaymentOverdueDeliveryTests.Provider(() => Open(database.ConnectionString), realtime);
        using var updater = new PaymentStatusUpdater(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PaymentStatusUpdater>.Instance);
        await Assert.ThrowsAsync<IOException>(() => updater.RunOnceAsync()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var notification = await db.UserNotifications.AsNoTracking().SingleAsync();
        await PaymentOverdueDeliveryTests.ExpireAsync(db); Assert.Equal(0, await updater.RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
        Assert.Equal(notification.Id, (await db.UserNotifications.SingleAsync()).Id); Assert.NotNull((await db.PaymentOverdueNotificationOutboxes.SingleAsync()).CompletedAt); Assert.Equal(1, realtime.Successes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentClaims_CannotCreateTwoInboxRows_AndAnExpiredOwnerCannotSettle(bool steal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var realtime = new PaymentOverdueDeliveryTests.ProbeRealtime { Before = async _ => { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); } } };
        await using var provider = PaymentOverdueDeliveryTests.Provider(() => Open(database.ConnectionString), realtime); var item = await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync();
        var pending = PaymentOverdueDeliveryTests.Processor(provider).ProcessAsync(item.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            if (steal) await PaymentOverdueDeliveryTests.ExpireAsync(db);
            Assert.Equal(steal, await PaymentOverdueDeliveryTests.Processor(provider).ProcessAsync(item.Id));
            Assert.Single(await db.UserNotifications.ToArrayAsync());
        }
        finally { release.TrySetResult(); }
        Assert.Equal(!steal, await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var completed = await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync(); Assert.NotNull(completed.CompletedAt); Assert.Equal(steal ? 2 : 1, completed.Attempts); Assert.Null(completed.LeaseToken);
    }

    [Fact]
    public async Task InboxPersistence_HoldsTheSamePaymentAndRecipientPermissionLocks()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var barrier = new HoldInboxSave();
        await using var provider = PaymentOverdueDeliveryTests.Provider(() => Open(database.ConnectionString, barrier), new PaymentOverdueDeliveryTests.ProbeRealtime());
        var pending = PaymentOverdueDeliveryTests.Processor(provider).ProcessBatchAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await using var contender = Open(database.ConnectionString);
            await using var transaction = await contender.Database.BeginTransactionAsync();
            var user = await db.Users.AsNoTracking().SingleAsync();
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => contender.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {user.RoleId} FOR UPDATE NOWAIT"));
            Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState); await transaction.RollbackAsync();
            await using var paymentTransaction = await contender.Database.BeginTransactionAsync();
            var paymentId = await db.Payments.Select(p => p.Id).SingleAsync();
            error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => contender.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {paymentId} FOR UPDATE NOWAIT")); Assert.Equal(Npgsql.PostgresErrorCodes.LockNotAvailable, error.SqlState);
            await paymentTransaction.RollbackAsync();
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(20))); Assert.Single(await db.UserNotifications.ToArrayAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldInboxSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<UserNotification>().Any(entry => entry.State == EntityState.Added)) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            return result;
        }
    }
}

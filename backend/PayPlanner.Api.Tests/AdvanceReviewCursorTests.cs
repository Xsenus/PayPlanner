using System.IO;
using System.Reflection;
using System.Text.Json;
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

public sealed class AdvanceReviewCursorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPendingPayment_DoesNotStarveLaterCandidate(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1);
        var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision; await db.SaveChangesAsync();
        await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync();
        Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync()).MarkedCount);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == later.Id)).AdvanceReviewStatus);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartedRunner_VisitsEveryPageAndWraps(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); var second = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var third = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        for (var i = 0; i < 4; i++) Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync()).ProcessedCount);
        Assert.Equal(new int?[] { fixture.PaymentId, second.Id, third.Id, fixture.PaymentId }, probe.Events.Select(e => e.ObjectId));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuousArrivals_DoNotPreventTheNextCycle(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); var second = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var third = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        for (var i = 0; i < 4; i++) { await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync(); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); }
        Assert.Equal(new int?[] { fixture.PaymentId, second.Id, third.Id, fixture.PaymentId }, probe.Events.Take(4).Select(e => e.ObjectId));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneRecipientFailure_DoesNotBlockOtherPayments(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 10); var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        probe.Send = envelope => envelope.ObjectId == fixture.PaymentId ? throw new IOException("Synthetic first recipient failure") : Task.FromResult(true);
        await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync());
        Assert.Contains(probe.Events, e => e.ObjectId == later.Id);
    }
    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"afterId\":1,\"throughId\":2}")]
    [InlineData("{\"version\":1,\"afterId\":-1,\"throughId\":2}")]
    [InlineData("{\"version\":1,\"afterId\":9,\"throughId\":2}")]
    [InlineData("oversized")]
    public async Task InvalidCursor_RestartsAndPersistsAValidPosition(string value)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, false, 1);
        db.SystemSettings.Add(new() { Key = PaymentAdvanceReviewMonitoringService.CursorSettingKey, Value = value == "oversized" ? new string('x', 300) : value }); await db.SaveChangesAsync();
        Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync()).MarkedCount);
        Assert.Equal(fixture.PaymentId, await CursorAfterAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledSave_PreservesBothCursorAndDecision(bool existing, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); var fault = new SaveFault(cancellation, cancel); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        var fixture = await SeedPageAsync(db, false, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var runner = AdvanceReviewMonitoringBoundaryTests.Runner(db);
        if (existing) await runner.RunOnceAsync(); var cursor = await CursorValueAsync(db); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); fault.Armed = true;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunOnceAsync(cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.Equal(cursor, await CursorValueAsync(db)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        fault.Armed = false; Assert.Equal(1, (await runner.RunOnceAsync()).MarkedCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrap_KeepsPersistedNotificationDeduplication(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        probe.Send = async envelope =>
        {
            db.UserNotifications.Add(new() { RecipientUserId = envelope.RecipientUserId, EventKey = envelope.EventKey, Type = envelope.Type, ObjectType = envelope.ObjectType, ObjectId = envelope.ObjectId }); await db.SaveChangesAsync(); return true;
        };
        for (var i = 0; i < 4; i++) await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync();
        Assert.Equal(2, await db.UserNotifications.CountAsync()); Assert.Equal(2, probe.Events.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorWrap_PreservesPersonalNotificationBan(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        db.UserSettings.Add(new() { UserId = fixture.ActorId, Key = UserNotificationPreferenceSettings.SettingKey, Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([UserNotificationTypes.PaymentAdvanceReviewRequired], new Dictionary<string, IReadOnlyList<string>>())) }); await db.SaveChangesAsync();
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        for (var i = 0; i < 4; i++) await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync(); Assert.Empty(probe.Events);
        Assert.Equal(2, await db.Payments.CountAsync(p => p.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRequiredNotification_DoesNotBlockAdminEscalation(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); (await db.Roles.SingleAsync()).Name = "admin"; await db.SaveChangesAsync();
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        probe.Send = e => e.Type == UserNotificationTypes.PaymentAdvanceReviewRequired ? throw new IOException("Synthetic responsible recipient failure") : Task.FromResult(true);
        await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync());
        Assert.Contains(probe.Events, e => e.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation); Assert.Equal(fixture.PaymentId, await CursorAfterAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFirstAdministrator_DoesNotBlockTheOtherAdministrator(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedPageAsync(db, legal, 1); var role = await db.Roles.SingleAsync(); role.Name = "admin";
        db.Users.Add(new() { Email = "second-admin@test.local", FullName = "Second synthetic administrator", PasswordHash = "unused", RoleId = role.Id, IsActive = true, IsApproved = true }); await db.SaveChangesAsync();
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; int? failedRecipient = null;
        probe.Send = envelope =>
        {
            if (envelope.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation && failedRecipient is null) { failedRecipient = envelope.RecipientUserId; throw new IOException("Synthetic first admin delivery failure"); }
            return Task.FromResult(true);
        };
        await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync());
        Assert.Equal(2, probe.Events.Where(e => e.Type == UserNotificationTypes.PaymentAdvanceReviewEscalation).Select(e => e.RecipientUserId).Distinct().Count());
    }
    [Fact]
    public async Task LargeFailureBatch_AttemptsAllRecipientsWithBoundedErrorSamples()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, false, 500);
        for (var i = 1; i < 40; i++) await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; probe.Send = _ => throw new IOException("Synthetic batch delivery failure");
        var error = await Assert.ThrowsAsync<AggregateException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync());
        Assert.Equal(40, probe.Events.Count); Assert.Equal(32, error.InnerExceptions.Count); Assert.Contains("40", error.Message); Assert.Equal(40, await db.Payments.CountAsync(p => p.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleFailures_AreReportedAfterOtherRecipientsAreAttempted(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 10); var second = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var third = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; probe.Send = e => e.ObjectId == third.Id ? Task.FromResult(true) : throw new IOException("Synthetic recipient failure");
        var error = await Assert.ThrowsAsync<AggregateException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync()); Assert.Equal(2, error.InnerExceptions.Count); Assert.Contains(probe.Events, e => e.ObjectId == third.Id); Assert.Equal(third.Id, await CursorAfterAsync(db));
    }
    [Fact]
    public async Task DeliveryCancellationWithoutCallerCancellation_IsReportedAsFailure()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, false, 10); var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; probe.Send = e => e.ObjectId == fixture.PaymentId ? Task.FromException<bool>(new OperationCanceledException("Synthetic transport timeout")) : Task.FromResult(true);
        var error = await Assert.ThrowsAsync<AggregateException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync()); Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(error.InnerExceptions)); Assert.Contains(probe.Events, e => e.ObjectId == later.Id);
    }
    [Fact]
    public async Task CallerCancellation_StopsDispatchWithCommittedCursor()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, false, 10); var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); using var cancellation = new CancellationTokenSource();
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; probe.Send = _ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync(cancellation.Token)); Assert.Single(probe.Events); Assert.Equal(later.Id, await CursorAfterAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }
    [Fact]
    public async Task FailedNotificationContext_CannotSaveItsDirtyPaymentThroughAnotherRecipient()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, false, 10); var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var delivered = new List<int?>();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options; var services = new ServiceCollection(); services.AddScoped(_ => new PaymentContext(options));
        services.AddScoped<INotificationService>(provider =>
        {
            var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
            probe.Send = async envelope =>
            {
                var isolated = provider.GetRequiredService<PaymentContext>();
                if (envelope.ObjectId == fixture.PaymentId) { (await isolated.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).Amount = 999m; throw new IOException("Synthetic dirty notification context"); }
                Assert.False(isolated.ChangeTracker.HasChanges()); await isolated.SaveChangesAsync(); delivered.Add(envelope.ObjectId); return true;
            }; return notifications;
        });
        await using var provider = services.BuildServiceProvider(); var runner = new PaymentAdvanceReviewMonitoringService(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<PaymentAdvanceReviewMonitoringService>.Instance);
        await Assert.ThrowsAsync<IOException>(() => runner.RunOnceAsync()); Assert.Contains(later.Id, delivered); Assert.Equal(100m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).Amount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateChanges_DoNotMoveAnUnvisitedIdBehindTheCursor(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); var later = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
        await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync(); (await db.Payments.SingleAsync(p => p.Id == later.Id)).Date = DateTime.UtcNow.Date.AddYears(-20); await db.SaveChangesAsync();
        Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync()).MarkedCount); Assert.Equal(later.Id, await CursorAfterAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedTail_StartsAnotherCycleWithoutAnEmptyRun(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedPageAsync(db, legal, 1); var tail = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync();
        (await db.Payments.SingleAsync(p => p.Id == tail.Id)).Status = PaymentStatus.Cancelled; await db.SaveChangesAsync(); var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync()).ProcessedCount); Assert.Equal(fixture.PaymentId, Assert.Single(probe.Events).ObjectId);
    }
    [Fact]
    public async Task EmptyMonitor_DoesNotCreateProgressRows()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); Assert.Equal(0, (await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync()).ProcessedCount); Assert.Null(await CursorValueAsync(db));
    }
    internal static Task<string?> CursorValueAsync(PaymentContext db)
        => db.SystemSettings.AsNoTracking().Where(s => s.Key == PaymentAdvanceReviewMonitoringService.CursorSettingKey).Select(s => s.Value).SingleOrDefaultAsync();
    internal static async Task<int> CursorAfterAsync(PaymentContext db)
    {
        using var json = JsonDocument.Parse((await CursorValueAsync(db))!);
        return json.RootElement.GetProperty("afterId").GetInt32();
    }
    private sealed class SaveFault(CancellationTokenSource cancellation, bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed && cancel) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => Armed && !cancel ? throw new IOException("Synthetic cursor save failure") : ValueTask.FromResult(result);
    }
    internal static async Task<Fixture> SeedPageAsync(PaymentContext db, bool legal, int limit)
    {
        var fixture = await AdvanceReviewMonitoringBoundaryTests.SeedAsync(db, legal);
        db.SystemSettings.Add(new() { Key = PaymentAdvanceReviewMonitoringService.BatchSizeSettingKey, Value = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) }); await db.SaveChangesAsync(); return fixture;
    }
    public class RecordingNotifications : DispatchProxy
    {
        public List<NotificationEventEnvelope> Events { get; } = [];
        public Func<NotificationEventEnvelope, Task<bool>>? Send { get; set; }
        public static INotificationService Create() => DispatchProxy.Create<INotificationService, RecordingNotifications>();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(INotificationService.NotifyAsync) || args?[0] is not NotificationEventEnvelope envelope) throw new InvalidOperationException("Unexpected notification method");
            Events.Add(envelope); return Send?.Invoke(envelope) ?? Task.FromResult(true);
        }
    }
}

public sealed class AdvanceReviewCursorPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentInstances_InitializeAndAdvanceOneCursor(bool existing)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, false, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
            if (existing) await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync();
        }
        var barrier = new HoldSave(); await using var first = Open(database.ConnectionString, barrier); await using var second = Open(database.ConnectionString);
        var pending = AdvanceReviewMonitoringBoundaryTests.Runner(first).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(second).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).MarkedCount); var afterFirst = await AdvanceReviewCursorTests.CursorAfterAsync(second);
        Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(second).RunOnceAsync()).MarkedCount); Assert.True(await AdvanceReviewCursorTests.CursorAfterAsync(second) > afterFirst);
        Assert.Equal(1, await second.SystemSettings.CountAsync(s => s.Key == PaymentAdvanceReviewMonitoringService.CursorSettingKey));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlFailure_RollsBackCursorAndReviewTogether(bool existing)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using (var db = Open(database.ConnectionString))
        {
            var fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, false, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture);
            if (existing) await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync();
        }
        await using var failing = Open(database.ConnectionString, new FailAfterSave()); var cursor = await AdvanceReviewCursorTests.CursorValueAsync(failing); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(failing);
        await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(failing).RunOnceAsync()); Assert.Equal(cursor, await AdvanceReviewCursorTests.CursorValueAsync(failing)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(failing)); Assert.Null(failing.Database.CurrentTransaction);
        await using var retry = Open(database.ConnectionString); Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(retry).RunOnceAsync()).MarkedCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedCursor_IsNotBypassedOrPartiallyAdvanced(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using (var db = Open(database.ConnectionString)) { var fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, legal, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync(); }
        await using var blocker = Open(database.ConnectionString); await using var transaction = await blocker.Database.BeginTransactionAsync();
        var key = PaymentAdvanceReviewMonitoringService.CursorSettingKey; await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SystemSettings\" WHERE \"Key\" = {key} FOR UPDATE");
        await using var monitoring = Open(database.ConnectionString); var cursor = await AdvanceReviewCursorTests.CursorValueAsync(monitoring); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring);
        var error = await Assert.ThrowsAsync<PostgresException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); Assert.Equal(cursor, await AdvanceReviewCursorTests.CursorValueAsync(monitoring)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(monitoring)); await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowDispatch_DoesNotHoldCursorOrOverwriteTheNextPage(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int secondId;
        await using (var db = Open(database.ConnectionString)) { fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, legal, 1); secondId = (await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture)).Id; }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = AdvanceReviewCursorTests.RecordingNotifications.Create(); var probe = (AdvanceReviewCursorTests.RecordingNotifications)(object)notifications; probe.Send = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); return true; };
        await using var first = Open(database.ConnectionString); await using var second = Open(database.ConnectionString); var pending = AdvanceReviewMonitoringBoundaryTests.Runner(first, notifications).RunOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(second).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))).MarkedCount); Assert.Equal(secondId, await AdvanceReviewCursorTests.CursorAfterAsync(second)); }
        finally { release.TrySetResult(); }
        Assert.Equal(1, (await pending.WaitAsync(TimeSpan.FromSeconds(20))).NotifiedCount); Assert.Equal(secondId, await AdvanceReviewCursorTests.CursorAfterAsync(second));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommitAcknowledgement_DoesNotLoseTheCommittedCursor(bool existing)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) { fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, false, 1); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); if (existing) await AdvanceReviewMonitoringBoundaryTests.Runner(db).RunOnceAsync(); }
        var fault = new LostAcknowledgement(); await using var monitoring = Open(database.ConnectionString, fault); await Assert.ThrowsAsync<IOException>(() => AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync()); var cursor = await AdvanceReviewCursorTests.CursorAfterAsync(monitoring);
        fault.Armed = false; Assert.Equal(1, (await AdvanceReviewMonitoringBoundaryTests.Runner(monitoring).RunOnceAsync()).MarkedCount); Assert.True(await AdvanceReviewCursorTests.CursorAfterAsync(monitoring) > cursor);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRestart_WrapsAndPreservesAllFinancialFields(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var fixture = await AdvanceReviewCursorTests.SeedPageAsync(db, legal, 1); var second = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var third = await AdvanceReviewBatchBoundaryTests.AddLegacyAsync(db, fixture); var before = await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(db);
        var notifications = AdvanceReviewCursorTests.RecordingNotifications.Create(); var probe = (AdvanceReviewCursorTests.RecordingNotifications)(object)notifications;
        for (var i = 0; i < 5; i++) await AdvanceReviewMonitoringBoundaryTests.Runner(db, notifications).RunOnceAsync();
        Assert.Equal(new int?[] { fixture.PaymentId, second.Id, third.Id, fixture.PaymentId, second.Id }, probe.Events.Select(e => e.ObjectId)); Assert.Equal(before, await AdvanceReviewMonitoringBoundaryTests.MoneyAsync(db));
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
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => throw new IOException("Synthetic native cursor save failure");
    }
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; } = true;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) => Armed ? throw new IOException("Synthetic lost cursor commit acknowledgement") : Task.CompletedTask;
    }
}

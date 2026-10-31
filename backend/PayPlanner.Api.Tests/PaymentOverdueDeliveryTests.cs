using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentOverdueDeliveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryAfterPersistedNotification_DoesNotRewriteMoneyOrDuplicateTheInbox(bool legal, bool invoice)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await SeedAsync(db, legal, invoice);
        var realtime = new ProbeRealtime { Failures = 1 };
        await using var provider = Provider(() => new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options), realtime);
        using var updater = new PaymentStatusUpdater(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PaymentStatusUpdater>.Instance);
        await Assert.ThrowsAsync<IOException>(() => updater.RunOnceAsync());
        var row = await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Null(row.CompletedAt); Assert.Equal(1, row.Attempts); Assert.NotNull(row.LeaseToken);
        var first = await db.UserNotifications.AsNoTracking().SingleAsync(); Assert.StartsWith(row.EventKey + ":recipient:", first.EventKey);
        Assert.Equal(invoice ? UserNotificationTypes.InvoiceChanged : UserNotificationTypes.PaymentChanged, first.Type);
        Assert.Equal(invoice ? "invoice" : "payment", first.ObjectType); Assert.Contains("Просрочен", first.BodyPreview);
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await ExpireAsync(db);
        Assert.Equal(0, await updater.RunOnceAsync());
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
        Assert.Equal(first.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.NotNull((await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.Equal(1, realtime.Successes); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("pending")]
    [InlineData("cancelled")]
    [InlineData("date")]
    [InlineData("kind")]
    [InlineData("owner")]
    [InlineData("deleted")]
    [InlineData("zero")]
    [InlineData("compensated")]
    public async Task StaleOrProtectedEvent_IsSettledWithoutSending(string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var payment = await db.Payments.SingleAsync();
        if (reason == "paid") payment.IsPaid = true;
        if (reason == "pending") payment.Status = PaymentStatus.Pending;
        if (reason == "cancelled") payment.Status = PaymentStatus.Cancelled;
        if (reason == "date") payment.Date = payment.Date.AddDays(1);
        if (reason == "kind") payment.DocumentKind = DocumentKind.Invoice;
        if (reason == "owner") { var owner = new Client { Name = "Synthetic replacement" }; db.Clients.Add(owner); await db.SaveChangesAsync(); payment.ClientId = owner.Id; }
        if (reason == "deleted") db.Entry(payment).Property<bool>("IsDeleted").CurrentValue = true;
        if (reason == "zero") payment.Amount = 0m;
        if (reason == "compensated") db.ClientBalanceTransactions.Add(new() { ClientId = payment.ClientId, PaymentId = payment.Id, Amount = 25m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var realtime = new ProbeRealtime(); await using var provider = SqliteProvider(db, realtime);
        Assert.Equal(1, await Processor(provider).ProcessBatchAsync()); Assert.Empty(await db.UserNotifications.ToArrayAsync()); Assert.Equal(0, realtime.Successes);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("unassigned")]
    [InlineData("type")]
    [InlineData("inApp")]
    public async Task CurrentRightsAndPersonalRestrictions_PreventDelivery(string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var user = await db.Users.SingleAsync();
        if (reason == "permission") (await db.RolePermissions.SingleAsync(row => row.Section == "payments")).CanView = false;
        if (reason == "inactive") user.IsActive = false;
        if (reason == "unapproved") user.IsApproved = false;
        if (reason == "unassigned") db.RemoveRange(await db.Set<PaymentResponsible>().ToArrayAsync());
        if (reason is "type" or "inApp")
            db.UserSettings.Add(new()
            {
                UserId = user.Id,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = reason == "type" ? JsonSerializer.Serialize(new { disabledTypes = new[] { UserNotificationTypes.PaymentChanged } })
                    : JsonSerializer.Serialize(new { disabledChannels = new Dictionary<string, string[]> { [UserNotificationTypes.PaymentChanged] = [NotificationChannels.InApp] } })
            });
        await db.SaveChangesAsync(); var realtime = new ProbeRealtime(); await using var provider = SqliteProvider(db, realtime);
        Assert.Equal(1, await Processor(provider).ProcessBatchAsync()); Assert.Empty(await db.UserNotifications.ToArrayAsync()); Assert.Equal(0, realtime.Successes);
    }

    [Fact]
    public async Task RetryUsesCurrentResponsible_AndCannotReviveTheFormerRecipientsRow()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var realtime = new ProbeRealtime { Failures = 1 }; await using var provider = SqliteProvider(db, realtime); var processor = Processor(provider);
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessBatchAsync()); var first = await db.UserNotifications.AsNoTracking().SingleAsync();
        var user = new User { Email = "new-overdue@test.local", FullName = "Synthetic new responsible", PasswordHash = "unused", RoleId = (await db.Users.SingleAsync()).RoleId, IsActive = true, IsApproved = true };
        db.Users.Add(user); await db.SaveChangesAsync(); db.RemoveRange(await db.Set<PaymentResponsible>().ToArrayAsync()); db.Add(new PaymentResponsible { PaymentId = first.ObjectId!.Value, UserId = user.Id }); await db.SaveChangesAsync(); await ExpireAsync(db);
        Assert.Equal(1, await processor.ProcessBatchAsync()); Assert.Equal(2, await db.UserNotifications.CountAsync()); Assert.Equal(user.Id, Assert.Single(realtime.Recipients));
    }

    [Fact]
    public async Task PermissionRevokedAfterInboxSave_PreventsTheRetryFromSending()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var realtime = new ProbeRealtime { Failures = 1 }; await using var provider = SqliteProvider(db, realtime); var processor = Processor(provider);
        await Assert.ThrowsAsync<IOException>(() => processor.ProcessBatchAsync());
        (await db.RolePermissions.SingleAsync(row => row.Section == "payments")).CanView = false; await db.SaveChangesAsync(); await ExpireAsync(db);
        Assert.Equal(1, await processor.ProcessBatchAsync()); Assert.Single(await db.UserNotifications.ToArrayAsync()); Assert.Equal(0, realtime.Successes);
    }

    [Fact]
    public async Task ReenteredOverdue_CreatesANewRecipientKey()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false, false); var realtime = new ProbeRealtime(); await using var provider = SqliteProvider(db, realtime);
        using var updater = new PaymentStatusUpdater(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PaymentStatusUpdater>.Instance);
        Assert.Equal(1, await updater.RunOnceAsync()); db.ChangeTracker.Clear(); var p = await db.Payments.SingleAsync(); p.Status = PaymentStatus.Pending; await db.SaveChangesAsync();
        Assert.Equal(1, await updater.RunOnceAsync()); Assert.Equal(2, await db.UserNotifications.CountAsync()); Assert.Equal(2, (await db.UserNotifications.Select(n => n.EventKey).ToArrayAsync()).Distinct().Count()); Assert.Equal(2, realtime.Successes);
    }

    [Fact]
    public async Task FailedRecovery_DoesNotBlockANewOverdueTransition()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db);
        var p = await db.Payments.SingleAsync(); var next = new Payment { ClientId = p.ClientId, Amount = 10m, Date = p.Date, Status = PaymentStatus.Pending, Type = p.Type, DocumentKind = p.DocumentKind, Responsibles = [new() { UserId = (await db.Users.SingleAsync()).Id }] };
        db.Payments.Add(next); await db.SaveChangesAsync(); var realtime = new ProbeRealtime { Failures = 1 }; await using var provider = SqliteProvider(db, realtime);
        using var updater = new PaymentStatusUpdater(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PaymentStatusUpdater>.Instance);
        await Assert.ThrowsAsync<IOException>(() => updater.RunOnceAsync()); Assert.Equal(2, await db.Payments.CountAsync(row => row.Status == PaymentStatus.Overdue));
        Assert.Equal(2, await db.UserNotifications.CountAsync()); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync(row => row.CompletedAt != null)); Assert.Equal(1, realtime.Successes);
    }

    [Fact]
    public async Task CleanupIsBounded_AndOnlyRemovesOldCompletedEvents()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var first = await db.PaymentOverdueNotificationOutboxes.SingleAsync();
        first.CreatedAt = DateTime.UtcNow.AddDays(-90); first.LeaseUntil = DateTime.UtcNow.AddDays(1); var old = DateTime.UtcNow.AddDays(-31);
        db.PaymentOverdueNotificationOutboxes.AddRange(Enumerable.Range(0, 205).Select(i => new PaymentOverdueNotificationOutbox { PaymentId = first.PaymentId, EventKey = $"synthetic-completed:{i}", TransitionToken = Guid.NewGuid().ToString("N"), CreatedAt = old, DueDate = first.DueDate, CompletedAt = old }));
        db.PaymentOverdueNotificationOutboxes.Add(new() { PaymentId = first.PaymentId, EventKey = "synthetic-recent", TransitionToken = Guid.NewGuid().ToString("N"), CreatedAt = old, DueDate = first.DueDate, CompletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); await using var provider = SqliteProvider(db, new ProbeRealtime());
        Assert.Equal(0, await Processor(provider).ProcessBatchAsync()); Assert.Equal(7, await db.PaymentOverdueNotificationOutboxes.CountAsync()); Assert.True(await db.PaymentOverdueNotificationOutboxes.AnyAsync(row => row.Id == first.Id)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }

    [Fact]
    public async Task BatchIsFinite_AndUnattemptedEventsPrecedeFailedAttempts()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var first = await db.PaymentOverdueNotificationOutboxes.SingleAsync(); first.LastAttemptAt = DateTime.UtcNow; first.LeaseUntil = DateTime.UtcNow.AddSeconds(-1);
        db.PaymentOverdueNotificationOutboxes.AddRange(Enumerable.Range(0, 51).Select(i => new PaymentOverdueNotificationOutbox { PaymentId = first.PaymentId, EventKey = $"synthetic-stale:{i}", TransitionToken = Guid.NewGuid().ToString("N"), CreatedAt = first.CreatedAt, DueDate = first.DueDate })); await db.SaveChangesAsync();
        var realtime = new ProbeRealtime(); await using var provider = SqliteProvider(db, realtime);
        Assert.Equal(50, await Processor(provider).ProcessBatchAsync()); Assert.Null((await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == first.Id)).CompletedAt); Assert.Equal(0, realtime.Successes); Assert.Equal(2, await db.PaymentOverdueNotificationOutboxes.CountAsync(row => row.CompletedAt == null));
    }

    [Fact]
    public async Task WrongClaim_CannotPersistOrDispatch()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var item = await db.PaymentOverdueNotificationOutboxes.AsNoTracking().SingleAsync(); var realtime = new ProbeRealtime(); await using var provider = SqliteProvider(db, realtime);
        using var scope = provider.CreateScope(); var service = (IDurablePaymentOverdueNotificationService)scope.ServiceProvider.GetRequiredService<INotificationService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.NotifyOverdueForEventAsync(item, "wrong", (db.Users.Single()).Id)); Assert.Empty(await db.UserNotifications.ToArrayAsync()); Assert.Equal(0, realtime.Successes);
    }

    internal static async Task SeedAsync(PaymentContext db, bool legal, bool invoice)
    {
        await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        if (invoice) { (await db.Payments.SingleAsync()).DocumentKind = DocumentKind.Invoice; await db.SaveChangesAsync(); }
    }
    internal static Task<int> ExpireAsync(PaymentContext db)
    {
        var expiredAt = DateTime.UtcNow.AddMinutes(-1);
        return db.PaymentOverdueNotificationOutboxes.Where(row => row.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseUntil, (DateTime?)expiredAt));
    }
    internal static ServiceProvider SqliteProvider(PaymentContext db, ProbeRealtime realtime)
        => Provider(() => new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options), realtime);
    internal static ServiceProvider Provider(Func<PaymentContext> open, ProbeRealtime realtime)
    {
        var services = new ServiceCollection(); services.AddScoped(_ => open());
        services.AddScoped<INotificationService>(s => new NotificationService(s.GetRequiredService<PaymentContext>(), new ConfigurationBuilder().Build(),
            RejectExternal.Create<IEmailService>(), RejectExternal.Create<ITelegramNotificationService>(), NullLogger<NotificationService>.Instance, realtime));
        return services.BuildServiceProvider();
    }
    internal static PaymentOverdueNotificationDispatchProcessor Processor(ServiceProvider provider)
        => new(provider.GetRequiredService<IServiceScopeFactory>());
    internal sealed class ProbeRealtime : IRealtimeNotificationPublisher, IReliableRealtimeNotificationPublisher
    {
        public int Failures { get; set; }
        public int Successes { get; private set; }
        public List<int> Recipients { get; } = [];
        public Func<UserNotification, Task>? Before { get; set; }
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No task change expected");
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Reliable publisher is required");
        public async Task PublishUserNotificationOrThrowAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            if (Before is not null) await Before(notification);
            if (Failures > 0) { Failures--; throw new IOException("Synthetic realtime failure after inbox save"); }
            Successes++; Recipients.Add(notification.RecipientUserId);
        }
    }
    public class RejectExternal : DispatchProxy
    {
        public static T Create<T>() where T : class => Create<T, RejectExternal>();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("External delivery is not configured for this synthetic test");
    }
}

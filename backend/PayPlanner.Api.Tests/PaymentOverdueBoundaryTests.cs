using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

public sealed class PaymentOverdueBoundaryTests
{
    [Theory]
    [InlineData(false, "root")]
    [InlineData(true, "root")]
    [InlineData(false, "workflow")]
    [InlineData(true, "workflow")]
    [InlineData(false, "credit")]
    [InlineData(true, "credit")]
    [InlineData(false, "refund")]
    [InlineData(true, "refund")]
    public async Task ClosedOrCompensatedHistory_IsNotChangedByOverdueJob(bool legal, string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal); var root = await AddRootAsync(db, fixture, 0m);
        if (reason == "root") root.Status = PaymentStatus.Cancelled;
        if (reason == "workflow") root.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
        if (reason == "credit") db.ClientBalanceTransactions.Add(new() { PaymentId = fixture.PaymentId, InvoiceId = root.InvoiceId, ClientId = legal ? null : fixture.OwnerId, LegalEntityId = legal ? fixture.OwnerId : null, Amount = 25m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice });
        if (reason == "refund") db.MoneyOperations.Add(new() { LinkedInvoiceId = root.InvoiceId, Direction = MoneyOperationDirection.Expense, MatchComment = "Возврат оплаты при аннулировании счёта.", Amount = 25m, Date = DateTime.UtcNow.Date });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.Equal(0, await Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlSaveFailure_RollsBackTheStatusAndAudit(bool legal)
    {
        var fault = new FailAfterSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await SeedAsync(db, legal); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audits = await db.ObjectAuditEvents.CountAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audits, await db.ObjectAuditEvents.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverdueJob_DoesNotRepairHistoricalPaidAmount(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal); await AddRootAsync(db, fixture, 30m); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignTransactionOrPendingChange_IsPreserved(bool external)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false);
        if (external)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(); var error = await Assert.ThrowsAnyAsync<Exception>(() => Runner(db).RunOnceAsync()); Assert.Equal("PayPlanner.Api.Services.Payments.BalanceApplicationConflictException", error.GetType().FullName); Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
        }
        else
        {
            var client = await db.Clients.SingleAsync(); client.Name = "Unrelated pending change"; var error = await Assert.ThrowsAnyAsync<Exception>(() => Runner(db).RunOnceAsync()); Assert.Equal("PayPlanner.Api.Services.Payments.BalanceApplicationConflictException", error.GetType().FullName); Assert.Equal(EntityState.Modified, db.Entry(client).State);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_ObservesCommittedStatusAndUnchangedMoney(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal); var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
        probe.Before = async (payment, _) => { Assert.Null(db.Database.CurrentTransaction); Assert.Equal(PaymentStatus.Overdue, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id)).Status); };
        Assert.Equal(1, await Runner(db, notifications).RunOnceAsync()); Assert.Single(probe.Calls); Assert.Equal(fixture.PaymentId, probe.Calls[0].PaymentId);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount); Assert.Equal(100m, await db.ClientBalanceTransactions.SumAsync(t => t.Amount));
    }
    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Etc/GMT+12")]
    public async Task LocalDay_UsesTheResponsibleProfile(string zone)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var utc = DateTime.UtcNow; var due = utc.Date.AddDays(-1);
        var payment = await db.Payments.SingleAsync(); payment.Date = due; payment.PlannedDate = due;
        db.UserSettings.Add(new() { UserId = fixture.ActorId, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = zone }); await db.SaveChangesAsync();
        var expected = due < TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(zone)).Date;
        Assert.Equal(expected ? 1 : 0, await Runner(db).RunOnceAsync());
    }
    [Fact]
    public async Task MultiplePages_KeepTrackedPaymentsBounded()
    {
        var probe = new SaveSizeProbe(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: probe); var fixture = await SeedAsync(db, false);
        db.Payments.AddRange(Enumerable.Range(1, 400).Select(_ => DuePayment(fixture))); await db.SaveChangesAsync(); probe.Armed = true;
        Assert.Equal(401, await Runner(db).RunOnceAsync()); Assert.Equal(401, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Overdue)); Assert.Equal(new[] { 200, 200, 1 }, probe.Sizes);
    }
    [Fact]
    public async Task RelatedSiblings_DoNotLoadTheirUnneededResponsibleAssignments()
    {
        var probe = new SaveSizeProbe(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: probe); var fixture = await SeedAsync(db, false); var root = await AddRootAsync(db, fixture, 0m); root.Responsibles.Add(new() { UserId = fixture.ActorId });
        db.Payments.AddRange(Enumerable.Range(0, 100).Select(_ => { var payment = DuePayment(fixture); payment.InvoiceId = root.InvoiceId; payment.Amount = 0m; return payment; })); await db.SaveChangesAsync(); probe.Armed = true;
        Assert.Equal(1, await Runner(db).RunOnceAsync()); Assert.Equal(1, Assert.Single(probe.ResponsibleCounts));
    }
    [Fact]
    public async Task FirstNonDuePage_DoesNotStarveALaterDuePayment()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); (await db.Payments.SingleAsync()).Date = DateTime.UtcNow.Date;
        db.UserSettings.Add(new() { UserId = fixture.ActorId, Key = CalendarTimeZoneHelper.UserTimeZoneSettingKey, Value = "UTC" });
        db.Payments.AddRange(Enumerable.Range(1, 199).Select(_ => { var p = DuePayment(fixture); p.Date = DateTime.UtcNow.Date; return p; })); await db.SaveChangesAsync(); var due = DuePayment(fixture); db.Payments.Add(due); await db.SaveChangesAsync();
        Assert.Equal(1, await Runner(db).RunOnceAsync()); Assert.Equal(PaymentStatus.Overdue, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == due.Id)).Status); Assert.Equal(200, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUncoveredBalanceCredit_IsNotConcealedByOverdue(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        db.ClientBalanceTransactions.Add(new() { PaymentId = fixture.PaymentId, ClientId = legal ? null : fixture.OwnerId, LegalEntityId = legal ? fixture.OwnerId : null, Amount = 25m, Direction = ClientBalanceDirection.Debit, Type = ClientBalanceTransactionType.CreditApplied }); await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousInvoiceOwner_IsNotRepairedOrMarked(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal); await AddRootAsync(db, fixture, 0m); var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        if (legal) { var other = new LegalEntity { FullName = "Other synthetic company" }; db.LegalEntities.Add(other); await db.SaveChangesAsync(); source.LegalEntityId = other.Id; }
        else { var other = new Client { Name = "Other synthetic client" }; db.Clients.Add(other); await db.SaveChangesAsync(); source.ClientId = other.Id; }
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); Assert.Equal(0, await Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData("no-view")]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    public async Task UnqualifiedRecipient_IsNotNotified(string restriction)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false);
        if (restriction == "no-view") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanView = false;
        else if (restriction == "inactive") (await db.Users.SingleAsync()).IsActive = false; else (await db.Users.SingleAsync()).IsApproved = false;
        await db.SaveChangesAsync(); var notifications = RecordingNotifications.Create(); Assert.Equal(1, await Runner(db, notifications).RunOnceAsync()); Assert.Empty(((RecordingNotifications)(object)notifications).Calls);
    }
    [Fact]
    public async Task LaterPageFailure_KeepsEarlierPageAndRetryFinishesTheRest()
    {
        var fault = new FailPage(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); var fixture = await SeedAsync(db, false); db.Payments.AddRange(Enumerable.Range(1, 200).Select(_ => DuePayment(fixture))); await db.SaveChangesAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Runner(db).RunOnceAsync()); Assert.Equal(200, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Overdue)); Assert.Equal(1, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Pending));
        fault.Armed = false; Assert.Equal(1, await Runner(db).RunOnceAsync()); Assert.Equal(201, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Overdue));
    }
    [Fact]
    public async Task OversizedRelatedGraph_IsRejectedWithoutTruncation()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var root = await AddRootAsync(db, fixture, 0m);
        db.Payments.AddRange(Enumerable.Range(0, 4999).Select(_ => { var p = DuePayment(fixture); p.InvoiceId = root.InvoiceId; p.Amount = 0m; return p; })); await db.SaveChangesAsync(); var auditCount = await db.ObjectAuditEvents.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(db).RunOnceAsync()); Assert.Equal(0, await db.Payments.CountAsync(p => p.Status == PaymentStatus.Overdue)); Assert.Equal(auditCount, await db.ObjectAuditEvents.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeSave_PreservesStatusAndReleasesRunner(bool legal)
    {
        using var cancellation = new CancellationTokenSource(); var fault = new CancelSave(cancellation); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await SeedAsync(db, legal); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var runner = Runner(db); fault.Armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(cancellation.Token)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); fault.Armed = false; Assert.Equal(1, await runner.RunOnceAsync());
    }
    [Fact]
    public async Task FailedRecipient_DoesNotBlockTheOtherResponsibleOrUndoOverdue()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var user = new User { Email = "second-overdue@test.local", FullName = "Second responsible", PasswordHash = "unused", RoleId = (await db.Users.SingleAsync()).RoleId, IsActive = true, IsApproved = true }; db.Users.Add(user); await db.SaveChangesAsync(); (await db.Payments.SingleAsync()).Responsibles.Add(new() { UserId = user.Id }); await db.SaveChangesAsync();
        var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications; var failed = false; probe.Before = (_, _) => { if (!failed) { failed = true; throw new IOException("Synthetic first responsible failure"); } return Task.CompletedTask; };
        await Assert.ThrowsAsync<IOException>(() => Runner(db, notifications).RunOnceAsync()); Assert.Equal(2, probe.Calls.Select(c => c.RecipientId).Distinct().Count()); Assert.Equal(PaymentStatus.Overdue, (await db.Payments.AsNoTracking().SingleAsync()).Status);
    }
    [Fact]
    public async Task DirtyNotificationContext_CannotChangeThePaymentThroughAnotherRecipient()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var user = new User { Email = "isolated-overdue@test.local", FullName = "Isolated responsible", PasswordHash = "unused", RoleId = (await db.Users.SingleAsync()).RoleId, IsActive = true, IsApproved = true }; db.Users.Add(user); await db.SaveChangesAsync(); (await db.Payments.SingleAsync()).Responsibles.Add(new() { UserId = user.Id }); await db.SaveChangesAsync();
        var delivered = new List<int>(); var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(db.Database.GetDbConnection()).Options; var services = new ServiceCollection(); services.AddScoped(_ => new PaymentContext(options));
        services.AddScoped<INotificationService>(provider =>
        {
            var notifications = RecordingNotifications.Create(); var probe = (RecordingNotifications)(object)notifications;
            probe.Before = async (_, recipient) =>
            {
                var isolated = provider.GetRequiredService<PaymentContext>();
                if (recipient == fixture.ActorId) { (await isolated.Payments.SingleAsync()).Amount = 999m; throw new IOException("Synthetic dirty overdue notification context"); }
                Assert.False(isolated.ChangeTracker.HasChanges()); await isolated.SaveChangesAsync(); delivered.Add(recipient);
            }; return notifications;
        });
        await using var provider = services.BuildServiceProvider(); var runner = new PaymentStatusUpdater(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PaymentStatusUpdater>.Instance);
        await Assert.ThrowsAsync<IOException>(() => runner.RunOnceAsync()); Assert.Contains(user.Id, delivered); Assert.Equal(100m, (await db.Payments.AsNoTracking().SingleAsync()).Amount); Assert.Equal(PaymentStatus.Overdue, (await db.Payments.AsNoTracking().SingleAsync()).Status);
    }
    [Fact]
    public async Task HostedStop_CancelsItsOwnedPageWithoutSaving()
    {
        var barrier = new WaitForStop(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: barrier); await SeedAsync(db, false); barrier.Armed = true;
        using var updater = Runner(db); await updater.StartAsync(CancellationToken.None); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await updater.StopAsync(timeout.Token); Assert.Null(db.Database.CurrentTransaction); Assert.Equal(PaymentStatus.Pending, (await db.Payments.AsNoTracking().SingleAsync()).Status); Assert.False(db.ChangeTracker.HasChanges());
    }
    internal static async Task<Fixture> SeedAsync(PaymentContext db, bool legal)
    {
        var fixture = await BalanceApplicationIdempotencyTests.SeedAsync(db, legal); var payment = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); payment.Date = DateTime.UtcNow.Date.AddDays(-2); payment.PlannedDate = payment.Date; payment.Responsibles.Add(new() { UserId = fixture.ActorId });
        db.RolePermissions.Add(new() { RoleId = (await db.Users.SingleAsync()).RoleId, Section = "accounts", CanView = true }); await db.SaveChangesAsync(); return fixture;
    }
    internal static async Task<Payment> AddRootAsync(PaymentContext db, Fixture fixture, decimal paid)
    {
        var invoice = new Invoice { Number = "Synthetic overdue invoice", Date = DateTime.UtcNow.Date.AddDays(-10) }; db.Invoices.Add(invoice); await db.SaveChangesAsync();
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); source.InvoiceId = invoice.Id;
        var root = PaymentFor(fixture); root.DocumentKind = DocumentKind.Invoice; root.InvoiceId = invoice.Id; root.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft; root.Amount = 100m; root.PaidAmount = paid; root.Status = PaymentStatus.Processing; root.Date = DateTime.UtcNow.Date.AddDays(-2); root.PlannedDate = root.Date; db.Payments.Add(root); await db.SaveChangesAsync(); return root;
    }
    internal static Payment DuePayment(Fixture fixture)
    {
        var payment = PaymentFor(fixture); payment.Date = DateTime.UtcNow.Date.AddDays(-2); payment.PlannedDate = payment.Date; payment.Responsibles.Add(new() { UserId = fixture.ActorId }); return payment;
    }
    internal static PaymentStatusUpdater Runner(PaymentContext db, INotificationService? notifications = null)
        => new(new BorrowedScopes(db, notifications), NullLogger<PaymentStatusUpdater>.Instance);
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
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => Armed ? throw new IOException("Synthetic overdue save failure") : ValueTask.FromResult(result);
    }
    private sealed class SaveSizeProbe : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public List<int> Sizes { get; } = [];
        public List<int> ResponsibleCounts { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed && eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Modified)) { Sizes.Add(eventData.Context.ChangeTracker.Entries<Payment>().Count()); ResponsibleCounts.Add(eventData.Context.ChangeTracker.Entries<PaymentResponsible>().Count()); } return ValueTask.FromResult(result); }
    }
    private sealed class FailPage : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        private int pages;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed && eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Modified) && ++pages == 2) throw new IOException("Synthetic second page failure"); return ValueTask.FromResult(result); }
    }
    private sealed class CancelSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); } return ValueTask.FromResult(result); }
    }
    private sealed class WaitForStop : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return result; }
    }
    public class RecordingNotifications : DispatchProxy
    {
        public List<(int PaymentId, int RecipientId)> Calls { get; } = [];
        public Func<Payment, int, Task>? Before { get; set; }
        public static INotificationService Create() => DispatchProxy.Create<INotificationService, RecordingNotifications>();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name is not (nameof(INotificationService.NotifyPaymentChangedAsync) or nameof(INotificationService.NotifyInvoiceChangedAsync)) || args?[0] is not Payment payment || args[4] is not IEnumerable<int> recipients) throw new InvalidOperationException("Unexpected overdue notification method");
            var recipient = Assert.Single(recipients); Calls.Add((payment.Id, recipient)); return Before?.Invoke(payment, recipient) ?? Task.CompletedTask;
        }
    }
}

public sealed class PaymentOverduePostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, "root")]
    [InlineData(true, "root")]
    [InlineData(false, "workflow")]
    [InlineData(true, "workflow")]
    [InlineData(false, "credit")]
    [InlineData(true, "credit")]
    [InlineData(false, "refund")]
    [InlineData(true, "refund")]
    public async Task NativeProtectedHistory_IsExcludedByTheSamePredicate(bool legal, string reason)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal); var root = await PaymentOverdueBoundaryTests.AddRootAsync(db, fixture, 0m);
        if (reason == "root") root.Status = PaymentStatus.Cancelled;
        if (reason == "workflow") root.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled;
        if (reason == "credit") db.ClientBalanceTransactions.Add(new() { PaymentId = fixture.PaymentId, InvoiceId = root.InvoiceId, ClientId = legal ? null : fixture.OwnerId, LegalEntityId = legal ? fixture.OwnerId : null, Amount = 25m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice });
        if (reason == "refund") db.MoneyOperations.Add(new() { LinkedInvoiceId = root.InvoiceId, Direction = MoneyOperationDirection.Expense, MatchComment = "Возврат оплаты при аннулировании счёта.", Amount = 25m, Date = DateTime.UtcNow.Date });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverdueHoldingPayment_ApplyConflictsThenRetainsTheCredit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; await using (var db = Open(database.ConnectionString)) fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var updating = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString); var pending = PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, fixture, Request(fixture)).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(20))); Assert.Equal(25m, Result(await ApplyAsync(applying, fixture, Request(fixture))).AppliedAmount); Assert.Equal(25m, (await applying.Payments.AsNoTracking().SingleAsync()).PaidAmount); Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(applying).RunOnceAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyHoldingPayment_OverdueCannotOverwriteItsCommittedAmount(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; await using (var db = Open(database.ConnectionString)) fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var applying = Open(database.ConnectionString, barrier); await using var updating = Open(database.ConnectionString); var pending = ApplyAsync(applying, fixture, Request(fixture)); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { var error = await Assert.ThrowsAsync<PostgresException>(() => PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(25m, Result(await pending.WaitAsync(TimeSpan.FromSeconds(20))).AppliedAmount); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating); Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedRepresentative_ProtectsItsDueChildAndAggregate(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); int rootId; await using (var db = Open(database.ConnectionString)) { var fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal); rootId = (await PaymentOverdueBoundaryTests.AddRootAsync(db, fixture, 0m)).Id; }
        await using var blocker = Open(database.ConnectionString); await using var transaction = await blocker.Database.BeginTransactionAsync(); await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {rootId} FOR UPDATE");
        await using var updating = Open(database.ConnectionString); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating); var error = await Assert.ThrowsAsync<PostgresException>(() => PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating)); await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationHoldingRoot_CannotLeaveAnOverdueChild(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); int rootId; await using (var db = Open(database.ConnectionString)) { var fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal); rootId = (await PaymentOverdueBoundaryTests.AddRootAsync(db, fixture, 0m)).Id; }
        await using var cancelling = Open(database.ConnectionString); await using var transaction = await cancelling.Database.BeginTransactionAsync(); (await cancelling.Payments.SingleAsync(p => p.Id == rootId)).Status = PaymentStatus.Cancelled; await cancelling.SaveChangesAsync();
        await using var updating = Open(database.ConnectionString); var error = await Assert.ThrowsAsync<PostgresException>(() => PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); await transaction.CommitAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating); Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailure_RollsBackNativeStatusAndAudit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var db = Open(database.ConnectionString)) await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        await using var updating = Open(database.ConnectionString, new FailAfterSave()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating); var count = await updating.ObjectAuditEvents.CountAsync(); await Assert.ThrowsAsync<IOException>(() => PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating)); Assert.Equal(count, await updating.ObjectAuditEvents.CountAsync()); Assert.Null(updating.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notification_AnotherConnectionSeesCommittedOverdue(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var updating = Open(database.ConnectionString); var fixture = await PaymentOverdueBoundaryTests.SeedAsync(updating, legal); var notifications = PaymentOverdueBoundaryTests.RecordingNotifications.Create(); var probe = (PaymentOverdueBoundaryTests.RecordingNotifications)(object)notifications;
        probe.Before = async (payment, _) => { Assert.Null(updating.Database.CurrentTransaction); await using var observer = Open(database.ConnectionString); Assert.Equal(PaymentStatus.Overdue, (await observer.Payments.SingleAsync(p => p.Id == payment.Id)).Status); Assert.Equal(100m, await observer.ClientBalanceTransactions.SumAsync(t => t.Amount)); };
        Assert.Equal(1, await PaymentOverdueBoundaryTests.Runner(updating, notifications).RunOnceAsync()); Assert.Single(probe.Calls); Assert.Equal(fixture.PaymentId, probe.Calls[0].PaymentId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentJobs_DoNotBothChangeTheSamePayment(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var db = Open(database.ConnectionString)) await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var first = Open(database.ConnectionString, barrier); await using var second = Open(database.ConnectionString); var pending = PaymentOverdueBoundaryTests.Runner(first).RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { var error = await Assert.ThrowsAsync<PostgresException>(() => PaymentOverdueBoundaryTests.Runner(second).RunOnceAsync().WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(20))); Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(second).RunOnceAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitOutcome_PreservesCommittedStatusWithoutASecondWrite(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var db = Open(database.ConnectionString)) await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        var fault = new LostAcknowledgement(); await using var updating = Open(database.ConnectionString, fault); var notifications = PaymentOverdueBoundaryTests.RecordingNotifications.Create(); await Assert.ThrowsAsync<IOException>(() => PaymentOverdueBoundaryTests.Runner(updating, notifications).RunOnceAsync()); Assert.Empty(((PaymentOverdueBoundaryTests.RecordingNotifications)(object)notifications).Calls); Assert.Equal(1, await updating.Payments.CountAsync(p => p.Status == PaymentStatus.Overdue)); var audits = await updating.ObjectAuditEvents.CountAsync();
        fault.Armed = false; Assert.Equal(0, await PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync()); Assert.Equal(audits, await updating.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalPaidAmount_IsNotReconciledByTheNativeJob(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var updating = Open(database.ConnectionString); var fixture = await PaymentOverdueBoundaryTests.SeedAsync(updating, legal); await PaymentOverdueBoundaryTests.AddRootAsync(updating, fixture, 30m); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating); await Assert.ThrowsAsync<InvalidOperationException>(() => PaymentOverdueBoundaryTests.Runner(updating).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(updating));
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
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => throw new IOException("Synthetic native overdue save failure");
    }
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed { get; set; } = true;
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) => Armed ? throw new IOException("Synthetic lost overdue commit acknowledgement") : Task.CompletedTask;
    }
}

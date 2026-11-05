using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    private static async Task<PaymentContext> ReminderContextAsync(string? connection, IInterceptor interceptor)
    {
        if (connection is not null) return new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connection).AddInterceptors(interceptor).Options);
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").AddInterceptors(interceptor).Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }

    public static IEnumerable<object[]> ReminderRollbackCases() =>
        from after in new[] { false, true }
        from cancel in new[] { false, true }
        from postgres in new[] { false, true }
        select new object[] { after, cancel, postgres };

    [Theory]
    [MemberData(nameof(ReminderRollbackCases))]
    public async Task CourtReminder_AtomicFailureLeavesNeitherInboxNorDeliveryIntent(bool after, bool cancel, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        var fault = new ReminderInsertFault(after, cancel);
        await using var db = await ReminderContextAsync(database?.ConnectionString, fault);
        var seed = await SeedReminderAsync(db);
        var domain = await ReminderDomainAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Armed = true; fault.Cancel = cancellation.Cancel;
        var publisher = new ReminderAdmissionRealtime();
        Task Save() => ReminderService(db, publisher).NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])], cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Save);
        else await Assert.ThrowsAsync<IOException>(Save);
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
        db.ChangeTracker.Clear();
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
        Assert.Equal(domain, await ReminderDomainAsync(db));
        Assert.Empty(publisher.Delivered);
        fault.Armed = false;
        var service = ReminderService(db, publisher);
        Assert.Equal(1, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "reminder");
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(intent.Id, default));
        Assert.Single(publisher.Delivered);
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtReminder_RejectsCallerTransactionsAndPendingBusinessChanges(bool ambient, bool postgres)
    {
        await using var database = postgres ? await SecondaryTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? OpenPostgres(database!.ConnectionString) : await OpenSqliteAsync();
        var seed = await SeedReminderAsync(db);
        await using var transaction = ambient ? await db.Database.BeginTransactionAsync() : null;
        if (!ambient) seed.Case.Title = "Не сохранять чужое изменение";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReminderService(db, new ReminderAdmissionRealtime())
            .NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        if (ambient) Assert.Same(transaction, db.Database.CurrentTransaction);
        else
        {
            Assert.True(db.ChangeTracker.HasChanges());
            Assert.Equal("Не сохранять чужое изменение", seed.Case.Title);
        }
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
        if (transaction is not null) await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtReminder_NativeConcurrentAdmissionDoesNotDuplicateOrBlockIndependentCase(bool independent)
    {
        await using var database = await SecondaryTemplate.CreateDatabaseAsync();
        ReminderSeed seed;
        ClientCase other;
        ClientCaseProceduralDeadline otherDeadline;
        string domain;
        await using (var setup = OpenPostgres(database.ConnectionString))
        {
            seed = await SeedReminderAsync(setup);
            other = new() { ClientId = seed.Case.ClientId, Title = "Независимое дело", Stage = "Суд", ResponsibleUserId = seed.UserId };
            otherDeadline = new() { ClientCase = other, Title = "Независимый срок", DeadlineType = "appeal", DueAt = seed.Deadline.DueAt, Status = "active", WarningDaysBefore = 7 };
            setup.ClientCaseProceduralDeadlines.Add(otherDeadline); await setup.SaveChangesAsync();
            domain = await ReminderDomainAsync(setup);
        }
        var gate = new ReminderAdmissionGate();
        await using var first = await ReminderContextAsync(database.ConnectionString, gate);
        var writer = ReminderService(first, new ReminderAdmissionRealtime()).NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]);
        try
        {
            var ready = await Task.WhenAny(writer, gate.Started.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (ready == writer) await writer;
            Assert.True(gate.Started.Task.IsCompleted);
            await using var second = OpenPostgres(database.ConnectionString);
            var inputs = new List<CaseProceduralDeadlineReminderBatchItem> { new(seed.Case, seed.Deadline, [seed.UserId]) };
            if (independent) inputs.Add(new(other, otherDeadline, [seed.UserId]));
            Assert.Equal(independent ? 1 : 0, await ReminderService(second, new ReminderAdmissionRealtime())
                .NotifyCaseProceduralDeadlineRemindersAsync(inputs).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(1, await writer.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var verify = OpenPostgres(database.ConnectionString);
        Assert.Equal(independent ? 2 : 1, await verify.UserNotifications.CountAsync());
        Assert.Equal(independent ? 2 : 1, await verify.CaseCourtNotificationOutboxes.CountAsync(row => row.Kind == "reminder"));
        Assert.Equal(domain, await ReminderDomainAsync(verify));
    }

    private sealed class ReminderInsertFault(bool after, bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action? Cancel { get; set; }
        private bool trigger;
        private void Fail(CancellationToken ct)
        {
            Called = true;
            if (cancel) { Cancel!(); throw new OperationCanceledException(ct); }
            throw new IOException("Synthetic reminder admission failure");
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            trigger = Armed && eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added && row.Entity.Kind == "reminder");
            if (trigger && !after) Fail(cancellationToken);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (trigger && after) { trigger = false; Fail(cancellationToken); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReminderAdmissionGate : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added && row.Entity.Kind == "reminder"))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}

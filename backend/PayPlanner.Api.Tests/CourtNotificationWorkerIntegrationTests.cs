using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtNotificationWorkerIntegrationTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtNoticeWorker_RestartRecoversFailedOrInterruptedJournalWithoutTouchingCase(bool postgres, bool interrupt)
    {
        await using var database = postgres ? await template.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await OpenSqliteAsync();
        var id = await SeedAsync(db);
        var before = await db.ClientCases.AsNoTracking().Select(row => new { row.Id, row.Title, row.EventHistory }).SingleAsync();
        var state = new WorkerState { Fail = !interrupt, Block = interrupt };
        await using var provider = Services(db, postgres ? database!.ConnectionString : null, state);
        var factory = new WatchedScopes(provider.GetRequiredService<IServiceScopeFactory>());
        using (var worker = new CaseCourtNotificationDispatchWorker(factory, NullLogger<CaseCourtNotificationDispatchWorker>.Instance))
        {
            await worker.StartAsync(default);
            if (interrupt) await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            else await factory.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
            Assert.True(worker.ExecuteTask!.IsCompleted);
            Assert.False(worker.ExecuteTask.IsFaulted);
        }
        var retained = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Null(retained.CompletedAt);
        if (interrupt)
        {
            Assert.NotNull(retained.LeaseToken);
            Assert.NotNull(retained.LeaseUntil);
            Assert.Null(retained.NotificationCompletedAt);
        }
        else
        {
            Assert.Null(retained.LeaseToken);
            Assert.NotNull(retained.NotificationCompletedAt);
        }
        state.Fail = state.Block = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch).SetProperty(row => row.LeaseUntil, (DateTime?)DateTime.UnixEpoch));
        var resumed = new WatchedScopes(provider.GetRequiredService<IServiceScopeFactory>());
        using (var worker = new CaseCourtNotificationDispatchWorker(resumed, NullLogger<CaseCourtNotificationDispatchWorker>.Instance))
        {
            await worker.StartAsync(default);
            await resumed.Closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
            Assert.False(worker.ExecuteTask!.IsFaulted);
        }
        var complete = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.NotNull(complete.CompletedAt);
        Assert.NotNull(complete.JournalCompletedAt);
        Assert.NotNull(complete.NotificationCompletedAt);
        Assert.Null(complete.LeaseToken);
        Assert.Equal(2, state.Calls);
        Assert.Equal(before, await db.ClientCases.AsNoTracking().Select(row => new { row.Id, row.Title, row.EventHistory }).SingleAsync());
        Assert.Empty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
    }

    [Fact]
    public async Task CourtNoticeWorker_ScopeFailureIsIsolatedAndNextTickStillCompletesIntent()
    {
        await using var db = await OpenSqliteAsync();
        await SeedAsync(db);
        var state = new WorkerState();
        await using var provider = Services(db, null, state);
        var factory = new WatchedScopes(provider.GetRequiredService<IServiceScopeFactory>(), failFirst: true);
        using var worker = new CaseCourtNotificationDispatchWorker(factory, NullLogger<CaseCourtNotificationDispatchWorker>.Instance);
        await worker.StartAsync(default);
        await factory.Thrown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await factory.Closed.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
        }
        Assert.Equal(2, factory.Calls);
        Assert.Equal(1, state.Calls);
        Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
        Assert.False(worker.ExecuteTask!.IsFaulted);
    }

    private static ServiceProvider Services(PaymentContext seedDb, string? connection, WorkerState state)
    {
        var services = new ServiceCollection();
        services.AddScoped<PaymentContext>(_ => connection is null
            ? new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(seedDb.Database.GetDbConnection()).Options)
            : new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options));
        services.AddScoped<IDurableCourtNotificationService, ForbiddenCourtDelivery>();
        services.AddScoped<IRealtimeNotificationPublisher>(sp => new WorkerJournal(sp.GetRequiredService<PaymentContext>(), state));
        services.AddScoped<CaseCourtNotificationDispatchProcessor>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
    private static async Task<PaymentContext> OpenSqliteAsync()
    {
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }
    private static async Task<long> SeedAsync(PaymentContext db)
    {
        var clientCase = new ClientCase { Title = "Синтетическое дело фонового восстановления", Client = new Client { Name = "Синтетический клиент" } };
        db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
        var email = await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled");
        if (email is null) db.SystemSettings.Add(new() { Key = "notifications.email.enabled", Value = "false" }); else email.Value = "false";
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var id = Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(clientCase, 0, "Сотрудник",
            [new("journal", "updated", null, null, true)], default));
        await transaction.CommitAsync(); db.ChangeTracker.Clear(); return id;
    }
    private sealed class WorkerState
    {
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class WorkerJournal(PaymentContext db, WorkerState state) : IRealtimeNotificationPublisher, IReliableTaskJournalChangePublisher
    {
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Journal recovery must not publish a user notification");
        public async Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Interlocked.Increment(ref state.Calls);
            state.Entered.TrySetResult();
            if (state.Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (state.Fail) throw new IOException("Synthetic journal failure");
        }
        public Task PublishTaskJournalChangedOrThrowAsync(CancellationToken cancellationToken = default) => PublishTaskJournalChangedAsync(cancellationToken);
    }
    private sealed class ForbiddenCourtDelivery : IDurableCourtNotificationService
    {
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCourtEventForEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent,
            string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
            => throw new InvalidOperationException("Journal recovery must not deliver court event");
        public Task<IReadOnlyList<NotificationDelivery>> NotifyProceduralDeadlineForEventAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline,
            string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
            => throw new InvalidOperationException("Journal recovery must not deliver deadline");
    }
    private sealed class WatchedScopes(IServiceScopeFactory inner, bool failFirst = false) : IServiceScopeFactory
    {
        public int Calls;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Thrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref Calls) == 1 && failFirst)
            {
                Thrown.TrySetResult(); throw new IOException("Synthetic scope failure");
            }
            return new WatchedScope(inner.CreateScope(), Closed);
        }
    }
    private sealed class WatchedScope(IServiceScope inner, TaskCompletionSource closed) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;
        public void Dispose() { inner.Dispose(); closed.TrySetResult(); }
        public async ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asyncScope) await asyncScope.DisposeAsync(); else inner.Dispose();
            closed.TrySetResult();
        }
    }
}

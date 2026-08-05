using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ExternalCalendarTaskSyncOutboxServiceTests
{
    [Fact]
    public async Task EnqueueAsync_PersistsRequestAndWakesWorker()
    {
        await using var db = await CreateContextAsync();
        var task = await CreateTaskAsync(db);
        var signal = new ExternalCalendarTaskSyncWakeSignal();
        var wait = signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        var outbox = new ExternalCalendarTaskSyncOutbox(db, signal);

        await outbox.EnqueueAsync(task.Id, "CRM", CancellationToken.None);
        await wait.WaitAsync(TimeSpan.FromSeconds(1));

        var request = await db.ExternalCalendarTaskSyncRequests.AsNoTracking().SingleAsync();
        Assert.Equal(task.Id, request.TaskJournalItemId);
        Assert.Equal(ExternalCalendarSyncCoordinator.CrmProvider, request.SourceProvider);
        Assert.Equal(ExternalCalendarTaskSyncRequestStatuses.Queued, request.Status);
        Assert.Equal(0, request.AttemptCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_CoalescesRepeatedTaskChanges()
    {
        await using var db = await CreateContextAsync();
        var task = await CreateTaskAsync(db);
        db.ExternalCalendarTaskSyncRequests.AddRange(
            CreateRequest(task.Id, DateTime.UtcNow.AddSeconds(-3)),
            CreateRequest(task.Id, DateTime.UtcNow.AddSeconds(-2)),
            CreateRequest(task.Id, DateTime.UtcNow.AddSeconds(-1)));
        await db.SaveChangesAsync();
        var coordinator = new RecordingCoordinator();
        var processor = CreateProcessor(db, coordinator);

        var processed = await processor.ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Equal([(task.Id, ExternalCalendarSyncCoordinator.CrmProvider)], coordinator.SyncedTasks);
        Assert.Empty(await db.ExternalCalendarTaskSyncRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ProcessBatchAsync_OnUnexpectedFailureKeepsOnlyNewestRequestForRetry()
    {
        await using var db = await CreateContextAsync();
        var task = await CreateTaskAsync(db);
        db.ExternalCalendarTaskSyncRequests.AddRange(
            CreateRequest(task.Id, DateTime.UtcNow.AddSeconds(-2)),
            CreateRequest(task.Id, DateTime.UtcNow.AddSeconds(-1)));
        await db.SaveChangesAsync();
        var processor = CreateProcessor(db, new RecordingCoordinator(throwOnSync: true));

        var processed = await processor.ProcessBatchAsync();

        Assert.Equal(1, processed);
        var request = await db.ExternalCalendarTaskSyncRequests.AsNoTracking().SingleAsync();
        Assert.Equal(1, request.AttemptCount);
        Assert.Equal(ExternalCalendarTaskSyncRequestStatuses.Queued, request.Status);
        Assert.NotNull(request.LastError);
        Assert.True(request.NextAttemptAt > DateTime.UtcNow);
    }

    private static ExternalCalendarTaskSyncOutboxProcessor CreateProcessor(
        PaymentContext db,
        IExternalCalendarSyncCoordinator coordinator)
        => new(
            db,
            coordinator,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<ExternalCalendarTaskSyncOutboxProcessor>.Instance);

    private static ExternalCalendarTaskSyncRequest CreateRequest(int taskId, DateTime createdAt)
        => new()
        {
            TaskJournalItemId = taskId,
            SourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
            Status = ExternalCalendarTaskSyncRequestStatuses.Queued,
            NextAttemptAt = createdAt,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<TaskJournalItem> CreateTaskAsync(PaymentContext db)
    {
        var task = new TaskJournalItem
        {
            ObjectName = "Внутренняя задача",
            Title = "Проверить очередь календаря",
            AuthorName = "Автор",
            AssigneeName = "Исполнитель",
            DueAt = DateTime.UtcNow.AddHours(1),
            Status = "new",
        };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private sealed class RecordingCoordinator(bool throwOnSync = false) : IExternalCalendarSyncCoordinator
    {
        public List<(int TaskId, string SourceProvider)> SyncedTasks { get; } = [];

        public Task SyncTaskForConnectedUsersAsync(
            int taskId,
            string sourceProvider = ExternalCalendarSyncCoordinator.CrmProvider,
            CancellationToken cancellationToken = default)
        {
            SyncedTasks.Add((taskId, sourceProvider));
            return throwOnSync
                ? Task.FromException(new InvalidOperationException("Внешний календарь временно недоступен."))
                : Task.CompletedTask;
        }

        public Task DeleteTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task HandleUserAccessRevokedAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

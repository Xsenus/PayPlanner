using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SystemJobsControllerTests : IClassFixture<PostgreSqlModelTemplate>
{
    private readonly PostgreSqlModelTemplate deadlineBatchTemplate;

    public SystemJobsControllerTests(PostgreSqlModelTemplate deadlineBatchTemplate)
        => this.deadlineBatchTemplate = deadlineBatchTemplate;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineBatch_DeploymentValidationDefersSynchronizationAndResumesWithoutRestart(bool postgres)
    {
        await using var database = postgres ? await deadlineBatchTemplate.CreateDatabaseAsync() : null;
        await using var sqlite = postgres ? null : await CreateDbScopeAsync();
        PaymentContext Open() => OpenDeadlineBatchContext(database?.ConnectionString, sqlite?.Options, []);
        await using var seedDb = Open();
        var seed = await SeedDeadlineBatchAsync(seedDb);
        var casesBefore = await DeadlineBatchCasesSnapshotAsync(seedDb);
        var notifications = new RecordingTaskReminderNotificationService();
        var runs = new DeadlineBatchRuns();
        var realtime = new DeadlineBatchRealtime(Open);
        await using var provider = BuildDeadlineBatchProvider(Open, notifications, realtime);
        var marker = Path.Combine(Path.GetTempPath(), "payplanner-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(marker, "synthetic release");
            using var service = CreateDeadlineBatchService(provider, runs, 3, marker);
            Assert.Equal(1, await service.RunOnceAsync());
            Assert.Empty(await DeadlineBatchTaskCaseIdsAsync(seedDb));
            Assert.Empty(realtime.CommittedTaskCounts);
            Assert.Equal((seed.ReminderId, "before15m"), Assert.Single(notifications.Reminders));
            await seedDb.TaskJournalItems.Where(row => row.Id == seed.ReminderId)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ChannelsJson, "[]"));

            File.Delete(marker);
            Assert.Equal(0, await service.RunOnceAsync());
            Assert.Equal(seed.CaseIds.Order(), await DeadlineBatchTaskCaseIdsAsync(seedDb));
            Assert.Equal(3, Assert.Single(realtime.CommittedTaskCounts));
            Assert.Equal(casesBefore, await DeadlineBatchCasesSnapshotAsync(seedDb));
            Assert.Equal("success", runs.Status);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public async Task DeadlineBatch_BusyCaseDoesNotStarveFollowingCasesOrIndependentReminders(int batchSize, int busyIndex)
    {
        await using var database = await deadlineBatchTemplate.CreateDatabaseAsync();
        PaymentContext Open() => OpenDeadlineBatchContext(database.ConnectionString, null, []);
        await using var seedDb = Open();
        var seed = await SeedDeadlineBatchAsync(seedDb);
        var casesBefore = await DeadlineBatchCasesSnapshotAsync(seedDb);
        var notifications = new RecordingTaskReminderNotificationService();
        var runs = new DeadlineBatchRuns();
        var realtime = new DeadlineBatchRealtime(Open);
        await using var provider = BuildDeadlineBatchProvider(Open, notifications, realtime);
        using var service = CreateDeadlineBatchService(provider, runs, batchSize);
        await using var locker = Open();
        await using var transaction = await locker.Database.BeginTransactionAsync();
        var busyId = seed.CaseIds[busyIndex];
        await locker.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {busyId} FOR UPDATE");

        var passes = batchSize == 1 ? seed.CaseIds.Length : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            Assert.Equal(pass == 0 ? 1 : 0, await service.RunOnceAsync());
            if (pass == 0)
                await seedDb.TaskJournalItems.Where(row => row.Id == seed.ReminderId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ChannelsJson, "[]"));
        }
        Assert.Equal("success", runs.Status);
        Assert.Null(runs.Error);
        Assert.Equal((seed.ReminderId, "before15m"), Assert.Single(notifications.Reminders));
        var healthyIds = seed.CaseIds.Where(id => id != busyId).Order().ToArray();
        Assert.Equal(healthyIds, await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(batchSize == 1 ? 2 : 1, realtime.CommittedTaskCounts.Count);
        Assert.All(realtime.CommittedTaskCounts, count => Assert.True(count > 0));

        await transaction.RollbackAsync();
        for (var pass = 0; pass < seed.CaseIds.Length; pass++) Assert.Equal(0, await service.RunOnceAsync());
        Assert.Equal(seed.CaseIds.Order(), await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(casesBefore, await DeadlineBatchCasesSnapshotAsync(seedDb));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineBatch_ConfirmedConflictAfterActualSaveContinuesAndRetriesWithoutPartialGraph(bool postgres)
    {
        await using var database = postgres ? await deadlineBatchTemplate.CreateDatabaseAsync() : null;
        await using var sqlite = postgres ? null : await CreateDbScopeAsync();
        var fault = new DeadlineBatchFault("conflict");
        var transactionFault = new DeadlineBatchTransactionFault(fault);
        PaymentContext Open() => OpenDeadlineBatchContext(database?.ConnectionString, sqlite?.Options, [fault, transactionFault]);
        await using var seedDb = Open();
        var seed = await SeedDeadlineBatchAsync(seedDb);
        fault.CaseId = seed.CaseIds[0];
        fault.Armed = true;
        var notifications = new RecordingTaskReminderNotificationService();
        var runs = new DeadlineBatchRuns();
        var realtime = new DeadlineBatchRealtime(Open);
        await using var provider = BuildDeadlineBatchProvider(Open, notifications, realtime);
        using var service = CreateDeadlineBatchService(provider, runs, 3);

        Assert.Equal(1, await service.RunOnceAsync());
        Assert.True(fault.Called);
        Assert.Equal("success", runs.Status);
        Assert.Equal(seed.CaseIds.Skip(1), await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(2, Assert.Single(realtime.CommittedTaskCounts));
        Assert.Equal(0, await seedDb.TaskJournalComments.CountAsync(comment => !seedDb.TaskJournalItems.Any(task => task.Id == comment.TaskJournalItemId)));
        await seedDb.TaskJournalItems.Where(row => row.Id == seed.ReminderId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ChannelsJson, "[]"));
        // Reusing the same exception after COMMIT must not reuse a prior rollback proof.
        fault.Mode = "commit";
        fault.Armed = true;
        Assert.Equal(-1, await service.RunOnceAsync());
        Assert.Equal("failed", runs.Status);
        Assert.Same(fault.Conflict, runs.Error);
        Assert.Equal(seed.CaseIds.Order(), await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(0, await service.RunOnceAsync());
        Assert.Equal("success", runs.Status);
        Assert.Equal(seed.CaseIds.Order(), await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(3, await seedDb.TaskJournalComments.CountAsync(row => row.Kind == "system"));
        Assert.Equal(3, await seedDb.TaskJournalParticipants.CountAsync());
        Assert.Equal(new[] { 2 }, realtime.CommittedTaskCounts);
    }

    public static IEnumerable<object[]> DeadlineBatchFatalCases() =>
        from postgres in new[] { false, true }
        from mode in new[] { "io", "cancel", "commit", "rollback" }
        select new object[] { postgres, mode };

    [Theory]
    [MemberData(nameof(DeadlineBatchFatalCases))]
    public async Task DeadlineBatch_CancellationAndUnconfirmedFailuresRemainExplicitAndCanBeRetried(bool postgres, string mode)
    {
        await using var database = postgres ? await deadlineBatchTemplate.CreateDatabaseAsync() : null;
        await using var sqlite = postgres ? null : await CreateDbScopeAsync();
        var fault = new DeadlineBatchFault(mode);
        var transactionFault = new DeadlineBatchTransactionFault(fault);
        PaymentContext Open() => OpenDeadlineBatchContext(database?.ConnectionString, sqlite?.Options, [fault, transactionFault]);
        await using var seedDb = Open();
        var seed = await SeedDeadlineBatchAsync(seedDb);
        fault.CaseId = seed.CaseIds[0];
        fault.Armed = true;
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        var notifications = new RecordingTaskReminderNotificationService();
        var runs = new DeadlineBatchRuns();
        var realtime = new DeadlineBatchRealtime(Open);
        await using var provider = BuildDeadlineBatchProvider(Open, notifications, realtime);
        using var service = CreateDeadlineBatchService(provider, runs, 3);
        if (mode == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(cancellation.Token));
            Assert.Null(runs.Status);
        }
        else
        {
            Assert.Equal(-1, await service.RunOnceAsync());
            Assert.Equal("failed", runs.Status);
            Assert.NotNull(runs.Error);
            if (mode == "commit") Assert.IsType<DbUpdateConcurrencyException>(runs.Error);
            if (mode == "rollback") Assert.True(runs.Error.Data.Contains("CaseTaskSyncRollbackFailure"));
        }
        Assert.True(fault.Called);
        Assert.Empty(notifications.Reminders);
        Assert.Empty(realtime.CommittedTaskCounts);
        Assert.Equal(mode == "commit" ? new[] { seed.CaseIds[0] } : [], await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Equal(1, await service.RunOnceAsync());
        Assert.Equal("success", runs.Status);
        Assert.Equal(seed.CaseIds.Order(), await DeadlineBatchTaskCaseIdsAsync(seedDb));
        Assert.Single(notifications.Reminders);
    }

    private static PaymentContext OpenDeadlineBatchContext(string? postgres, DbContextOptions<PaymentContext>? sqlite, IInterceptor[] interceptors)
        => postgres is not null
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(postgres).AddInterceptors(interceptors).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>(sqlite!).AddInterceptors(interceptors).Options, useObjectAuditModel: false);

    private sealed record DeadlineBatchSeed(int[] CaseIds, int ReminderId);

    private static async Task<DeadlineBatchSeed> SeedDeadlineBatchAsync(PaymentContext db)
    {
        var role = new Role { Name = "deadline-batch-role", Description = "Synthetic deadline batch" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User { Email = "deadline-batch@payplanner.local", FullName = "Deadline Owner", PasswordHash = "hash", RoleId = role.Id, IsActive = true, IsApproved = true };
        var client = new Client { Name = "Deadline batch client" };
        db.Users.Add(user);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var cases = Enumerable.Range(1, 3).Select(index => new ClientCase
        {
            ClientId = client.Id,
            ResponsibleUserId = user.Id,
            CaseNumber = "DEADLINE-BATCH-" + index,
            Title = "Deadline batch " + index,
            Status = ClientCaseStatus.Open,
            DueDate = DateTime.UtcNow.Date.AddDays(8)
        }).ToArray();
        var reminder = new TaskJournalItem
        {
            ObjectName = "Independent reminder",
            Title = "Existing independent reminder",
            AssigneeUserId = user.Id,
            DueAt = DateTime.UtcNow.AddMinutes(10),
            Status = "new",
            ChannelsJson = "[\"inApp\"]",
            RemindersJson = "[\"before15m\"]"
        };
        db.ClientCases.AddRange(cases);
        db.TaskJournalItems.Add(reminder);
        await db.SaveChangesAsync();
        return new(cases.Select(row => row.Id).ToArray(), reminder.Id);
    }

    private static ServiceProvider BuildDeadlineBatchProvider(Func<PaymentContext> open, INotificationService notifications, IRealtimeNotificationPublisher realtime)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => open());
        services.AddSingleton(notifications);
        services.AddSingleton(realtime);
        return services.BuildServiceProvider();
    }

    private static TaskJournalReminderNotificationService CreateDeadlineBatchService(ServiceProvider provider, ISystemJobRunStateService runs, int batchSize, string? validationMarker = null)
        => new(provider.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:ValidationMarkerPath"] = validationMarker,
            ["Notifications:TaskReminders:DeadlineSyncLookAheadDays"] = "14",
            ["Notifications:TaskReminders:DeadlineSyncBatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build(), NullLogger<TaskJournalReminderNotificationService>.Instance, runs);

    private static async Task<int[]> DeadlineBatchTaskCaseIdsAsync(PaymentContext db)
        => await db.TaskJournalItems.AsNoTracking().Where(row => row.TagsJson.Contains("case-deadline:due"))
            .OrderBy(row => row.ClientCaseId).Select(row => row.ClientCaseId!.Value).ToArrayAsync();

    private static async Task<string> DeadlineBatchCasesSnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(await db.ClientCases.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientId, row.Title, row.Status, row.DueDate, row.FeeAmount }).ToArrayAsync());

    private sealed class DeadlineBatchRealtime(Func<PaymentContext> open) : IRealtimeNotificationPublisher
    {
        public List<int> CommittedTaskCounts { get; } = [];
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public async Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            await using var db = open();
            CommittedTaskCounts.Add((await DeadlineBatchTaskCaseIdsAsync(db)).Length);
        }
    }

    private sealed class DeadlineBatchRuns : ISystemJobRunStateService
    {
        public string? Status { get; private set; }
        public Exception? Error { get; private set; }
        public Task RecordSuccessAsync(string jobKey, DateTime startedAt, int? processedCount, int? skippedCount, string summary, CancellationToken cancellationToken = default)
        {
            Status = "success";
            Error = null;
            return Task.CompletedTask;
        }
        public Task RecordFailureAsync(string jobKey, DateTime startedAt, Exception exception, string? summary = null, CancellationToken cancellationToken = default)
        {
            Status = "failed";
            Error = exception;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, SystemJobRunState>> GetLastRunsAsync(IEnumerable<string> jobKeys, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, SystemJobRunState>>(new Dictionary<string, SystemJobRunState>());
    }

    private sealed class DeadlineBatchFault(string mode) : SaveChangesInterceptor
    {
        public string Mode { get; set; } = mode;
        public DbUpdateConcurrencyException Conflict { get; } = new("Synthetic deadline batch persistence conflict.");
        public bool Armed { get; set; }
        public int CaseId { get; set; }
        public bool Called { get; set; }
        public Action? Cancel { get; set; }
        public bool Targets(DbContext? db) => Armed && db?.ChangeTracker.Entries<TaskJournalItem>().Any(entry => entry.Entity.ClientCaseId == CaseId) == true;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Targets(eventData.Context) || Mode == "commit") return ValueTask.FromResult(result);
            Called = true;
            if (Mode != "rollback") Armed = false;
            if (Mode == "cancel") { Cancel!(); throw new OperationCanceledException(cancellationToken); }
            if (Mode == "io") throw new IOException("Synthetic deadline batch save failure.");
            throw Conflict;
        }
    }

    private sealed class DeadlineBatchTransactionFault(DeadlineBatchFault fault) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (fault.Mode != "commit" || !fault.Targets(eventData.Context)) return Task.CompletedTask;
            fault.Called = true;
            fault.Armed = false;
            throw fault.Conflict;
        }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (fault.Mode == "rollback" && fault.Called && fault.Armed)
            {
                fault.Armed = false;
                throw new IOException("Synthetic unconfirmed deadline batch rollback.");
            }
            return ValueTask.FromResult(result);
        }
    }
}

using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestOutboxOwnershipTests(PostgreSqlMigratedTemplate template)
    : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(false, "lease-success")]
    [InlineData(true, "lease-success")]
    [InlineData(false, "lease-failure")]
    [InlineData(true, "lease-failure")]
    [InlineData(false, "delivered-success")]
    [InlineData(true, "delivered-success")]
    [InlineData(false, "delivered-failure")]
    [InlineData(true, "delivered-failure")]
    [InlineData(false, "abandoned-failure")]
    [InlineData(true, "abandoned-failure")]
    [InlineData(false, "closed")]
    [InlineData(true, "closed")]
    public async Task StaleOutcomeCannotOverwriteSuccessorOrTerminalState(bool postgres, string scenario)
    {
        var lookup = new TaskLookupInterception();
        await using var fixture = await CreateAsync(postgres, lookup);
        var row = await SeedAsync(fixture.Db, scenario == "closed");
        var proxy = DispatchProxy.Create<INotificationService, NotificationProxy>();
        var notification = (NotificationProxy)(object)proxy;
        notification.Fail = scenario.EndsWith("failure", StringComparison.Ordinal);
        SalesRequestNotificationOutbox? expected = null;
        async Task ReplaceAsync()
        {
            await using var successor = fixture.NewContext();
            var claimed = await successor.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync();
            var terminalTime = DateTime.UtcNow.AddMinutes(-1);
            var lease = scenario.StartsWith("lease", StringComparison.Ordinal) || scenario == "closed"
                ? DateTime.UtcNow.AddMinutes(10) : claimed.NextAttemptAt;
            DateTime? delivered = scenario.StartsWith("delivered", StringComparison.Ordinal) ? terminalTime : null;
            DateTime? abandoned = scenario.StartsWith("abandoned", StringComparison.Ordinal) ? terminalTime : null;
            await successor.SalesRequestNotificationOutbox.Where(item => item.Id == row.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.NextAttemptAt, lease)
                    .SetProperty(item => item.AttemptCount, 3).SetProperty(item => item.LastError, "successor outcome")
                    .SetProperty(item => item.DeliveredAt, delivered).SetProperty(item => item.AbandonedAt, abandoned));
            expected = await successor.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync();
        }
        if (scenario == "closed")
        {
            lookup.Table = fixture.Db.Model.FindEntityType(typeof(TaskJournalItem))!.GetTableName()!;
            lookup.BeforeLookup = ReplaceAsync;
        }
        else notification.BeforeOutcome = ReplaceAsync;

        Assert.Equal(0, await new SalesRequestService(fixture.Db, proxy).DeliverPendingNotificationsAsync(default));
        Assert.NotNull(expected);
        var actual = await fixture.Db.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(expected.NextAttemptAt, actual.NextAttemptAt);
        Assert.Equal(expected.AttemptCount, actual.AttemptCount);
        Assert.Equal(expected.LastError, actual.LastError);
        Assert.Equal(expected.DeliveredAt, actual.DeliveredAt);
        Assert.Equal(expected.AbandonedAt, actual.AbandonedAt);
        Assert.Equal(scenario == "closed" ? 0 : 1, notification.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OwnedOutcomeStillRecordsSuccessOrRetry(bool postgres, bool fail)
    {
        await using var fixture = await CreateAsync(postgres);
        await SeedAsync(fixture.Db, false);
        var proxy = DispatchProxy.Create<INotificationService, NotificationProxy>();
        var notification = (NotificationProxy)(object)proxy;
        notification.Fail = fail;
        var service = new SalesRequestService(fixture.Db, proxy);
        Assert.Equal(fail ? 0 : 1, await service.DeliverPendingNotificationsAsync(default));
        var actual = await fixture.Db.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(fail ? 1 : 0, actual.AttemptCount);
        Assert.Equal(!fail, actual.DeliveredAt.HasValue);
        Assert.Null(actual.AbandonedAt);
        if (fail)
        {
            Assert.Equal("owned notification failure", actual.LastError);
            Assert.True(actual.NextAttemptAt > DateTime.UtcNow);
        }
        else Assert.Null(actual.LastError);
        Assert.Equal(0, await service.DeliverPendingNotificationsAsync(default));
        Assert.Equal(1, notification.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OwnedClosedOrMissingTaskIsAbandoned(bool postgres, bool missingTask)
    {
        await using var fixture = await CreateAsync(postgres);
        var row = await SeedAsync(fixture.Db, !missingTask);
        if (missingTask) await fixture.Db.TaskJournalItems.Where(task => task.Id == row.TaskId).ExecuteDeleteAsync();
        var proxy = DispatchProxy.Create<INotificationService, NotificationProxy>();
        Assert.Equal(0, await new SalesRequestService(fixture.Db, proxy).DeliverPendingNotificationsAsync(default));
        var actual = await fixture.Db.SalesRequestNotificationOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(actual.AbandonedAt);
        Assert.Null(actual.DeliveredAt);
        Assert.Equal(0, actual.AttemptCount);
        Assert.Equal("Задача закрыта или удалена.", actual.LastError);
        Assert.Equal(0, ((NotificationProxy)(object)proxy).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryTaskGetsALeaseFromItsOwnClaimTime(bool postgres)
    {
        await using var fixture = await CreateAsync(postgres);
        await SeedAsync(fixture.Db, false);
        await SeedAsync(fixture.Db, false);
        var proxy = DispatchProxy.Create<INotificationService, NotificationProxy>();
        var notification = (NotificationProxy)(object)proxy;
        var leases = new List<DateTime>();
        notification.BeforeOutcome = async () =>
        {
            await using var observer = fixture.NewContext();
            leases.Add(await observer.SalesRequestNotificationOutbox.Where(row => row.TaskId == notification.TaskId)
                .Select(row => row.NextAttemptAt).SingleAsync());
            if (leases.Count == 1) await Task.Delay(50);
        };
        Assert.Equal(2, await new SalesRequestService(fixture.Db, proxy).DeliverPendingNotificationsAsync(default));
        Assert.Equal(2, leases.Count);
        Assert.True(leases[1] > leases[0], "A slow earlier delivery must not shorten the next task's lease.");
        Assert.Equal(2, await fixture.Db.SalesRequestNotificationOutbox.CountAsync(row => row.DeliveredAt != null));
    }

    private async Task<Fixture> CreateAsync(bool postgres, params IInterceptor[] interceptors)
    {
        if (postgres)
        {
            var lease = await template.CreateDatabaseAsync("pp_sales_outbox_");
            PaymentContext Create() => new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(lease.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: false);
            return new(Create(), Create, lease);
        }
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        PaymentContext New() => new(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).AddInterceptors(interceptors).Options);
        var db = New();
        await db.Database.EnsureCreatedAsync();
        return new(db, New, connection);
    }

    private static async Task<SalesRequestNotificationOutbox> SeedAsync(PaymentContext db, bool closed)
    {
        var task = new TaskJournalItem { Title = "Owned notification task", Status = closed ? "cancelled" : "new", DueAt = DateTime.UtcNow.AddDays(1) };
        db.TaskJournalItems.Add(task);
        await db.SaveChangesAsync();
        var row = new SalesRequestNotificationOutbox { TaskId = task.Id, Key = $"task:{task.Id}:assigned", NextAttemptAt = DateTime.UtcNow.AddMinutes(-1) };
        db.SalesRequestNotificationOutbox.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private sealed record Fixture(PaymentContext Db, Func<PaymentContext> NewContext, IAsyncDisposable Resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Resource.DisposeAsync();
        }
    }

    public class NotificationProxy : DispatchProxy
    {
        public Func<Task>? BeforeOutcome { get; set; }
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public int TaskId { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(INotificationService.NotifyTaskAssignedAsync), targetMethod?.Name);
            TaskId = ((TaskJournalItem)args![0]!).Id;
            return CompleteAsync();
        }
        private async Task<IReadOnlyList<NotificationDelivery>> CompleteAsync()
        {
            Calls++;
            if (BeforeOutcome is not null) await BeforeOutcome();
            if (Fail) throw new InvalidOperationException("owned notification failure");
            return [];
        }
    }

    private sealed class TaskLookupInterception : DbCommandInterceptor
    {
        internal string? Table;
        internal Func<Task>? BeforeLookup;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Table is not null && command.CommandText.Contains($"FROM \"{Table}\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref BeforeLookup, null) is { } callback) await callback();
            return result;
        }
    }
}

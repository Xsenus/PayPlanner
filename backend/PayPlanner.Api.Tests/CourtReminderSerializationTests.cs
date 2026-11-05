using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class CourtNotificationChannelIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtReminder_SerializationRetriesStayBoundedAndAtomic(bool permanent)
    {
        await using var database = await SecondaryTemplate.CreateDatabaseAsync();
        var fault = new ReminderSerializationFault(permanent);
        await using var db = await ReminderContextAsync(database.ConnectionString, fault);
        var seed = await SeedReminderAsync(db);
        var domain = await ReminderDomainAsync(db);
        var publisher = new ReminderAdmissionRealtime();
        var service = ReminderService(db, publisher);
        fault.Armed = true;
        var created = -1;
        var error = await Record.ExceptionAsync(async () => created = await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
        if (permanent)
        {
            Assert.NotNull(error);
            Assert.True(FinancialConcurrencyGuard.IsSerializationFailure(error));
            Assert.Equal(3, fault.Attempts);
            Assert.Empty(await db.UserNotifications.ToArrayAsync());
            Assert.Empty(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(1, created);
            Assert.Equal(2, fault.Attempts);
            Assert.Single(await db.UserNotifications.ToArrayAsync());
            Assert.Single(await db.CaseCourtNotificationOutboxes.Where(row => row.Kind == "reminder").ToArrayAsync());
            Assert.Equal(0, await service.NotifyCaseProceduralDeadlineRemindersAsync([new(seed.Case, seed.Deadline, [seed.UserId])]));
            Assert.Equal(2, fault.Attempts);
        }
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(publisher.Delivered);
        Assert.Equal(domain, await ReminderDomainAsync(db));
    }

    private sealed class ReminderSerializationFault(bool permanent) : SaveChangesInterceptor
    {
        private bool trigger;
        public bool Armed { get; set; }
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            trigger = Armed && eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added && row.Entity.Kind == "reminder");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (trigger)
            {
                trigger = false;
                Attempts++;
                if (permanent || Attempts == 1) throw new PostgresException("Synthetic serialization rollback", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
            }
            return ValueTask.FromResult(result);
        }
    }
}

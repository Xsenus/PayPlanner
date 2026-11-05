using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtRecoveryCurrentStateCases() =>
        from kind in new[] { "event", "deadline" }
        from mode in new[] { "title", "date", "status", "newer-same", "newer-reverted", "metadata", "document", "other-source" }
        from postgres in new[] { false, true }
        select new object[] { kind, mode, postgres };

    [Theory]
    [MemberData(nameof(CourtRecoveryCurrentStateCases))]
    public async Task CourtRecovery_SkipsStaleAcceptedStateAndKeepsIndependentJournal(string kind, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var publisher = new CourtRecoveryPublisher(db) { FailUser = true, FailJournal = true };
        var command = kind + "-create";
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User,
            notificationService: NewCourtRecoveryService(db, publisher), realtimePublisher: publisher), seed, command, default), command);
        var original = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync(row => row.EventKey == original.EventKey);
        Assert.Null(original.CompletedAt);
        Assert.Null(original.JournalCompletedAt);
        Assert.Null(original.NotificationCompletedAt);
        if (kind == "event")
        {
            var source = db.ClientCaseCourtEvents.Where(row => row.Id == original.SourceId);
            switch (mode)
            {
                case "title": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Новое название")); break;
                case "date": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.ScheduledAt, DateTime.UnixEpoch.AddDays(100))); break;
                case "status": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.MovementStatus, "cancelled")); break;
                case "metadata": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceCheckedAt, (DateTime?)DateTime.UnixEpoch.AddDays(100))); break;
                case "document": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, (int?)seed.Base.Base.OtherDocument.Id)); break;
            }
        }
        else
        {
            var source = db.ClientCaseProceduralDeadlines.Where(row => row.Id == original.SourceId);
            switch (mode)
            {
                case "title": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Новое название")); break;
                case "date": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.DueAt, DateTime.UnixEpoch.AddDays(100))); break;
                case "status": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "closed")); break;
                case "metadata": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceCheckedAt, (DateTime?)DateTime.UnixEpoch.AddDays(100))); break;
                case "document": await source.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, (int?)seed.Base.Base.OtherDocument.Id)); break;
            }
        }
        long? newerId = null;
        if (mode is "newer-same" or "newer-reverted" or "other-source")
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var clientCase = await db.ClientCases.SingleAsync(row => row.Id == original.ClientCaseId);
            var eventId = mode == "other-source" ? seed.SecondEvent.Id : original.SourceId;
            var deadlineId = mode == "other-source" ? seed.SecondDeadline.Id : original.SourceId;
            var courtEvent = kind == "event" ? await db.ClientCaseCourtEvents.SingleAsync(row => row.Id == eventId) : null;
            var deadline = kind == "deadline" ? await db.ClientCaseProceduralDeadlines.SingleAsync(row => row.Id == deadlineId) : null;
            var title = courtEvent?.Title ?? deadline!.Title;
            if (mode == "newer-reverted")
            {
                if (courtEvent is not null) courtEvent.Title = "Временное состояние";
                else deadline!.Title = "Временное состояние";
                await db.SaveChangesAsync();
            }
            var ids = await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(clientCase, seed.Base.Base.User.Id,
                "Сотрудник", [new(kind, "updated", courtEvent, deadline, false)], default);
            newerId = Assert.Single(ids);
            Assert.True(newerId > original.Id);
            if (mode == "newer-reverted")
            {
                if (courtEvent is not null) courtEvent.Title = title;
                else deadline!.Title = title;
                await db.SaveChangesAsync();
            }
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var before = await CourtGraphSnapshotAsync(db);
        var calls = publisher.UserCalls;
        var journalCalls = publisher.JournalCalls;
        publisher.FailUser = publisher.FailJournal = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == original.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, publisher), publisher).ProcessAsync(original.Id, default));
        var complete = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == original.Id);
        Assert.NotNull(complete.CompletedAt);
        Assert.NotNull(complete.NotificationCompletedAt);
        Assert.NotNull(complete.JournalCompletedAt);
        Assert.Equal(journalCalls + 1, publisher.JournalCalls);
        var stale = mode is "title" or "date" or "status" or "newer-same" or "newer-reverted";
        Assert.Equal(calls + (stale ? 0 : 1), publisher.UserCalls);
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync(row => row.EventKey == original.EventKey)).Id);
        if (mode == "newer-same")
        {
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, publisher), publisher).ProcessAsync(newerId!.Value, default));
            var delivered = Assert.Single(publisher.Notifications);
            Assert.NotEqual(original.EventKey, delivered.EventKey);
            Assert.Equal(2, await db.UserNotifications.CountAsync());
        }
    }
}

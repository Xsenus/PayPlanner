using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtDateMarkerValidationCases() =>
        from decision in new[] { false, true }
        from mode in new[] { "valid", "wrong-date", "unchanged", "not-suppressed", "action", "missing", "mixed" }
        from postgres in new[] { false, true }
        select new object[] { decision, mode, postgres };

    [Theory]
    [MemberData(nameof(CourtDateMarkerValidationCases))]
    public async Task CourtDateVersion_TechnicalMarkersValidateBeforePersistenceAndNeverNotify(bool decision, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var date = (decision ? seed.Case.CourtDecisionAt : seed.Case.NextHearingAt)!.Value;
        var kind = decision ? "decision-marker" : "hearing-marker";
        var valid = new CaseCourtNotificationRequest(kind, "updated", null, null, false, new(date.AddDays(-1), date, true));
        var request = mode switch
        {
            "wrong-date" => valid with { DateChange = new(date.AddDays(-1), date.AddDays(1), true) },
            "unchanged" => valid with { DateChange = new(date, date, true) },
            "not-suppressed" => valid with { DateChange = new(date.AddDays(-1), date, false) },
            "action" => valid with { Action = "created" },
            "missing" => valid with { DateChange = null },
            _ => valid
        };
        IReadOnlyCollection<CaseCourtNotificationRequest> requests = mode == "mixed" ? [valid, valid with { Action = "created" }] : [request];
        var before = await CaseDateDomainAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var queue = new CaseCourtNotificationQueue(db);
            if (mode == "valid")
            {
                Assert.Single(await queue.EnqueueBatchAsync(seed.Case, seed.Editor.Id, seed.Editor.FullName, requests, default));
                await transaction.CommitAsync();
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueBatchAsync(seed.Case, seed.Editor.Id, seed.Editor.FullName, requests, default));
                Assert.Empty(await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync());
                Assert.DoesNotContain(db.ChangeTracker.Entries<CaseCourtNotificationOutbox>(), row => row.State == EntityState.Added);
                await transaction.RollbackAsync();
            }
        }
        db.ChangeTracker.Clear();
        if (mode == "valid")
        {
            var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
            var publisher = new CaseDatePublisher(db) { FailUser = true, FailJournal = true };
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, new ForbiddenDateMarkerNotification(), publisher).ProcessAsync(intent.Id, default));
            Assert.NotNull((await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync()).CompletedAt);
            Assert.Empty(await db.UserNotifications.ToArrayAsync());
            Assert.Empty(await db.NotificationDeliveries.ToArrayAsync());
        }
        Assert.Equal(before, await CaseDateDomainAsync(db));
    }

    public static IEnumerable<object[]> CourtDateMarkerScopeCases() =>
        from decision in new[] { false, true }
        from mode in new[] { "same-source", "other-date", "other-case", "older-marker" }
        from postgres in new[] { false, true }
        select new object[] { decision, mode, postgres };

    [Theory]
    [MemberData(nameof(CourtDateMarkerScopeCases))]
    public async Task CourtDateVersion_OnlyNewerMatchingCaseAndDateMarkerSupersedes(bool decision, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var date = (decision ? seed.Case.CourtDecisionAt : seed.Case.NextHearingAt)!.Value;
        async Task Marker()
        {
            var source = await db.ClientCases.SingleAsync(row => row.Id == seed.Case.Id);
            var markerDecision = mode == "other-date" ? !decision : decision;
            if (mode == "other-case")
            {
                source = CaseDateEdit(source, source.NextHearingAt, source.CourtDecisionAt);
                db.ClientCases.Add(source); await db.SaveChangesAsync();
            }
            var current = markerDecision ? source.CourtDecisionAt : source.NextHearingAt;
            var request = new CaseCourtNotificationRequest(markerDecision ? "decision-marker" : "hearing-marker", "updated", null, null,
                false, new(current?.AddDays(-1) ?? date.AddDays(-1), current, true));
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Single(await new CaseCourtNotificationQueue(db).EnqueueBatchAsync(source, seed.Editor.Id, "Сотрудник", [request], default));
            await transaction.CommitAsync(); db.ChangeTracker.Clear();
        }
        if (mode == "older-marker") await Marker();
        var id = await QueueCaseDateAsync(db, seed.Case, decision, date.AddDays(-1), seed.Editor.Id);
        var publisher = new CaseDatePublisher(db) { FailUser = true };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(id, default));
        var inboxId = (await db.UserNotifications.AsNoTracking().SingleAsync()).Id;
        if (mode != "older-marker") await Marker();
        db.ChangeTracker.Clear();
        var state = await CaseDateDomainAsync(db);
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(id, default));
        Assert.Equal(mode == "same-source" ? 0 : 1, publisher.Delivered.Count);
        Assert.Equal(inboxId, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(state, await CaseDateDomainAsync(db));
    }

    private sealed class ForbiddenDateMarkerNotification : IDurableCourtNotificationService
    {
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCourtEventForEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent,
            string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
            => throw new InvalidOperationException("Technical marker must not call a transport");
        public Task<IReadOnlyList<NotificationDelivery>> NotifyProceduralDeadlineForEventAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline,
            string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipients, CaseCourtDeliveryClaim claim, CancellationToken ct)
            => throw new InvalidOperationException("Technical marker must not call a transport");
        public Task<IReadOnlyList<NotificationDelivery>> NotifyCaseDateForEventAsync(ClientCase clientCase, CaseCourtDateChange change, string actorName,
            int actorUserId, IReadOnlyCollection<int> recipients, DateTime changedAtUtc, CaseCourtDeliveryClaim claim, CancellationToken ct)
            => throw new InvalidOperationException("Technical marker must not call a transport");
    }
}

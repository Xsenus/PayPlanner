using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtRecoveryCommands() =>
        from command in new[] { "event-create", "event-update", "deadline-create", "deadline-update", "deadline-close", "hearing" }
        from failure in command == "hearing" ? new[] { "none", "journal" } : new[] { "none", "user", "journal" }
        from postgres in new[] { false, true }
        select new object[] { command, failure, postgres };

    [Theory]
    [MemberData(nameof(CourtRecoveryCommands))]
    public async Task CourtRecovery_AcceptedCommandRecoversEachPhaseWithoutRepeatingCompletedWork(string command, string failure, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var foreign = await CourtGraphForeignSnapshotAsync(db, seed);
        var publisher = new CourtRecoveryPublisher(db) { FailUser = failure == "user", FailJournal = failure == "journal" };
        var service = NewCourtRecoveryService(db, publisher);
        var result = await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User, notificationService: service, realtimePublisher: publisher), seed, command, default);
        AssertCourtGraphSuccess(result, command);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Contains(db.ChangeTracker.Entries<ClientCase>(), entry => entry.Entity.Id == seed.Base.Base.Cases[0]);
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var originalPayload = intent.PayloadJson;
        var originalKey = intent.EventKey;
        var notificationId = await db.UserNotifications.AsNoTracking().Where(row => row.EventKey == originalKey).Select(row => (int?)row.Id).SingleOrDefaultAsync();
        var journalCalls = publisher.JournalCalls;
        var userCalls = publisher.UserCalls;
        if (failure == "none") Assert.NotNull(intent.CompletedAt);
        else
        {
            Assert.Null(intent.CompletedAt);
            if (failure == "user") Assert.NotNull(intent.JournalCompletedAt);
            else Assert.NotNull(intent.NotificationCompletedAt);
            publisher.FailUser = false;
            publisher.FailJournal = false;
            await db.CaseCourtNotificationOutboxes.Where(row => row.Id == intent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
            var restarted = NewCourtRecoveryService(db, publisher);
            Assert.True(await new CaseCourtNotificationDispatchProcessor(db, restarted, publisher).ProcessAsync(intent.Id, default));
            if (failure == "user") Assert.Equal(journalCalls, publisher.JournalCalls);
            else Assert.Equal(userCalls, publisher.UserCalls);
        }
        var finished = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.NotNull(finished.CompletedAt);
        Assert.Null(finished.LeaseToken);
        Assert.Null(finished.LeaseUntil);
        Assert.Equal(originalPayload, finished.PayloadJson);
        Assert.Equal(originalKey, finished.EventKey);
        if (command != "hearing")
        {
            var notification = await db.UserNotifications.AsNoTracking().SingleAsync(row => row.EventKey == originalKey);
            Assert.Equal(notificationId, notification.Id);
            Assert.Single(publisher.Notifications);
        }
        else Assert.Empty(publisher.Notifications);
        journalCalls = publisher.JournalCalls;
        userCalls = publisher.UserCalls;
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(intent.Id, default));
        Assert.Equal(journalCalls, publisher.JournalCalls);
        Assert.Equal(userCalls, publisher.UserCalls);
        Assert.Equal(foreign, await CourtGraphForeignSnapshotAsync(db, seed));
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.AsNoTracking().Where(row => row.Id == seed.DoneTaskId).Select(row => row.Result).SingleAsync());
    }

    public static IEnumerable<object[]> CourtRecoveryAccessCases() =>
        from mode in new[] { "inactive", "unapproved", "permission", "hidden", "source-case", "read", "archived", "deleted", "inapp-disabled", "type-disabled" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(CourtRecoveryAccessCases))]
    public async Task CourtRecovery_ReplayChecksCurrentRecipientAndPreservesSuppressedInbox(string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var publisher = new CourtRecoveryPublisher(db) { FailUser = true };
        var service = NewCourtRecoveryService(db, publisher);
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User, notificationService: service, realtimePublisher: publisher), seed, "event-create", default), "event-create");
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        var notification = await db.UserNotifications.AsNoTracking().SingleAsync(row => row.EventKey == intent.EventKey);
        var recipient = await db.Users.AsNoTracking().SingleAsync(row => row.Id == notification.RecipientUserId);
        switch (mode)
        {
            case "inapp-disabled":
            case "type-disabled":
                db.UserSettings.Add(new()
                {
                    UserId = recipient.Id,
                    Key = UserNotificationPreferenceSettings.SettingKey,
                    Value = UserNotificationPreferenceSettings.Serialize(mode == "type-disabled"
                        ? new UserNotificationPreferenceSnapshot([notification.Type], new Dictionary<string, IReadOnlyList<string>>())
                        : new UserNotificationPreferenceSnapshot([], new Dictionary<string, IReadOnlyList<string>> { [notification.Type] = [NotificationChannels.InApp] }))
                });
                await db.SaveChangesAsync();
                break;
            case "inactive": await db.Users.Where(row => row.Id == recipient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false)); break;
            case "unapproved": await db.Users.Where(row => row.Id == recipient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false)); break;
            case "permission": await db.RolePermissions.Where(row => row.RoleId == recipient.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false)); break;
            case "hidden": await db.ClientCases.Where(row => row.Id == intent.ClientCaseId).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); break;
            case "source-case": await db.ClientCaseCourtEvents.Where(row => row.Id == intent.SourceId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Base.Cases[1])); break;
            case "read": await db.UserNotifications.Where(row => row.Id == notification.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsRead, true)); break;
            case "archived": await db.UserNotifications.Where(row => row.Id == notification.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, true)); break;
            case "deleted": await db.UserNotifications.Where(row => row.Id == notification.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DeletedAt, (DateTime?)DateTime.UnixEpoch)); break;
        }
        var before = await CourtGraphSnapshotAsync(db);
        var calls = publisher.UserCalls;
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == intent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, publisher), publisher).ProcessAsync(intent.Id, default));
        Assert.Equal(calls, publisher.UserCalls);
        Assert.Empty(publisher.Notifications);
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    public static IEnumerable<object[]> CourtRecoveryRollbackCases() =>
        from command in new[] { "event-create", "event-update", "deadline-create", "deadline-update", "deadline-close", "hearing" }
        from postgres in new[] { false, true }
        from cancel in new[] { false, true }
        select new object[] { command, postgres, cancel };

    [Theory]
    [MemberData(nameof(CourtRecoveryRollbackCases))]
    public async Task CourtRecovery_FailureAfterIntentInsertRollsBackIntentAndWholeCommand(string command, bool postgres, bool cancel)
    {
        var fault = new CourtRecoveryOutboxFault(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var seed = await SeedCourtRecoveryAsync(db);
        var before = await CourtGraphSnapshotAsync(db);
        var publisher = new CourtRecoveryPublisher(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        var controller = CreateController(db, seed.Base.Base.User, notificationService: NewCourtRecoveryService(db, publisher), realtimePublisher: publisher);
        Task Write() => InvokeCourtGraphAsync(controller, seed, command, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtRecoveryInjectedFailure>(Write);
        Assert.True(fault.Called);
        Assert.Equal(0, publisher.UserCalls);
        Assert.Equal(0, publisher.JournalCalls);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    private static async Task<CourtGraphSeed> SeedCourtRecoveryAsync(PaymentContext db)
    {
        var seed = await SeedCourtGraphAsync(db);
        var recipientId = await db.ClientCases.Where(row => row.Id == seed.Base.Base.Cases[0]).Select(row => row.ResponsibleUserId).SingleAsync();
        var role = new Role { Name = "court-recovery-recipient-" + Guid.NewGuid().ToString("N"), Description = "Получатель судебных уведомлений" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Cases, CanView = true, ViewScope = DataVisibilityScope.Own });
        await db.Users.Where(row => row.Id == recipientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, role.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return seed;
    }

    private static NotificationService NewCourtRecoveryService(PaymentContext db, IRealtimeNotificationPublisher publisher, IWebPushNotificationService? push = null)
        => NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher, push);

    private sealed class CourtRecoveryInjectedFailure : Exception;

    private sealed class CourtRecoveryPublisher(PaymentContext db) : IRealtimeNotificationPublisher, IReliableRealtimeNotificationPublisher, IReliableTaskJournalChangePublisher
    {
        public bool FailUser { get; set; }
        public bool FailJournal { get; set; }
        public int UserCalls { get; private set; }
        public int JournalCalls { get; private set; }
        public List<UserNotification> Notifications { get; } = [];
        public Func<Task>? BeforeUser { get; set; }

        public async Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            UserCalls++;
            if (BeforeUser is not null) await BeforeUser();
            if (FailUser) throw new CourtRecoveryInjectedFailure();
            Notifications.Add(notification);
        }

        public Task PublishUserNotificationOrThrowAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => PublishUserNotificationAsync(notification, toastEnabled, cancellationToken);

        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            JournalCalls++;
            if (FailJournal) throw new CourtRecoveryInjectedFailure();
            return Task.CompletedTask;
        }

        public Task PublishTaskJournalChangedOrThrowAsync(CancellationToken cancellationToken = default) => PublishTaskJournalChangedAsync(cancellationToken);
    }

    private sealed class CourtRecoveryOutboxFault(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action? Cancel { get; set; }
        private bool trigger;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            trigger = Armed && eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(entry => entry.State == EntityState.Added);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (trigger)
            {
                trigger = false;
                Called = true;
                if (cancel) { Cancel!(); throw new OperationCanceledException(cancellationToken); }
                throw new CourtRecoveryInjectedFailure();
            }
            return ValueTask.FromResult(result);
        }
    }
}

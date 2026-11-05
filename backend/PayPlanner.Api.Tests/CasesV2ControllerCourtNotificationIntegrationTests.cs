using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtNoticeSyncCases() =>
        from mode in new[] { "success", "user", "journal", "file" }
        from postgres in new[] { false, true }
        select new object[] { mode, postgres };

    [Theory]
    [MemberData(nameof(CourtNoticeSyncCases))]
    public async Task CourtNoticeIntegration_RunCourtSyncRecoversDeliveryWithoutRewritingAcceptedGraph(string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRecoveryAsync(db);
        var caseId = seed.Base.Base.Cases[0];
        await db.ClientCases.Where(row => row.Id == caseId).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.CourtUrl, "https://court.test/search").SetProperty(row => row.CourtCaseNumber, "42/2026"));
        db.ChangeTracker.Clear();
        var foreign = await CourtGraphForeignSnapshotAsync(db, seed);
        var documentIds = await db.ClientCaseDocuments.Select(row => row.Id).ToArrayAsync();
        var realtime = new CourtNoticeIntegrationRealtime(db) { FailCourtUser = mode == "user", FailJournal = mode == "journal" };
        var storage = new CourtCoreStorage { Db = db, Throw = mode == "file" };
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        var service = NewCourtRecoveryService(db, realtime);
        var controller = CourtCoreController(db, seed.Base.Base.User, provider, storage, service, realtime);
        if (mode == "file") await Assert.ThrowsAsync<CourtCoreFileFault>(() => controller.RunCourtSync(caseId, null, default));
        else Assert.IsType<OkObjectResult>((await controller.RunCourtSync(caseId, null, default)).Result);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
        var intents = await db.CaseCourtNotificationOutboxes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Contains(intents, row => row.Kind == "event");
        Assert.Contains(intents, row => row.Kind == "deadline");
        Assert.Contains(intents, row => row.Kind == "journal");
        Assert.Equal(mode == "file" ? 1 : 2, intents.Count(row => row.Kind == "journal"));
        var noticeIds = intents.Where(row => row.Kind != "journal").Select(row => row.Id).ToArray();
        if (mode is "user" or "journal") Assert.Contains(intents, row => row.CompletedAt == null);
        else Assert.All(intents, row => Assert.NotNull(row.CompletedAt));
        var beforeRecovery = await CourtCoreStateAsync(db);
        var inboxIds = await db.UserNotifications.AsNoTracking().Where(row => row.EventKey.StartsWith("case-court:"))
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync();
        Assert.NotEmpty(inboxIds);
        realtime.FailCourtUser = realtime.FailJournal = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var processor = new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, realtime), realtime);
        await processor.ProcessBatchAsync(default);
        Assert.All(await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync(), row => Assert.NotNull(row.CompletedAt));
        Assert.Equal(beforeRecovery, await CourtCoreStateAsync(db));
        Assert.Equal(inboxIds, await db.UserNotifications.AsNoTracking().Where(row => row.EventKey.StartsWith("case-court:"))
            .OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
        foreach (var group in realtime.Delivered.Where(row => row.EventKey.StartsWith("case-court:")).GroupBy(row => row.EventKey))
            Assert.Single(group);
        Assert.Equal(intents.Where(row => row.Kind != "journal").Select(row => row.EventKey).Order(),
            realtime.Delivered.Where(row => row.EventKey.StartsWith("case-court:")).Select(row => row.EventKey).Order());
        if (mode == "file")
        {
            storage.Throw = false;
            Assert.IsType<OkObjectResult>((await CourtCoreController(db, seed.Base.Base.User, provider, storage,
                NewCourtRecoveryService(db, realtime), realtime).RunCourtSync(caseId, null, default)).Result);
        }
        var created = await db.ClientCaseDocuments.AsNoTracking().Where(row => !documentIds.Contains(row.Id)).ToArrayAsync();
        var summary = Assert.Single(created);
        Assert.True(storage.Exists(summary.StoragePath));
        Assert.Contains(await db.ClientCaseProceduralDeadlines.AsNoTracking().Where(row => row.ClientCaseId == caseId).ToArrayAsync(),
            row => row.ClientCaseDocumentId == summary.Id);
        var saves = storage.Saves;
        var deliveries = realtime.Delivered.Count(row => row.EventKey.StartsWith("case-court:"));
        Assert.IsType<OkObjectResult>((await CourtCoreController(db, seed.Base.Base.User, provider, storage,
            NewCourtRecoveryService(db, realtime), realtime).RunCourtSync(caseId, null, default)).Result);
        Assert.Equal(saves, storage.Saves);
        Assert.Equal(deliveries, realtime.Delivered.Count(row => row.EventKey.StartsWith("case-court:")));
        Assert.Equal(noticeIds, await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind != "journal").OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
        Assert.Equal(foreign, await CourtGraphForeignSnapshotAsync(db, seed));
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.AsNoTracking().Where(row => row.Id == seed.DoneTaskId).Select(row => row.Result).SingleAsync());
    }

    public static IEnumerable<object[]> CourtNoticeComplaintCases() =>
        from command in new[] { "create", "update", "archive" }
        from postgres in new[] { false, true }
        select new object[] { command, postgres };

    [Theory]
    [MemberData(nameof(CourtNoticeComplaintCases))]
    public async Task CourtNoticeIntegration_ComplaintRecoversJournalWithoutChangingSavedBusinessState(string command, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtWriteAsync(db, command);
        var realtime = new CourtNoticeIntegrationRealtime(db) { FailJournal = true };
        var result = await InvokeCourtWriteAsync(CreateController(db, seed.Base.User,
            notificationService: NewCourtRecoveryService(db, realtime), realtimePublisher: realtime), seed, command, default);
        if (command == "create") Assert.IsType<CreatedResult>(result);
        else if (command == "archive") Assert.IsType<NoContentResult>(result);
        else Assert.IsType<OkObjectResult>(result);
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync();
        Assert.Equal("journal", intent.Kind);
        Assert.Null(intent.JournalCompletedAt);
        Assert.Null(intent.CompletedAt);
        Assert.NotNull(intent.NotificationCompletedAt);
        var accepted = await CourtWriteSnapshotAsync(db);
        realtime.FailJournal = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == intent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var processor = new CaseCourtNotificationDispatchProcessor(db, NewCourtRecoveryService(db, realtime), realtime);
        Assert.True(await processor.ProcessAsync(intent.Id, default));
        Assert.Equal(accepted, await CourtWriteSnapshotAsync(db));
        Assert.Empty(realtime.Delivered);
        Assert.False(await processor.ProcessAsync(intent.Id, default));
        Assert.Equal(2, realtime.JournalCalls);
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
    }

    public static IEnumerable<object[]> CourtNoticeIntegrationRollbackCases() =>
        from command in new[] { "sync", "create", "update", "archive" }
        from postgres in new[] { false, true }
        from cancel in new[] { false, true }
        select new object[] { command, postgres, cancel };

    [Theory]
    [MemberData(nameof(CourtNoticeIntegrationRollbackCases))]
    public async Task CourtNoticeIntegration_QueueFailureRollsBackAcceptedSyncOrComplaint(string command, bool postgres, bool cancel)
    {
        var fault = new CourtRecoveryOutboxFault(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var storage = new CourtCoreStorage();
        var service = NewCourtRecoveryService(db, realtime);
        using var cancellation = new CancellationTokenSource();
        Task Write;
        string before;
        if (command == "sync")
        {
            var (user, clientCase) = await SeedCourtSearchAsync(db);
            before = await CourtCoreStateAsync(db);
            fault.Armed = true; fault.Cancel = cancellation.Cancel;
            Write = CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, service, realtime)
                .RunCourtSync(clientCase.Id, null, cancellation.Token);
        }
        else
        {
            var seed = await SeedCourtWriteAsync(db, command);
            before = await CourtWriteSnapshotAsync(db);
            fault.Armed = true; fault.Cancel = cancellation.Cancel;
            Write = InvokeCourtWriteAsync(CreateController(db, seed.Base.User, notificationService: service, realtimePublisher: realtime), seed, command, cancellation.Token);
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Write);
        else await Assert.ThrowsAsync<CourtRecoveryInjectedFailure>(() => Write);
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Equal(before, command == "sync" ? await CourtCoreStateAsync(db) : await CourtWriteSnapshotAsync(db));
        Assert.Empty(realtime.Delivered);
        Assert.Equal(0, realtime.JournalCalls);
        Assert.Equal(0, storage.Saves);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtNoticeIntegration_FinalJournalInsertFailureRollsBackSuccessAndRetainsPublishedSummary(bool postgres, bool cancel)
    {
        var fault = new CourtRecoveryOutboxFault(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var realtime = new CourtNoticeIntegrationRealtime(db);
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        var storage = new CourtPublicationStorage { AfterSave = () => { fault.Armed = true; return Task.CompletedTask; } };
        Task Write() => CourtCoreController(db, user, provider, storage, NewCourtRecoveryService(db, realtime), realtime)
            .RunCourtSync(clientCase.Id, null, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtRecoveryInjectedFailure>(Write);
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Null((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        var document = await db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.True(storage.HasFile);
        Assert.NotEmpty(await db.TaskJournalItems.ToArrayAsync());
        Assert.All(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync(), row => Assert.Null(row.ClientCaseDocumentId));
        Assert.Single(await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind == "journal").ToArrayAsync());
        var tasks = await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Result }).ToArrayAsync();
        fault.Armed = false;
        storage.AfterSave = null;
        Assert.IsType<OkObjectResult>((await CourtCoreController(db, user, provider, storage,
            NewCourtRecoveryService(db, realtime), realtime).RunCourtSync(clientCase.Id, null, default)).Result);
        Assert.NotNull((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Equal(document.Id, (await db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.All(await db.ClientCaseProceduralDeadlines.AsNoTracking().ToArrayAsync(), row => Assert.Equal(document.Id, row.ClientCaseDocumentId));
        Assert.Equal(1, storage.Saves);
        Assert.Equal(tasks, await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Result }).ToArrayAsync());
        Assert.All(await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync(), row => Assert.NotNull(row.CompletedAt));
    }

}

internal sealed class CourtNoticeIntegrationRealtime(PaymentContext db) : IRealtimeNotificationPublisher,
    IReliableRealtimeNotificationPublisher, IReliableTaskJournalChangePublisher
{
    public bool FailCourtUser { get; set; }
    public bool FailJournal { get; set; }
    public int? FailRecipient { get; set; }
    public int JournalCalls { get; private set; }
    public List<UserNotification> Delivered { get; } = [];
    public Func<UserNotification, Task>? BeforeUser { get; set; }

    public async Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
    {
        Assert.Null(db.Database.CurrentTransaction);
        if (BeforeUser is not null) await BeforeUser(notification);
        if (notification.EventKey.StartsWith("case-court:", StringComparison.Ordinal)
            && (FailCourtUser || FailRecipient == notification.RecipientUserId)) throw new IOException("Synthetic court channel failure");
        Delivered.Add(notification);
    }
    public Task PublishUserNotificationOrThrowAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        => PublishUserNotificationAsync(notification, toastEnabled, cancellationToken);
    public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
    {
        Assert.Null(db.Database.CurrentTransaction);
        JournalCalls++;
        if (FailJournal) throw new IOException("Synthetic journal failure");
        return Task.CompletedTask;
    }
    public Task PublishTaskJournalChangedOrThrowAsync(CancellationToken cancellationToken = default) => PublishTaskJournalChangedAsync(cancellationToken);
}

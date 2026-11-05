using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using PayPlanner.Api.Data;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services.Cases;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CaseDateAcceptedCases() =>
        from create in new[] { false, true }
        from decision in new[] { false, true }
        from postgres in new[] { false, true }
        from legacy in new[] { false, true }
        select new object[] { create, decision, postgres, legacy };

    [Theory]
    [MemberData(nameof(CaseDateAcceptedCases))]
    public async Task CaseDateNotice_AcceptedSaveRecoversWithoutRepeatingDateOrTaskMutation(bool create, bool decision, bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var editor = await CreateUserAsync(db, "case-date-editor@synthetic.local", "Редактор", DataVisibilityScope.All);
        var responsible = await CreateUserAsync(db, "case-date-recipient@synthetic.local", "Ответственный", DataVisibilityScope.All);
        var client = new Client { Name = "Синтетический клиент", IsActive = true, ResponsibleUserId = editor.Id };
        db.Clients.Add(client); await db.SaveChangesAsync();
        var initial = DateTime.UtcNow.AddDays(5).Date;
        var changed = initial.AddDays(2);
        ClientCase Input(DateTime date) => new()
        {
            ClientId = client.Id,
            Title = "Судебное дело дат",
            Stage = "Суд",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = responsible.Id,
            NextHearingAt = decision ? null : date,
            CourtDecisionAt = decision ? date : null
        };
        var existing = create ? null : Input(initial);
        if (existing is not null) { db.ClientCases.Add(existing); await db.SaveChangesAsync(); }
        db.ChangeTracker.Clear();
        var publisher = new CaseDatePublisher(db) { FailUser = true };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        var requested = Input(changed);
        var result = await SaveCaseDateAsync(db, editor, service, publisher, legacy, create, existing?.Id ?? 0, requested, default);
        if (create) Assert.IsType<CreatedResult>(result); else Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.Database.CurrentTransaction);
        var saved = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(changed, decision ? saved.CourtDecisionAt : saved.NextHearingAt);
        var intent = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind != "journal");
        Assert.Null(intent.CompletedAt);
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(responsible.Id, inbox.RecipientUserId);
        var state = await CaseDateDomainAsync(db);
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(intent.Id, default));
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Single(publisher.Delivered);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(intent.Id, default));
        Assert.Equal(state, await CaseDateDomainAsync(db));
    }

    private static async Task<string> CaseDateDomainAsync(PaymentContext db) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Cases = await db.ClientCases.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Events = await db.ClientCaseCourtEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Deadlines = await db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
    });

    public static IEnumerable<object[]> CaseDateCurrentCases() =>
        from decision in new[] { false, true }
        from mode in new[] { "active", "date", "newer-reverted", "clear", "pretrial", "permission", "personal" }
        from postgres in new[] { false, true }
        select new object[] { decision, mode, postgres };

    [Theory]
    [MemberData(nameof(CaseDateCurrentCases))]
    public async Task CaseDateNotice_RecoveryChecksLatestDateAndCurrentRecipient(bool decision, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var date = decision ? seed.Case.CourtDecisionAt!.Value : seed.Case.NextHearingAt!.Value;
        var id = await QueueCaseDateAsync(db, seed.Case, decision, date.AddDays(-1), seed.Editor.Id);
        var publisher = new CaseDatePublisher(db) { FailUser = true };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(id, default));
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        if (mode is "date" or "clear") await SetCaseDateAsync(db, seed.Case.Id, decision, mode == "clear" ? null : date.AddDays(9));
        if (mode == "newer-reverted")
        {
            await SetCaseDateAsync(db, seed.Case.Id, decision, date.AddDays(9));
            await QueueCaseDateAsync(db, await db.ClientCases.SingleAsync(), decision, date, seed.Editor.Id);
            await SetCaseDateAsync(db, seed.Case.Id, decision, date);
            await QueueCaseDateAsync(db, await db.ClientCases.SingleAsync(), decision, date.AddDays(9), seed.Editor.Id);
        }
        if (mode == "pretrial") await db.ClientCases.Where(row => row.Id == seed.Case.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Stage, "Досудебная работа"));
        if (mode == "permission") Assert.Equal(1, await db.RolePermissions.Where(row => row.RoleId == seed.Recipient.RoleId && row.Section == RolePermissionCatalog.Cases)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false)));
        if (mode == "personal")
        {
            db.UserSettings.Add(new()
            {
                UserId = seed.Recipient.Id,
                Key = UserNotificationPreferenceSettings.SettingKey,
                Value = UserNotificationPreferenceSettings.Serialize(new UserNotificationPreferenceSnapshot([inbox.Type], new Dictionary<string, IReadOnlyList<string>>()))
            });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        var domain = await CaseDateDomainAsync(db);
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(id, default));
        Assert.Equal(mode == "active" ? 1 : 0, publisher.Delivered.Count);
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(domain, await CaseDateDomainAsync(db));
    }

    public static IEnumerable<object[]> CaseDateAtomicCases() =>
        from create in new[] { false, true }
        from cancel in new[] { false, true }
        from postgres in new[] { false, true }
        from legacy in new[] { false, true }
        select new object[] { create, cancel, postgres, legacy };

    [Theory]
    [MemberData(nameof(CaseDateAtomicCases))]
    public async Task CaseDateNotice_IntentInsertFailureRollsBackAcceptedDatesTasksAndHistory(bool create, bool cancel, bool postgres, bool legacy)
    {
        var fault = new CourtRecoveryOutboxFault(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString, fault) : await CreateContextAsync(fault);
        var seed = await SeedCaseDateAsync(db, false);
        var before = await CaseDateDomainAsync(db);
        var publisher = new CaseDatePublisher(db);
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        using var cancellation = new CancellationTokenSource();
        fault.Armed = true; fault.Cancel = cancellation.Cancel;
        var requested = CaseDateEdit(seed.Case, seed.Case.NextHearingAt!.Value.AddDays(2), DateTime.UtcNow.Date);
        Task Write() => SaveCaseDateAsync(db, seed.Editor, service, publisher, legacy, create, seed.Case.Id, requested, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtRecoveryInjectedFailure>(Write);
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear();
        Assert.Equal(before, await CaseDateDomainAsync(db));
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(await db.NotificationDeliveries.ToArrayAsync());
        Assert.Empty(publisher.Delivered);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CaseDateNotice_ClaimFailureAfterCommitKeepsSuccessfulResponseAndDurableWork(bool create, bool postgres, bool legacy)
    {
        var fault = new CaseDateClaimFailure();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString, fault) : await CreateContextAsync(fault);
        var seed = await SeedCaseDateAsync(db, false);
        var publisher = new CaseDatePublisher(db);
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        var request = CaseDateEdit(seed.Case, seed.Case.NextHearingAt!.Value.AddDays(2), null);
        var result = await SaveCaseDateAsync(db, seed.Editor, service, publisher, legacy, create, seed.Case.Id, request, default);
        if (create) Assert.IsType<CreatedResult>(result); else Assert.IsType<OkObjectResult>(result);
        Assert.True(fault.Called);
        var date = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Kind == "hearing-date");
        Assert.Null(date.CompletedAt);
        Assert.Empty(publisher.Delivered);
        var state = await CaseDateDomainAsync(db);
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(date.Id, default));
        Assert.Single(publisher.Delivered);
        Assert.Equal(state, await CaseDateDomainAsync(db));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CaseDateNotice_JournalRecoveryDoesNotRepeatCompletedUserDelivery(bool decision, bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var publisher = new CaseDatePublisher(db) { FailJournal = true };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        var result = await SaveCaseDateAsync(db, seed.Editor, service, publisher, legacy, false, seed.Case.Id, CaseDateEdit(seed.Case,
            decision ? null : seed.Case.NextHearingAt!.Value.AddDays(2), decision ? seed.Case.CourtDecisionAt!.Value.AddDays(2) : null), default);
        Assert.IsType<OkObjectResult>(result);
        var inbox = await db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Single(publisher.Delivered);
        var domain = await CaseDateDomainAsync(db);
        publisher.FailJournal = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.Equal(1, await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessBatchAsync(default));
        Assert.Single(publisher.Delivered);
        Assert.Equal(inbox.Id, (await db.UserNotifications.AsNoTracking().SingleAsync()).Id);
        Assert.All(await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync(), row => Assert.NotNull(row.CompletedAt));
        Assert.Equal(domain, await CaseDateDomainAsync(db));
    }

    public static IEnumerable<object[]> CaseDateRuleCases() =>
        from decision in new[] { false, true }
        from mode in new[] { "clear", "pretrial", "same" }
        from postgres in new[] { false, true }
        from legacy in new[] { false, true }
        select new object[] { decision, mode, postgres, legacy };

    [Theory]
    [MemberData(nameof(CaseDateRuleCases))]
    public async Task CaseDateNotice_ClearPretrialAndUnchangedValuesPreserveExistingRules(bool decision, string mode, bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var current = decision ? seed.Case.CourtDecisionAt : seed.Case.NextHearingAt;
        var next = mode == "clear" ? null : mode == "same" ? current : current!.Value.AddDays(1);
        var request = CaseDateEdit(seed.Case, decision ? null : next, decision ? next : null);
        if (mode == "pretrial") request.Stage = "Досудебная работа";
        var publisher = new CaseDatePublisher(db);
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        Assert.IsType<OkObjectResult>(await SaveCaseDateAsync(db, seed.Editor, service, publisher, legacy, false, seed.Case.Id, request, default));
        var rows = await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync();
        Assert.Equal(mode == "same" ? 1 : 2, rows.Length);
        Assert.All(rows, row => Assert.NotNull(row.CompletedAt));
        Assert.Equal(mode == "clear" && decision ? 1 : 0, publisher.Delivered.Count);
        if (mode == "clear" && decision) Assert.Contains("Удалена дата", Assert.Single(publisher.Delivered).Title, StringComparison.Ordinal);
        Assert.Empty(await db.ClientCaseCourtEvents.ToArrayAsync());
        Assert.Empty(await db.ClientCaseProceduralDeadlines.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaseDateNotice_CreateWithBothDatesPersistsIndependentIntentsInOneBatch(bool postgres, bool legacy)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, false);
        var publisher = new CaseDatePublisher(db);
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        var request = CaseDateEdit(seed.Case, DateTime.UtcNow.AddDays(4), DateTime.UtcNow.Date);
        Assert.IsType<CreatedResult>(await SaveCaseDateAsync(db, seed.Editor, service, publisher, legacy, true, 0, request, default));
        var rows = await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync();
        Assert.Equal(new[] { "decision-date", "hearing-date", "journal" }, rows.Select(row => row.Kind).Order().ToArray());
        Assert.All(rows, row => Assert.NotNull(row.CompletedAt));
        Assert.Equal(new[] { UserNotificationTypes.CourtDecisionRecorded, UserNotificationTypes.CourtHearing }.Order(), publisher.Delivered.Select(row => row.Type).Order());
        Assert.All(publisher.Delivered, row => Assert.Equal(seed.Recipient.Id, row.RecipientUserId));
    }

    private static Task<IActionResult> SaveCaseDateAsync(PaymentContext db, User editor, INotificationService service,
        CaseDatePublisher publisher, bool legacy, bool create, int id, ClientCase request, CancellationToken ct)
    {
        var current = CreateController(db, editor, notificationService: service, realtimePublisher: publisher);
        if (!legacy) return create ? current.Create(request, ct) : current.Update(id, request, ct);
        var previous = new CasesController(db, publisher, service) { ControllerContext = current.ControllerContext };
        if (create) return previous.Create(request, ct);
        var typed = new CaseEditRequest
        {
            ClientId = request.ClientId,
            Title = request.Title,
            Stage = request.Stage,
            Status = request.Status,
            ResponsibleUserId = request.ResponsibleUserId,
            NextHearingAt = request.NextHearingAt,
            CourtDecisionAt = request.CourtDecisionAt
        };
        return previous.Update(id, typed, ct);
    }

    private sealed record CaseDateSeed(User Editor, User Recipient, ClientCase Case);
    private static async Task<CaseDateSeed> SeedCaseDateAsync(PaymentContext db, bool decision)
    {
        var editor = await CreateUserAsync(db, "case-date-state-editor@synthetic.local", "Редактор", DataVisibilityScope.All);
        var recipient = await CreateUserAsync(db, "case-date-state-recipient@synthetic.local", "Получатель", DataVisibilityScope.All);
        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Синтетический клиент дат", IsActive = true, ResponsibleUserId = editor.Id },
            Title = "Судебное дело дат",
            Stage = "Суд",
            Status = ClientCaseStatus.Open,
            ResponsibleUserId = recipient.Id,
            NextHearingAt = decision ? null : DateTime.UtcNow.AddDays(5).Date,
            CourtDecisionAt = decision ? DateTime.UtcNow.AddDays(5).Date : null
        };
        db.ClientCases.Add(clientCase); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return new(editor, recipient, clientCase);
    }
    private static ClientCase CaseDateEdit(ClientCase source, DateTime? hearing, DateTime? decision) => new()
    {
        ClientId = source.ClientId,
        Title = source.Title,
        Stage = source.Stage,
        Status = source.Status,
        ResponsibleUserId = source.ResponsibleUserId,
        NextHearingAt = hearing,
        CourtDecisionAt = decision
    };
    private static PostgreSqlPaymentContext CaseDatePostgres(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    private static async Task<long> QueueCaseDateAsync(PaymentContext db, ClientCase clientCase, bool decision, DateTime? previous, int actorId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var ids = await new CaseCourtNotificationQueue(db).EnqueueCaseEditAsync(clientCase, decision ? null : previous, decision ? previous : null, actorId, "Сотрудник", default);
        await transaction.CommitAsync(); db.ChangeTracker.Clear();
        return await db.CaseCourtNotificationOutboxes.Where(row => ids.Contains(row.Id) && row.Kind != "journal").Select(row => row.Id).SingleAsync();
    }
    private static async Task SetCaseDateAsync(PaymentContext db, int caseId, bool decision, DateTime? value)
    {
        var source = await db.ClientCases.SingleAsync(row => row.Id == caseId);
        if (decision) source.CourtDecisionAt = value; else source.NextHearingAt = value;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    }
    private sealed class CaseDateClaimFailure : DbCommandInterceptor
    {
        public bool Called { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("CaseCourtNotificationOutboxes", StringComparison.Ordinal) && command.CommandText.Contains("LeaseToken", StringComparison.Ordinal))
            {
                Called = true; throw new IOException("Synthetic claim failure after accepted case commit");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CaseDatePublisher(PaymentContext db) : IRealtimeNotificationPublisher, IReliableRealtimeNotificationPublisher, IReliableTaskJournalChangePublisher
    {
        public bool FailUser { get; set; }
        public bool FailJournal { get; set; }
        public List<UserNotification> Delivered { get; } = [];
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (FailUser) throw new IOException("Synthetic case date realtime failure");
            Delivered.Add(notification); return Task.CompletedTask;
        }
        public Task PublishUserNotificationOrThrowAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => PublishUserNotificationAsync(notification, toastEnabled, cancellationToken);
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (FailJournal) throw new IOException("Synthetic case date journal failure");
            return Task.CompletedTask;
        }
        public Task PublishTaskJournalChangedOrThrowAsync(CancellationToken cancellationToken = default) => PublishTaskJournalChangedAsync(cancellationToken);
    }
}

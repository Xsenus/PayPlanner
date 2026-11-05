using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtDateMarkerRollbackCases() =>
        from decision in new[] { false, true }
        from cancel in new[] { false, true }
        from postgres in new[] { false, true }
        select new object[] { decision, cancel, postgres };

    [Theory]
    [MemberData(nameof(CourtDateMarkerRollbackCases))]
    public async Task CourtDateVersion_MarkerInsertFailureRollsBackAcceptedDate(bool decision, bool cancel, bool postgres)
    {
        var fault = new CourtDateMarkerInsertFault(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString, fault) : await CreateContextAsync(fault);
        var seed = await SeedCaseDateAsync(db, decision);
        await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/search")
            .SetProperty(row => row.CourtCaseNumber, "42/2026"));
        db.ChangeTracker.Clear();
        var date = (decision ? seed.Case.CourtDecisionAt : seed.Case.NextHearingAt)!.Value;
        var before = await CourtCoreStateAsync(db);
        var publisher = new CourtNoticeIntegrationRealtime(db);
        var storage = new CourtCoreStorage();
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        Task Write() => CourtCoreController(db, seed.Editor, new CaseDateSyncProvider { Date = date.AddDays(2), Decision = decision },
            storage, NewCourtRecoveryService(db, publisher), publisher).RunCourtSync(seed.Case.Id, null, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtRecoveryInjectedFailure>(Write);
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Empty(await db.CaseCourtNotificationOutboxes.ToArrayAsync());
        Assert.Empty(await db.UserNotifications.ToArrayAsync());
        Assert.Empty(publisher.Delivered);
        Assert.Equal(0, storage.Saves);
    }

    public static IEnumerable<object[]> CourtAcceptedClaimCases() =>
        from command in new[] { "event-create", "event-update", "deadline-create", "deadline-update", "deadline-close", "hearing", "sync", "complaint" }
        from postgres in new[] { false, true }
        select new object[] { command, postgres };

    [Theory]
    [MemberData(nameof(CourtAcceptedClaimCases))]
    public async Task CourtDateVersion_FirstClaimFaultKeepsAcceptedCommandSuccessful(string command, bool postgres)
    {
        var fault = new CaseDateClaimFailure();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString, fault) : await CreateContextAsync(fault);
        var publisher = new CourtNoticeIntegrationRealtime(db);
        var service = NewCourtRecoveryService(db, publisher);
        if (command == "complaint")
        {
            var seed = await SeedCourtWriteAsync(db, "create");
            Assert.IsType<CreatedResult>(await InvokeCourtWriteAsync(CreateController(db, seed.Base.User,
                notificationService: service, realtimePublisher: publisher), seed, "create", default));
        }
        else if (command == "sync")
        {
            var (user, clientCase) = await SeedCourtSearchAsync(db);
            Assert.IsType<OkObjectResult>((await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask),
                new CourtCoreStorage { Db = db }, service, publisher).RunCourtSync(clientCase.Id, null, default)).Result);
        }
        else
        {
            var seed = await SeedCourtRecoveryAsync(db);
            AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User,
                notificationService: service, realtimePublisher: publisher), seed, command, default), command);
        }
        Assert.True(fault.Called);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        var intents = await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync();
        Assert.NotEmpty(intents);
        Assert.Contains(intents, row => row.CompletedAt is null);
        var before = await CourtAcceptedDomainAsync(db);
        await db.CaseCourtNotificationOutboxes.Where(row => row.CompletedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        var processor = new CaseCourtNotificationDispatchProcessor(db, service, publisher);
        await processor.ProcessBatchAsync(default);
        Assert.All(await db.CaseCourtNotificationOutboxes.AsNoTracking().ToArrayAsync(), row => Assert.NotNull(row.CompletedAt));
        Assert.Equal(before, await CourtAcceptedDomainAsync(db));
        Assert.All((await db.UserNotifications.AsNoTracking().Where(row => row.EventKey.StartsWith("case-court:")).ToArrayAsync())
            .GroupBy(row => row.EventKey), group => Assert.Single(group));
        Assert.Equal(0, await processor.ProcessBatchAsync(default));
        Assert.All(publisher.Delivered.Where(row => row.EventKey.StartsWith("case-court:")).GroupBy(row => row.EventKey), group => Assert.Single(group));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtDateVersion_FinalJournalClaimFaultPreservesSuccessfulCourtCheck(bool postgres)
    {
        var fault = new CourtFinalJournalClaimFault();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString, fault) : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var publisher = new CourtNoticeIntegrationRealtime(db);
        var service = NewCourtRecoveryService(db, publisher);
        var storage = new CourtPublicationStorage { AfterSave = () => { fault.Armed = true; return Task.CompletedTask; } };
        Assert.IsType<OkObjectResult>((await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            service, publisher).RunCourtSync(clientCase.Id, null, default)).Result);
        Assert.True(fault.Called);
        Assert.True(storage.HasFile);
        Assert.Equal(1, storage.Saves);
        Assert.NotNull((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        var pending = Assert.Single(await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.CompletedAt == null).ToArrayAsync());
        Assert.Equal("journal", pending.Kind);
        var before = await CourtCoreStateAsync(db);
        var delivered = publisher.Delivered.Count;
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(pending.Id, default));
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(delivered, publisher.Delivered.Count);
        Assert.Equal(1, storage.Saves);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CourtDateVersion_FileFailureRetainsAcceptedMarker(bool decision, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/search")
            .SetProperty(row => row.CourtCaseNumber, "42/2026"));
        db.ChangeTracker.Clear();
        var date = (decision ? seed.Case.CourtDecisionAt : seed.Case.NextHearingAt)!.Value.AddDays(2);
        var publisher = new CourtNoticeIntegrationRealtime(db);
        var storage = new CourtCoreStorage { Throw = true };
        await Assert.ThrowsAsync<CourtCoreFileFault>(() => CourtCoreController(db, seed.Editor,
            new CourtDateFileProvider(decision, date), storage, NewCourtRecoveryService(db, publisher), publisher)
            .RunCourtSync(seed.Case.Id, null, default));
        var saved = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(date, decision ? saved.CourtDecisionAt : saved.NextHearingAt);
        Assert.NotNull(decision ? saved.CourtDecisionAt : saved.NextHearingAt);
        var marker = Assert.Single(await db.CaseCourtNotificationOutboxes.AsNoTracking()
            .Where(row => row.Kind == (decision ? "decision-marker" : "hearing-marker")).ToArrayAsync());
        Assert.NotNull(marker.CompletedAt);
        Assert.DoesNotContain(publisher.Delivered, row => row.EventKey == marker.EventKey);
        Assert.False(await db.UserNotifications.AnyAsync(row => row.EventKey == marker.EventKey));
        Assert.Equal(1, storage.Saves);
    }

    private static async Task<string> CourtAcceptedDomainAsync(PaymentContext db)
    {
        var snapshot = JsonNode.Parse(await CourtGraphSnapshotAsync(db))!.AsObject();
        snapshot.Remove("Notifications");
        snapshot.Remove("Deliveries");
        return snapshot.ToJsonString();
    }

    private sealed class CourtDateFileProvider(bool decision, DateTime date) : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        public string Key => "test";
        public string Label => "Синтетический суд";
        public string SourceType => "test";
        public int SchemaVersion => 1;
        public CourtSyncProviderCapability Capabilities => CourtSyncProviderCapability.ReadCase;
        public IReadOnlyList<string> SupportedHosts { get; } = ["court.test"];
        public bool CanHandle(string? url) => url?.StartsWith("https://court.test/", StringComparison.Ordinal) == true;
        public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders() => [];
        public ICourtSyncProvider? Resolve(string? url) => CanHandle(url) ? this : null;
        public Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct) => Task.FromResult(new SudrfCourtCaseData
        {
            SourceUrl = binding.SourceUrl,
            CourtName = Label,
            CaseNumber = "42/2026",
            Instance = "first",
            NextHearingAt = decision ? null : date,
            DecisionAt = decision ? date : null,
            CourtActs = [new() { Title = "Решение", ActDate = date, Result = "Синтетическое решение", SourceUrl = binding.SourceUrl, Summary = "Синтетическая сводка" }]
        });
    }

    private sealed class CourtDateMarkerInsertFault(bool cancel) : SaveChangesInterceptor
    {
        private bool trigger;
        public bool Called { get; private set; }
        public Action? Cancel { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            trigger = eventData.Context!.ChangeTracker.Entries<CaseCourtNotificationOutbox>().Any(row => row.State == EntityState.Added
                && row.Entity.Kind is "hearing-marker" or "decision-marker");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (trigger)
            {
                trigger = false; Called = true;
                if (cancel) { Cancel!(); throw new OperationCanceledException(cancellationToken); }
                throw new CourtRecoveryInjectedFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CourtFinalJournalClaimFault : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Called && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("CaseCourtNotificationOutboxes", StringComparison.Ordinal) && command.CommandText.Contains("LeaseToken", StringComparison.Ordinal))
            {
                Called = true; throw new IOException("Synthetic final journal claim failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}

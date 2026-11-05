using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtDateVersionCases() =>
        from decision in new[] { false, true }
        from mode in new[] { "aba", "metadata", "unchanged" }
        from postgres in new[] { false, true }
        select new object[] { decision, mode, postgres };

    [Theory]
    [MemberData(nameof(CourtDateVersionCases))]
    public async Task CourtDateVersion_AutomaticSyncSupersedesOnlyChangedManualDate(bool decision, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres ? CaseDatePostgres(database!.ConnectionString) : await CreateContextAsync();
        var seed = await SeedCaseDateAsync(db, decision);
        var source = await db.ClientCases.SingleAsync();
        source.CourtUrl = "https://court.test/search";
        source.CourtCaseNumber = "42/2026";
        source.CourtName = "Синтетический суд";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        source = await db.ClientCases.SingleAsync();
        var date = (decision ? source.CourtDecisionAt : source.NextHearingAt)!.Value;
        var oldId = await QueueCaseDateAsync(db, source, decision, date.AddDays(-1), seed.Editor.Id);
        var old = await db.CaseCourtNotificationOutboxes.AsNoTracking().SingleAsync(row => row.Id == oldId);
        var publisher = new CaseDatePublisher(db) { FailUser = true };
        var service = NotificationServiceTests.CreateService(db, new NotificationServiceTests.RecordingEmailService(),
            new NotificationServiceTests.RecordingTelegramService(), publisher);
        Assert.False(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(oldId, default));
        var inboxId = await db.UserNotifications.AsNoTracking().Where(row => row.EventKey == old.EventKey).Select(row => row.Id).SingleAsync();
        var provider = new CaseDateSyncProvider
        {
            Date = mode == "aba" ? date.AddDays(2) : date,
            Decision = decision,
            CourtName = mode == "metadata" ? "Изменились только реквизиты суда" : "Синтетический суд"
        };
        var storage = new CourtCoreStorage { Db = db };
        Task<IActionResult> Sync() => SyncResult();
        async Task<IActionResult> SyncResult() => (await CourtCoreController(db, seed.Editor, provider, storage, service, publisher)
            .RunCourtSync(source.Id, null, default)).Result!;
        Assert.IsType<OkObjectResult>(await Sync());
        if (mode == "aba")
        {
            provider.Date = date;
            Assert.IsType<OkObjectResult>(await Sync());
        }
        var saved = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(date, decision ? saved.CourtDecisionAt : saved.NextHearingAt);
        var state = await CourtCoreStateAsync(db);
        publisher.FailUser = false;
        await db.CaseCourtNotificationOutboxes.Where(row => row.Id == oldId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAt, DateTime.UnixEpoch));
        Assert.True(await new CaseCourtNotificationDispatchProcessor(db, service, publisher).ProcessAsync(oldId, default));
        Assert.Equal(mode == "aba" ? 0 : 1, publisher.Delivered.Count(row => row.EventKey == old.EventKey));
        Assert.Equal(inboxId, await db.UserNotifications.AsNoTracking().Where(row => row.EventKey == old.EventKey).Select(row => row.Id).SingleAsync());
        Assert.Equal(state, await CourtCoreStateAsync(db));
        var markerKind = decision ? "decision-marker" : "hearing-marker";
        var markers = await db.CaseCourtNotificationOutboxes.AsNoTracking().Where(row => row.Kind == markerKind).ToArrayAsync();
        Assert.Equal(mode == "aba" ? 2 : 0, markers.Length);
        Assert.All(markers, row => Assert.NotNull(row.CompletedAt));
        var keys = markers.Select(row => row.EventKey).ToArray();
        Assert.Empty(await db.UserNotifications.AsNoTracking().Where(row => keys.Contains(row.EventKey)).ToArrayAsync());
    }

    private sealed class CaseDateSyncProvider : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        public DateTime Date { get; set; }
        public bool Decision { get; set; }
        public string CourtName { get; set; } = "Синтетический суд";
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
            CourtName = CourtName,
            CaseNumber = "42/2026",
            Instance = "first",
            NextHearingAt = Decision ? null : Date,
            DecisionAt = Decision ? Date : null
        });
    }
}

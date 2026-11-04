using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData("unapproved", 401)]
    [InlineData("inactive", 401)]
    [InlineData("session", 401)]
    [InlineData("permission", 403)]
    [InlineData("view", 403)]
    [InlineData("role", 403)]
    [InlineData("hidden", 404)]
    [InlineData("owner", 404)]
    [InlineData("deleted", 404)]
    public async Task CourtCoreHttp_LateAccessRevocationRejectsSuccessfulSourceWithoutWrites(string mode, int expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var provider = new CourtCoreProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
            before = await CourtCoreStateAsync(db);
        });
        var storage = new CourtCoreStorage();
        var notifications = NewCourtCoreNotifications(db);
        var realtime = new CourtCoreRealtime(db);
        using var server = CourtCoreServer(db, user, provider, storage, notifications, realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Equal(0, realtime.Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("number")]
    [InlineData("uid")]
    [InlineData("court")]
    [InlineData("attempt")]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("error")]
    public async Task CourtCoreHttp_ChangedSourceOrNewerOutcomeRejectsSuccessfulReply(string change)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var now = DateTime.UtcNow;
        var provider = new CourtCoreProvider(async () =>
        {
            var query = db.ClientCases.Where(row => row.Id == clientCase.Id);
            if (change == "url") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
            else if (change == "number") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtCaseNumber, "new-number"));
            else if (change == "uid") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.JudicialUid, "new-uid"));
            else if (change == "court") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtName, "Новый суд"));
            else if (change == "attempt") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastAttemptAt, now));
            else if (change == "success") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastSuccessAt, now));
            else if (change == "failure") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncFailureStartedAt, now));
            else await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastError, "Новый результат"));
            before = await CourtCoreStateAsync(db);
        });
        var storage = new CourtCoreStorage();
        var notifications = NewCourtCoreNotifications(db);
        using var server = CourtCoreServer(db, user, provider, storage, notifications, new CourtCoreRealtime(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("JudgeName")]
    [InlineData("Courtroom")]
    [InlineData("Claimant")]
    public async Task CourtCoreHttp_ConcurrentManualCourtFieldIsPreserved(string field)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var provider = new CourtCoreProvider(async () =>
        {
            await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => EF.Property<string>(row, field), "Изменено вручную"));
            before = await CourtCoreStateAsync(db);
        });
        using var server = CourtCoreServer(db, user, provider, new CourtCoreStorage(), NewCourtCoreNotifications(db), new CourtCoreRealtime(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal("Изменено вручную", await db.ClientCases.AsNoTracking().Select(row => EF.Property<string>(row, field)).SingleAsync());
    }

    [Fact]
    public void CourtCore_CourtFieldSnapshotCoversAllSourceTouchedCaseFields()
    {
        string[] fields = [nameof(ClientCase.JudgeName), nameof(ClientCase.Courtroom), nameof(ClientCase.Claimant), nameof(ClientCase.Defendant),
            nameof(ClientCase.ThirdParties), nameof(ClientCase.Representatives), nameof(ClientCase.NextHearingAt), nameof(ClientCase.CourtDecisionAt),
            nameof(ClientCase.AppealDeadlineAt), nameof(ClientCase.CassationDeadlineAt), nameof(ClientCase.NextHearingConfirmedForAt),
            nameof(ClientCase.NextHearingConfirmedAt), nameof(ClientCase.NextHearingConfirmedByUserId), nameof(ClientCase.NextHearingConfirmedByName)];
        var type = typeof(CasesV2Controller).GetNestedType("CourtSyncCaseState", BindingFlags.NonPublic)!;
        var capture = type.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var field in fields)
        {
            var baseline = capture.Invoke(null, [new ClientCase()]);
            var changed = new ClientCase();
            var property = typeof(ClientCase).GetProperty(field)!;
            object value = property.PropertyType == typeof(string) ? "Изменено вручную"
                : Nullable.GetUnderlyingType(property.PropertyType) == typeof(DateTime) ? DateTime.UnixEpoch.AddDays(1) : 17;
            property.SetValue(changed, value);
            Assert.NotEqual(baseline, capture.Invoke(null, [changed]));
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task CourtCore_SqlFailureRollsBackAcceptedGraphAndSuppressesPublication(int ordinal, bool cancel)
    {
        var fault = new CourtCoreAfterSql(ordinal, cancel);
        await using var db = await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var before = await CourtCoreStateAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        var storage = new CourtCoreStorage();
        var notifications = NewCourtCoreNotifications(db);
        var realtime = new CourtCoreRealtime(db);
        var controller = CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, notifications, realtime);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else await Assert.ThrowsAsync<CourtCoreSqlFault>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        Assert.True(fault.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Equal(0, realtime.Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCore_NotificationFailureKeepsAllAcceptedDeadlinesAndControlTasks()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtCoreStorage();
        var notifications = NewCourtCoreNotifications(db);
        ((CourtCoreNotifications)(object)notifications).Throw = true;
        var controller = CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, notifications, new CourtCoreRealtime(db));
        await Assert.ThrowsAsync<CourtCoreNotificationFault>(() => controller.RunCourtSync(clientCase.Id, null, CancellationToken.None));
        Assert.NotEmpty(await db.ClientCaseCourtEvents.ToArrayAsync());
        Assert.NotEmpty(await db.ClientCaseCourtParties.ToArrayAsync());
        Assert.NotEmpty(await db.ClientCaseProceduralDeadlines.ToArrayAsync());
        Assert.NotEmpty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Equal(0, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCore_FileFailureKeepsAcceptedDeadlinesAndControlTasks()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtCoreStorage { Throw = true, Db = db };
        var notifications = NewCourtCoreNotifications(db);
        var controller = CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, notifications, new CourtCoreRealtime(db));
        await Assert.ThrowsAsync<CourtCoreFileFault>(() => controller.RunCourtSync(clientCase.Id, null, CancellationToken.None));
        Assert.NotEmpty(await db.ClientCaseProceduralDeadlines.ToArrayAsync());
        Assert.NotEmpty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCoreHttp_CurrentSourceRetainsCompleteResponseAndLinks()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtCoreStorage { Db = db };
        var notifications = NewCourtCoreNotifications(db);
        ((CourtCoreNotifications)(object)notifications).RequireCompleteCore = true;
        using var server = CourtCoreServer(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, notifications, new CourtCoreRealtime(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtSyncRunDto>();
        Assert.Equal("external-sync-completed", reply!.Status);
        Assert.True(reply.SyncedFromExternalSource);
        Assert.Equal(1, reply.CreatedCourtEvents);
        Assert.Equal(1, reply.CreatedCourtParties);
        Assert.Equal(1, reply.CreatedCourtActDocuments);
        Assert.True(reply.CreatedProceduralDeadlines > 0);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("Принятый суд", actual.CourtName);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.NotNull(actual.CourtSyncLastSuccessAt);
        var document = await db.ClientCaseDocuments.SingleAsync();
        Assert.All(await db.ClientCaseProceduralDeadlines.ToArrayAsync(), row => Assert.Equal(document.Id, row.ClientCaseDocumentId));
        Assert.NotEmpty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtCore_ExistingContextAcceptsSameStableSourceWithoutFalseConflictOrDuplicate(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtName, "Изменено до запроса"));
        var storage = new CourtCoreStorage { Db = db };
        var notifications = NewCourtCoreNotifications(db);
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        var first = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db)).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first.Result);
        var events = await db.ClientCaseCourtEvents.Select(row => row.Id).ToArrayAsync();
        var deadlines = await db.ClientCaseProceduralDeadlines.Select(row => row.Id).ToArrayAsync();
        var tasks = await db.TaskJournalItems.Select(row => row.Id).ToArrayAsync();
        var second = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db)).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(second.Result);
        Assert.Equal(events, await db.ClientCaseCourtEvents.Select(row => row.Id).ToArrayAsync());
        Assert.Equal(deadlines, await db.ClientCaseProceduralDeadlines.Select(row => row.Id).ToArrayAsync());
        Assert.Equal(tasks, await db.TaskJournalItems.Select(row => row.Id).ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("binding")]
    [InlineData("busy")]
    [InlineData("rollback")]
    [InlineData("cancel")]
    public async Task CourtCoreHttp_ExistingPostgresProtectsAcceptance(string mode)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        var fault = new CourtCoreAfterSql(2, mode == "cancel");
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).AddInterceptors(fault).Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = mode is "rollback" or "cancel";
        string before = await CourtCoreStateAsync(db);
        var provider = new CourtCoreProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (mode == "unapproved") await ChangeCourtSearchAccessAsync(other, user, clientCase.Id, mode);
            if (mode == "binding") await other.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
            if (mode == "busy")
            {
                await other.Database.BeginTransactionAsync();
                await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
            }
            before = await CourtCoreStateAsync(db);
        });
        var storage = new CourtCoreStorage();
        var notifications = NewCourtCoreNotifications(db);
        var controller = CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db));
        if (mode == "rollback") await Assert.ThrowsAsync<CourtCoreSqlFault>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else
        {
            var result = await controller.RunCourtSync(clientCase.Id, null, cancellation.Token);
            if (mode == "unapproved") Assert.IsType<UnauthorizedResult>(result.Result);
            else Assert.IsType<ConflictObjectResult>(result.Result);
        }
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task<string> CourtCoreStateAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Case = await CourtCheckStateAsync(db),
        CourtFields = await db.ClientCases.AsNoTracking().Select(row => new
        {
            row.JudgeName,
            row.Courtroom,
            row.Claimant,
            row.Defendant,
            row.ThirdParties,
            row.Representatives,
            row.NextHearingAt,
            row.CourtDecisionAt,
            row.AppealDeadlineAt,
            row.CassationDeadlineAt,
            row.NextHearingConfirmedAt,
            row.NextHearingConfirmedForAt,
            row.NextHearingConfirmedByUserId,
            row.NextHearingConfirmedByName,
        }).ToArrayAsync(),
        Events = await db.ClientCaseCourtEvents.AsNoTracking().Select(row => new { row.Id, row.ClientCaseId, row.Title, row.ScheduledAt }).ToArrayAsync(),
        Parties = await db.ClientCaseCourtParties.AsNoTracking().Select(row => new { row.Id, row.ClientCaseId, row.DisplayName }).ToArrayAsync(),
        Instances = await db.ClientCaseCourtInstances.AsNoTracking().Select(row => new { row.Id, row.ClientCaseId, row.CourtName }).ToArrayAsync(),
        Deadlines = await db.ClientCaseProceduralDeadlines.AsNoTracking().Select(row => new { row.Id, row.ClientCaseId, row.DueAt, row.ClientCaseDocumentId }).ToArrayAsync(),
        Documents = await db.ClientCaseDocuments.AsNoTracking().Select(row => new { row.Id, row.ClientCaseId, row.DocumentKey }).ToArrayAsync(),
    });

    private static CasesV2Controller CourtCoreController(PaymentContext db, User user, ICourtSyncProviderRegistry provider,
        ICaseDocumentStorage storage, INotificationService notifications, IRealtimeNotificationPublisher realtime)
        => new(db, caseDocumentStorage: storage, courtSyncProviderRegistry: provider, notificationService: notifications, realtimePublisher: realtime)
        { ControllerContext = CreateController(db, user).ControllerContext };

    private static INotificationService NewCourtCoreNotifications(PaymentContext db)
    {
        var service = DispatchProxy.Create<INotificationService, CourtCoreNotifications>();
        ((CourtCoreNotifications)(object)service).Db = db;
        return service;
    }

    public class CourtCoreNotifications : DispatchProxy
    {
        public PaymentContext Db { get; set; } = null!;
        public bool Throw { get; set; }
        public bool RequireCompleteCore { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls++;
            if (method!.Name == nameof(INotificationService.NotifyCaseDocumentAsync)) return Task.CompletedTask;
            Assert.Contains(method.Name, new[]
            {
                nameof(INotificationService.NotifyAsync),
                nameof(INotificationService.NotifyCaseCourtEventAsync),
                nameof(INotificationService.NotifyCaseProceduralDeadlineAsync),
            });
            Assert.Null(Db.Database.CurrentTransaction);
            if (RequireCompleteCore)
            {
                Assert.NotEmpty(Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArray());
                Assert.NotEmpty(Db.TaskJournalItems.AsNoTracking().ToArray());
            }
            if (method.ReturnType == typeof(Task<bool>))
                return Throw ? Task.FromException<bool>(new CourtCoreNotificationFault()) : Task.FromResult(true);
            return Throw ? Task.FromException(new CourtCoreNotificationFault()) : Task.CompletedTask;
        }
    }

    private sealed class CourtCoreRealtime(PaymentContext db) : IRealtimeNotificationPublisher
    {
        public int Calls { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CourtCoreFileFault : Exception;
    private sealed class CourtCoreNotificationFault : Exception;
    private sealed class CourtCoreSqlFault : Exception;
    private sealed class CourtCoreAfterSql(int ordinal, bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action Cancel { get; set; } = null!;
        private int saves;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!Armed || ++saves != ordinal) return await base.SavedChangesAsync(data, result, ct);
            Called = true;
            if (cancel) { Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new CourtCoreSqlFault();
        }
    }

    private sealed class CourtCoreStorage : ICaseDocumentStorage
    {
        public int Saves { get; private set; }
        public bool Throw { get; set; }
        public PaymentContext? Db { get; set; }
        private readonly HashSet<string> files = [];
        public string BuildRelativePath(int caseId, string fileName) => caseId + "/" + fileName;
        public string ResolvePath(string path) => path;
        public bool Exists(string path) => files.Contains(path);
        public void DeleteIfExists(string path) => files.Remove(path);
        public Task SaveAsync(string path, Stream stream, CancellationToken ct) => SaveAsync(path, [], ct);
        public Task SaveAsync(string path, byte[] bytes, CancellationToken ct)
        {
            if (Db is not null)
            {
                Assert.Null(Db.Database.CurrentTransaction);
                Assert.NotEmpty(Db.ClientCaseProceduralDeadlines.AsNoTracking().ToArray());
                Assert.NotEmpty(Db.TaskJournalItems.AsNoTracking().ToArray());
            }
            Saves++;
            if (Throw) throw new CourtCoreFileFault();
            files.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class CourtCoreProvider(Func<Task> whileWaiting) : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        private readonly DateTime observedAt = DateTime.UtcNow;
        public string Key => "test";
        public string Label => "Синтетический суд";
        public string SourceType => "test";
        public int SchemaVersion => 1;
        public CourtSyncProviderCapability Capabilities => CourtSyncProviderCapability.ReadCase;
        public IReadOnlyList<string> SupportedHosts { get; } = ["court.test"];
        public bool CanHandle(string? url) => url?.StartsWith("https://court.test/", StringComparison.Ordinal) == true;
        public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders() => [];
        public ICourtSyncProvider? Resolve(string? url) => CanHandle(url) ? this : null;
        public async Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct)
        {
            await whileWaiting();
            return new SudrfCourtCaseData
            {
                SourceUrl = binding.SourceUrl,
                CourtName = "Принятый суд",
                JudgeName = "Судья источника",
                NextHearingPlace = "Зал источника",
                CaseNumber = "42/2026",
                Instance = "first",
                Participants = [new() { Name = "Синтетическая сторона", Role = "ИСТЕЦ" }],
                MovementEvents = [new() { Title = "Судебное заседание", ScheduledAt = observedAt.AddDays(7), SourceSummary = "Синтетическое заседание" }],
                CourtActs = [new() { Title = "Решение", ActDate = observedAt.Date.AddDays(-2), Result = "Требования удовлетворены", SourceUrl = binding.SourceUrl, Summary = "Синтетическое решение" }],
            };
        }
    }

    private static TestServer CourtCoreServer(PaymentContext db, User user, ICourtSyncProviderRegistry provider,
        ICaseDocumentStorage storage, INotificationService notifications, IRealtimeNotificationPublisher realtime)
        => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("Email:Enabled", "false").UseSetting("BackgroundServices:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IDbContextFactory<PaymentContext>>(new CourtSearchContextFactory(db));
                services.AddAuthentication("court-core-fixture").AddScheme<AuthenticationSchemeOptions, CourtSearchAuthentication>("court-core-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly);
                services.AddSingleton<IControllerActivator>(new CourtCoreActivator(db, user, provider, storage, notifications, realtime));
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

    private sealed class CourtCoreActivator(PaymentContext db, User user, ICourtSyncProviderRegistry provider,
        ICaseDocumentStorage storage, INotificationService notifications, IRealtimeNotificationPublisher realtime) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            var controller = CourtCoreController(db, user, provider, storage, notifications, realtime);
            controller.ControllerContext = context;
            return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }
}

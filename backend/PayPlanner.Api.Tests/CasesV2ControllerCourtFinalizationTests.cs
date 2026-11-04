using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
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
    public async Task CourtFinalHttp_RevocationAfterCoreCannotMarkSuccess(string mode, int expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var realtime = new CourtFinalRealtime(async () =>
        {
            await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
            before = await CourtCoreStateAsync(db);
        });
        using var server = CourtFinalServer(db, user, new CourtFinalProvider(), realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.True(realtime.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Null((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.NotEmpty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("outcome")]
    [InlineData("court-field")]
    public async Task CourtFinalHttp_NewSourceOrOutcomeAfterCoreIsPreserved(string mode)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var now = DateTime.UtcNow;
        var realtime = new CourtFinalRealtime(async () =>
        {
            var query = db.ClientCases.Where(row => row.Id == clientCase.Id);
            if (mode == "binding") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
            else if (mode == "outcome") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastAttemptAt, now).SetProperty(row => row.CourtSyncLastSuccessAt, now));
            else await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Изменено вручную"));
            before = await CourtCoreStateAsync(db);
        });
        using var server = CourtFinalServer(db, user, new CourtFinalProvider(), realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(realtime.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtFinalHttp_MergesCurrentHistoryAndPreservesIndependentTitle()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var realtime = new CourtFinalRealtime(async () =>
        {
            await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => row.EventHistory, "История другого сотрудника\n").SetProperty(row => row.Title, "Новое название"));
        });
        using var server = CourtFinalServer(db, user, new CourtFinalProvider(), realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("Новое название", actual.Title);
        Assert.Collection(actual.EventHistory.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Equal("История другого сотрудника", line),
            line => Assert.Contains("Судебный источник", line));
        Assert.NotNull(actual.CourtSyncLastSuccessAt);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("binding")]
    [InlineData("busy")]
    public async Task CourtFinal_ExistingPostgresRechecksAfterCore(string mode)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var realtime = new CourtFinalRealtime(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (mode == "unapproved") await ChangeCourtSearchAccessAsync(other, user, clientCase.Id, mode);
            else if (mode == "binding") await other.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
            else
            {
                await other.Database.BeginTransactionAsync();
                await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
            }
            before = await CourtCoreStateAsync(db);
        });
        var controller = CourtFinalController(db, user, new CourtFinalProvider(), realtime);
        var reply = await controller.RunCourtSync(clientCase.Id, null, CancellationToken.None);
        if (mode == "unapproved") Assert.IsType<UnauthorizedResult>(reply.Result);
        else Assert.IsType<ConflictObjectResult>(reply.Result);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    [InlineData(false, "new-version")]
    [InlineData(true, "new-version")]
    [InlineData(false, "deadline")]
    [InlineData(true, "deadline")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(true, "busy")]
    public async Task CourtFinal_ConcurrentDocumentChangesCannotBeOverwritten(bool postgres, string mode)
    {
        var publication = new CourtFinalBeforePublication();
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(publication).Options)
            : await CreateContextAsync(publication);
        await using var other = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options) : null;
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtCoreStorage();
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        var notifications = NewCourtCoreNotifications(db);
        var first = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db))
            .RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first.Result);
        var documentId = await db.ClientCaseDocuments.Select(row => row.Id).SingleAsync();
        string? before = null;
        string? documentsBefore = null;
        publication.BeforeRead = async () =>
        {
            var writer = (PaymentContext?)other ?? db;
            if (mode == "metadata")
                await writer.ClientCaseDocuments.Where(row => row.Id == documentId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "archived"));
            else if (mode == "new-version")
            {
                var next = await writer.ClientCaseDocuments.AsNoTracking().SingleAsync(row => row.Id == documentId);
                next.Id = 0;
                next.Version++;
                writer.ClientCaseDocuments.Add(next);
                await writer.SaveChangesAsync();
                writer.Entry(next).State = EntityState.Detached;
            }
            else if (mode == "deadline")
                await writer.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, (int?)null));
            else if (mode == "deleted")
                await writer.ClientCaseDocuments.Where(row => row.Id == documentId).ExecuteDeleteAsync();
            else
            {
                await writer.Database.BeginTransactionAsync();
                await writer.ClientCaseDocuments.Where(row => row.Id == documentId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "archived"));
            }
            before = await CourtCoreStateAsync(db);
            documentsBefore = await CourtFinalDocumentsAsync(db);
        };
        var result = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db))
            .RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.True(publication.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(documentsBefore, await CourtFinalDocumentsAsync(db));
        Assert.Equal(1, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtFinal_MetadataAndSuccessRollBackTogetherAfterSql(bool postgres, bool cancel)
    {
        var publication = new CourtFinalBeforePublication();
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var fault = new CourtFinalAfterSql(cancel);
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(fault, publication).Options)
            : await CreateContextAsync(fault, additionalInterceptors: [publication]);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtCoreStorage();
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        var notifications = NewCourtCoreNotifications(db);
        var first = await CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db))
            .RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(first.Result);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        string? before = null;
        string? documentsBefore = null;
        publication.BeforeRead = async () =>
        {
            before = await CourtCoreStateAsync(db);
            documentsBefore = await CourtFinalDocumentsAsync(db);
            fault.Armed = true;
        };
        var controller = CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else await Assert.ThrowsAsync<CourtFinalSqlFault>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        Assert.True(fault.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal(documentsBefore, await CourtFinalDocumentsAsync(db));
        Assert.Equal(1, storage.Saves);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task<string> CourtFinalDocumentsAsync(PaymentContext db)
        => JsonSerializer.Serialize(await db.ClientCaseDocuments.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.ClientCaseId,
            row.DocumentKey,
            row.Version,
            row.Status,
            row.DocumentType,
            row.OriginalFileName,
            row.SourceType,
            row.SourceUrl,
            row.SourceCheckedAt,
            row.SourceConfidence,
            row.StoragePath,
            row.StoredFileName,
            row.ContentType,
            row.SizeBytes,
        }).ToArrayAsync());

    private sealed class CourtFinalBeforePublication : DbCommandInterceptor
    {
        public Func<Task>? BeforeRead { get; set; }
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (BeforeRead is not null && command.CommandText.Contains("FROM \"StorageProductBindings\"", StringComparison.Ordinal))
            {
                var callback = BeforeRead;
                BeforeRead = null;
                Called = true;
                await callback();
            }
            return result;
        }
    }

    private sealed class CourtFinalSqlFault : Exception;

    private sealed class CourtFinalAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action Cancel { get; set; } = null!;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!Armed) return await base.SavedChangesAsync(data, result, ct);
            Called = true;
            Assert.NotNull(data.Context!.Database.CurrentTransaction);
            if (cancel) { Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new CourtFinalSqlFault();
        }
    }

    private static CasesV2Controller CourtFinalController(PaymentContext db, User user, ICourtSyncProviderRegistry provider, IRealtimeNotificationPublisher realtime)
        => new(db, caseDocumentStorage: new CourtCoreStorage(), courtSyncProviderRegistry: provider,
            notificationService: NewCourtCoreNotifications(db), realtimePublisher: realtime)
        { ControllerContext = CreateController(db, user).ControllerContext };

    private sealed class CourtFinalRealtime(Func<Task> afterCore) : IRealtimeNotificationPublisher
    {
        public bool Called { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default)
        {
            if (Called) return;
            Called = true;
            await afterCore();
        }
    }

    private sealed class CourtFinalProvider : ICourtSyncProviderRegistry, ICourtSyncCaseReader
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
            CourtName = "Принятый суд",
            CaseNumber = binding.CourtCaseNumber ?? "",
            NextHearingAt = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc),
            MovementEvents = [new() { Title = "Судебное заседание", ScheduledAt = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc), SourceSummary = "Заседание" }],
        });
    }

    private static TestServer CourtFinalServer(PaymentContext db, User user, ICourtSyncProviderRegistry provider, IRealtimeNotificationPublisher realtime)
        => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("Email:Enabled", "false").UseSetting("BackgroundServices:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IDbContextFactory<PaymentContext>>(new CourtSearchContextFactory(db));
                services.AddAuthentication("court-final-fixture").AddScheme<AuthenticationSchemeOptions, CourtSearchAuthentication>("court-final-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly);
                services.AddSingleton<IControllerActivator>(new CourtFinalActivator(db, user, provider, realtime));
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class CourtFinalActivator(PaymentContext db, User user, ICourtSyncProviderRegistry provider, IRealtimeNotificationPublisher realtime) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            var controller = CourtFinalController(db, user, provider, realtime);
            controller.ControllerContext = context;
            return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }
}

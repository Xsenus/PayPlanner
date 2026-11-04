using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.LegalIntegrations;
using PayPlanner.Api.Services.LegalIntegrations.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtBindLateAdmissions()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var sourceError in new[] { false, true })
                foreach (var mode in new[] { "inactive", "unapproved", "session", "permission", "view", "role", "hidden", "owner" })
                    yield return [postgres, sourceError, mode];
    }

    [Theory]
    [MemberData(nameof(CourtBindLateAdmissions))]
    public async Task CourtBindHttp_LateAdmissionPreservesRecords(bool postgres, bool sourceError, string mode)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var provider = new CourtBindingProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
            before = await CourtBindingStateAsync(db);
        }, sourceError);
        var storage = new CourtCoreStorage();
        var notifications = NewCourtBindingNotifications(db);
        var realtime = new CourtCoreRealtime(db);
        using var server = CourtCoreServer(db, user, provider, storage, notifications, realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/bind", CourtBindingRequest());
        var expected = mode is "inactive" or "unapproved" or "session" ? 401 : mode is "hidden" or "owner" ? 404 : 403;
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(before, await CourtBindingStateAsync(db));
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtBindingNotifications)(object)notifications).Calls);
        Assert.Equal(0, realtime.Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("session")]
    [InlineData("permission")]
    [InlineData("view")]
    [InlineData("role")]
    [InlineData("hidden")]
    [InlineData("owner")]
    public async Task CourtBindHttp_CurrentAdmissionPrecedesProvider(string mode)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtBindingProvider(() => Task.CompletedTask, true);
        var notifications = NewCourtBindingNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications,
            () => ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/bind", CourtBindingRequest());
        var expected = mode is "inactive" or "unapproved" or "session" ? 401 : mode is "hidden" or "owner" ? 404 : 403;
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(0, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
        Assert.Empty(await db.ClientCaseCourtInstances.ToArrayAsync());
    }

    public static IEnumerable<object[]> CourtBindChangedSources()
    {
        foreach (var sourceError in new[] { false, true })
            foreach (var field in new[] { "CourtName", "CourtCaseNumber", "JudicialUid", "CourtUrl", "CourtSyncLastAttemptAt",
                "CourtSyncLastSuccessAt", "CourtSyncFailureStartedAt", "CourtSyncLastError" })
                yield return [sourceError, field];
        yield return [false, "JudgeName"];
        yield return [false, "Courtroom"];
    }

    [Theory]
    [MemberData(nameof(CourtBindChangedSources))]
    public async Task CourtBindHttp_ChangedCaseRequiresFreshConfirmation(bool sourceError, string field)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var provider = new CourtBindingProvider(async () =>
        {
            if (field.EndsWith("At", StringComparison.Ordinal))
                await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                    set.SetProperty(row => EF.Property<DateTime?>(row, field), DateTime.UnixEpoch.AddDays(2)));
            else await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => EF.Property<string>(row, field), "Сохранено другим сотрудником"));
            before = await CourtBindingStateAsync(db);
        }, sourceError);
        using var server = CourtCoreServer(db, user, provider, new CourtCoreStorage(), NewCourtBindingNotifications(db), new CourtCoreRealtime(db));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/bind", CourtBindingRequest());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await CourtBindingStateAsync(db));
    }

    [Theory]
    [InlineData(false, "url")]
    [InlineData(true, "url")]
    [InlineData(false, "judge")]
    [InlineData(true, "judge")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "moved")]
    [InlineData(true, "moved")]
    [InlineData(false, "inserted")]
    [InlineData(true, "inserted")]
    public async Task CourtBind_ChangedInstanceIsPreserved(bool postgres, string mode)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var otherCase = new ClientCase { Title = "Другое дело", ResponsibleUserId = user.Id, ClientId = clientCase.ClientId };
        db.ClientCases.Add(otherCase);
        if (mode != "inserted") db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance
        { ClientCaseId = clientCase.Id, Instance = "first", CourtName = "Исходный суд", CourtUrl = clientCase.CourtUrl });
        await db.SaveChangesAsync();
        string? before = null;
        var provider = new CourtBindingProvider(async () =>
        {
            if (mode == "url") await db.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/manual"));
            else if (mode == "judge") await db.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Ручной судья"));
            else if (mode == "deleted") await db.ClientCaseCourtInstances.ExecuteDeleteAsync();
            else if (mode == "moved") await db.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, otherCase.Id));
            else
            {
                db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "first", CourtName = "Ручная инстанция" });
                await db.SaveChangesAsync();
            }
            before = await CourtBindingStateAsync(db);
        });
        var result = await CourtCoreController(db, user, provider, new CourtCoreStorage(), NewCourtBindingNotifications(db), new CourtCoreRealtime(db))
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(before, await CourtBindingStateAsync(db));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task CourtBind_FirstSqlFailureRollsBackBindingAndSource(bool postgres, bool sourceError, bool cancel)
    {
        var fault = new CourtCoreAfterSql(1, cancel);
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var before = await CourtBindingStateAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        var provider = new CourtBindingProvider(() => { fault.Armed = true; return Task.CompletedTask; }, sourceError);
        var storage = new CourtCoreStorage();
        var notifications = NewCourtBindingNotifications(db);
        var controller = CourtCoreController(db, user, provider, storage, notifications, new CourtCoreRealtime(db));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), cancellation.Token));
        else await Assert.ThrowsAsync<CourtCoreSqlFault>(() => controller.BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default));
        Assert.True(fault.Called);
        Assert.Equal(before, await CourtBindingStateAsync(db));
        Assert.Equal(0, storage.Saves);
        Assert.Equal(0, ((CourtBindingNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtBind_CurrentRequestRetainsContractAndIndependentHistory(bool postgres, bool sourceError)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtBindingProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal("https://court.test/search", await db.ClientCases.AsNoTracking().Select(row => row.CourtUrl).SingleAsync());
            Assert.Empty(await db.ClientCaseCourtInstances.AsNoTracking().ToArrayAsync());
            await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Текущий заголовок")
                .SetProperty(row => row.EventHistory, "Параллельная история"));
        }, sourceError);
        var notifications = NewCourtBindingNotifications(db);
        var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(
            (await CourtCoreController(db, user, provider, new CourtCoreStorage(), notifications, new CourtCoreRealtime(db))
                .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default)).Result).Value);
        Assert.Equal(!sourceError, dto.SyncedFromExternalSource);
        Assert.Equal(clientCase.Id, dto.ClientCaseId);
        Assert.Equal("first", dto.Instance);
        Assert.Equal("https://court.test/bound", dto.CourtUrl);
        Assert.Equal(dto.CourtInstance.Id, (await db.ClientCaseCourtInstances.SingleAsync()).Id);
        var saved = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("Текущий заголовок", saved.Title);
        Assert.Contains("Параллельная история", saved.EventHistory);
        Assert.Contains("Привязана судебная карточка", saved.EventHistory);
        Assert.Equal(123.45m, saved.CourtClaimAmount);
        if (sourceError)
        {
            Assert.Equal("private-source-error", dto.SyncError);
            Assert.Null(saved.CourtSyncLastSuccessAt);
            Assert.Equal("private-source-error", saved.CourtSyncLastError);
            Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        }
        else
        {
            Assert.Equal(saved.CourtSyncLastAttemptAt, saved.CourtSyncLastSuccessAt);
            Assert.NotNull(saved.CourtSyncLastSuccessAt);
            Assert.Equal(1, dto.CreatedCourtActDocuments);
            Assert.Equal(1, dto.CreatedProceduralDeadlines);
        }
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "access")]
    [InlineData(true, "access")]
    [InlineData(false, "instance")]
    [InlineData(true, "instance")]
    [InlineData(false, "notes")]
    [InlineData(true, "notes")]
    public async Task CourtBind_FinalPublicationChecksCurrentInstanceAndAccess(bool postgres, string mode)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtBindingProvider(() => Task.CompletedTask);
        var realtime = new CourtFinalRealtime(async () =>
        {
            if (mode == "access") await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, "unapproved");
            else if (mode == "instance") await db.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/manual"));
            else await db.ClientCaseCourtInstances.ExecuteUpdateAsync(set => set.SetProperty(row => row.Notes, "Сохранённая заметка"));
        });
        var result = await CourtCoreController(db, user, provider, new CourtCoreStorage(), NewCourtBindingNotifications(db), realtime)
            .BindCourtSyncCase(clientCase.Id, CourtBindingRequest(), default);
        Assert.True(realtime.Called);
        if (mode == "access") Assert.IsType<UnauthorizedResult>(result.Result);
        else if (mode == "instance") Assert.IsType<ConflictObjectResult>(result.Result);
        else
        {
            var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtSyncBindingDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal("Сохранённая заметка", dto.CourtInstance.Notes);
        }
        if (mode != "notes") Assert.Null((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static CasesV2Controller.ClientCaseCourtSyncBindRequest CourtBindingRequest() => new()
    {
        Instance = "first",
        SourceUrl = "https://court.test/bound",
        CourtName = "Новый суд",
        CourtCaseNumber = "99/2026",
        JudicialUid = "new-uid",
        ConfirmDifferences = true
    };

    private static async Task<string> CourtBindingStateAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await CourtCoreStateAsync(db),
        Instances = await db.ClientCaseCourtInstances.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.ClientCaseId,
            row.Instance,
            row.CourtId,
            row.CourtName,
            row.CourtCaseNumber,
            row.JudicialUid,
            row.CourtUrl,
            row.JudgeName,
            row.Courtroom,
            row.Notes,
            row.CreatedAt,
            row.UpdatedAt,
            row.CreatedByUserId,
            row.UpdatedByUserId
        }).ToArrayAsync()
    });

    private static INotificationService NewCourtBindingNotifications(PaymentContext db)
    {
        var notifications = DispatchProxy.Create<INotificationService, CourtBindingNotifications>();
        ((CourtBindingNotifications)(object)notifications).Db = db;
        return notifications;
    }

    public class CourtBindingNotifications : CourtCoreNotifications
    {
        private int failureCalls;
        public new int Calls => base.Calls + failureCalls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(INotificationService.NotifyCourtSyncFailedAsync)) return base.Invoke(method, args);
            failureCalls++;
            Assert.Null(Db.Database.CurrentTransaction);
            return Throw ? Task.FromException(new CourtCoreNotificationFault()) : Task.CompletedTask;
        }
    }

    private sealed class CourtBindingProvider(Func<Task> whileWaiting, bool sourceError = false) : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        private readonly CourtCoreProvider inner = new(whileWaiting);
        public int Calls { get; private set; }
        public CourtSyncCaseBinding? LastBinding { get; private set; }
        public string Key => inner.Key;
        public string Label => inner.Label;
        public string SourceType => inner.SourceType;
        public int SchemaVersion => inner.SchemaVersion;
        public CourtSyncProviderCapability Capabilities => inner.Capabilities;
        public IReadOnlyList<string> SupportedHosts => inner.SupportedHosts;
        public bool CanHandle(string? url) => inner.CanHandle(url);
        public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders() => [];
        public ICourtSyncProvider? Resolve(string? url) => CanHandle(url) ? this : null;
        public async Task<SudrfCourtCaseData> ReadCaseAsync(CourtSyncCaseBinding binding, CancellationToken ct)
        {
            Calls++;
            LastBinding = binding;
            var data = await inner.ReadCaseAsync(binding, ct);
            if (sourceError) throw new SudrfCourtSyncException("private-source-error");
            return data;
        }
    }
}

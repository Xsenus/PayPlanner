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
    public static IEnumerable<object[]> CourtCheckAdmissions()
    {
        foreach (var manual in new[] { false, true })
            foreach (var afterAuthentication in new[] { false, true })
                foreach (var mode in new[] { "anonymous", "inactive", "unapproved", "session", "permission", "view", "role", "hidden", "owner", "missing" })
                    yield return [manual, afterAuthentication, mode];
    }

    [Theory]
    [MemberData(nameof(CourtCheckAdmissions))]
    public async Task CourtCheckHttp_CurrentAdmissionBeforeProviderAndManualWrite(bool manual, bool afterAuthentication, string mode)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        if (!afterAuthentication) await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
        var provider = new CourtCheckProvider(() => Task.CompletedTask);
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications,
            afterAuthentication ? () => ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode) : null);
        using var client = server.CreateClient();
        if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{(mode == "missing" ? clientCase.Id + 1000 : clientCase.Id)}/court-sync/run", new { manualOnly = manual });
        var expected = mode is "anonymous" or "inactive" or "unapproved" or "session" ? 401
            : mode is "permission" or "view" or "role" ? 403 : 404;
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

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
    public async Task CourtCheckHttp_RevocationDuringProviderCannotRecordError(string mode, int expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var provider = new CourtCheckProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
            before = await CourtCheckStateAsync(db);
        });
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CourtCheckStateAsync(db));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
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
    public async Task CourtCheckHttp_ChangedBindingOrNewerOutcomePreservesCurrentRecord(string change)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var now = DateTime.UtcNow;
        var provider = new CourtCheckProvider(async () =>
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
            before = await CourtCheckStateAsync(db);
        });
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CourtCheckStateAsync(db));
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("manual", false)]
    [InlineData("manual", true)]
    [InlineData("binding-required", false)]
    [InlineData("binding-required", true)]
    [InlineData("number-only", false)]
    [InlineData("number-only", true)]
    [InlineData("unsupported", false)]
    [InlineData("unsupported", true)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    public async Task CourtCheck_SqlFailureOrCancellationRollsBackHistoryAndFailureFlags(string mode, bool cancel)
    {
        var fault = new CourtCheckAfterSqlFault(cancel);
        await using var db = await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        await PrepareCourtCheckModeAsync(db, clientCase.Id, mode);
        var before = await CourtCheckStateAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        var notifications = NewCourtCheckNotifications(db);
        var controller = CreateController(db, user, courtSyncProviderRegistry: new CourtCheckProvider(() => Task.CompletedTask), notificationService: notifications);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncRunRequest { ManualOnly = mode == "manual" }, cancellation.Token));
        else await Assert.ThrowsAsync<CourtCheckSqlFault>(() => controller.RunCourtSync(clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncRunRequest { ManualOnly = mode == "manual" }, cancellation.Token));
        Assert.True(fault.Called);
        Assert.NotEqual(before, fault.Executed);
        Assert.Equal(before, await CourtCheckStateAsync(db));
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("manual", "manual-check-recorded")]
    [InlineData("binding-required", "binding-required")]
    [InlineData("number-only", "manual-check-recorded")]
    [InlineData("unsupported", "manual-check-recorded")]
    [InlineData("error", "external-sync-error")]
    public async Task CourtCheckHttp_CurrentRequestPersistsThenNotifies(string mode, string status)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        await PrepareCourtCheckModeAsync(db, clientCase.Id, mode);
        var provider = new CourtCheckProvider(() => Task.CompletedTask);
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { manualOnly = mode == "manual", comment = "Комментарий сотрудника." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtSyncRunDto>();
        Assert.Equal(status, reply!.Status);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.NotEmpty(actual.EventHistory);
        Assert.Single(actual.EventHistory.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        if (mode == "error")
        {
            Assert.NotNull(actual.CourtSyncFailureStartedAt);
            Assert.Equal("private-source-error", actual.CourtSyncLastError);
            Assert.Equal(1, ((CourtCheckNotifications)(object)notifications).Calls);
        }
        else
        {
            Assert.Null(actual.CourtSyncLastAttemptAt);
            Assert.Equal(0, provider.Calls);
            Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        }
        Assert.Empty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCheck_PostCommitNotificationFailureRetainsSavedOutcome()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var notifications = NewCourtCheckNotifications(db);
        ((CourtCheckNotifications)(object)notifications).Throw = true;
        var controller = CreateController(db, user, courtSyncProviderRegistry: new CourtCheckProvider(() => Task.CompletedTask), notificationService: notifications);
        await Assert.ThrowsAsync<CourtCheckNotificationFault>(() => controller.RunCourtSync(clientCase.Id, null, CancellationToken.None));
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.NotNull(actual.CourtSyncFailureStartedAt);
        Assert.Equal("private-source-error", actual.CourtSyncLastError);
        Assert.NotEmpty(actual.EventHistory);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("case")]
    [InlineData("permission")]
    public async Task CourtCheckHttp_ExistingPostgresBusyWriteRefusesAndExplicitRetrySucceeds(string locked)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var calls = 0;
        var provider = new CourtCheckProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (++calls > 1) return;
            await other.Database.BeginTransactionAsync();
            if (locked == "actor") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
            else if (locked == "case") await other.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Занят"));
            else await other.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        });
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var refused = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain("private-source", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        await other.Database.RollbackTransactionAsync();
        using var retry = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtCheck_ExistingPostgresSqlFailureRollsBackErrorRecord(bool cancel)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        var fault = new CourtCheckAfterSqlFault(cancel);
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(lease.ConnectionString).AddInterceptors(fault).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var before = await CourtCheckStateAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        var notifications = NewCourtCheckNotifications(db);
        var controller = CreateController(db, user, courtSyncProviderRegistry: new CourtCheckProvider(() => Task.CompletedTask), notificationService: notifications);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else await Assert.ThrowsAsync<CourtCheckSqlFault>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        Assert.True(fault.Called);
        Assert.NotEqual(before, fault.Executed);
        Assert.Equal(before, await CourtCheckStateAsync(db));
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCheckHttp_ExistingPostgresLateApprovalRevocationCannotSaveError()
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var before = await CourtCheckStateAsync(db);
        var provider = new CourtCheckProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await using var writer = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
            await ChangeCourtSearchAccessAsync(writer, user, clientCase.Id, "unapproved");
        });
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await CourtCheckStateAsync(db));
        Assert.Equal(0, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtCheckHttp_PreservesConcurrentHistoryAndExistingTaskAndPaymentLinks()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var task = new TaskJournalItem
        {
            Title = "Сохранённая задача",
            ObjectName = "Дело",
            ClientCaseId = clientCase.Id,
            ClientId = clientCase.ClientId,
            AuthorUserId = user.Id,
            AssigneeUserId = user.Id,
            DueAt = DateTime.UtcNow.AddDays(2),
            ContractorPaymentAmount = 120.50m,
        };
        var payment = new Payment
        {
            ClientId = clientCase.ClientId,
            ClientCaseId = clientCase.Id,
            Amount = 421.23m,
            Account = "synthetic-check-42",
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            Date = DateTime.UtcNow,
            PlannedDate = DateTime.UtcNow,
        };
        db.TaskJournalItems.Add(task);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var provider = new CourtCheckProvider(async () =>
        {
            await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => row.EventHistory, "История другого сотрудника\n").SetProperty(row => row.Title, "Изменено параллельно"));
        });
        var notifications = NewCourtCheckNotifications(db);
        using var server = CourtCheckServer(db, user, provider, notifications);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Collection(actual.EventHistory.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Equal("История другого сотрудника", line),
            line => Assert.Contains("private-source-error", line));
        Assert.Equal("Изменено параллельно", actual.Title);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        var actualTask = await db.TaskJournalItems.AsNoTracking().SingleAsync();
        Assert.Equal(task.Id, actualTask.Id);
        Assert.Equal(clientCase.Id, actualTask.ClientCaseId);
        Assert.Equal(clientCase.ClientId, actualTask.ClientId);
        Assert.Equal(120.50m, actualTask.ContractorPaymentAmount);
        Assert.Equal("Сохранённая задача", actualTask.Title);
        var actualPayment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(payment.Id, actualPayment.Id);
        Assert.Equal(clientCase.Id, actualPayment.ClientCaseId);
        Assert.Equal(clientCase.ClientId, actualPayment.ClientId);
        Assert.Equal(421.23m, actualPayment.Amount);
        Assert.Equal(0m, actualPayment.PaidAmount);
        Assert.Equal(PaymentStatus.Pending, actualPayment.Status);
        Assert.Equal("synthetic-check-42", actualPayment.Account);
        Assert.Equal(1, ((CourtCheckNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task PrepareCourtCheckModeAsync(PaymentContext db, int id, string mode)
    {
        var query = db.ClientCases.Where(row => row.Id == id);
        if (mode == "binding-required") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "").SetProperty(row => row.CourtCaseNumber, "").SetProperty(row => row.JudicialUid, ""));
        else if (mode == "number-only") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, ""));
        else if (mode == "unsupported") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://unsupported.test/card"));
        db.ChangeTracker.Clear();
    }

    private static async Task<string> CourtCheckStateAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Cases = await db.ClientCases.AsNoTracking().Select(row => new
        {
            row.Id,
            row.Title,
            row.CourtName,
            row.CourtUrl,
            row.CourtCaseNumber,
            row.JudicialUid,
            row.CourtClaimAmount,
            row.CourtSyncLastAttemptAt,
            row.CourtSyncLastSuccessAt,
            row.CourtSyncFailureStartedAt,
            row.CourtSyncLastError,
            row.EventHistory
        }).ToArrayAsync(),
        Notifications = await db.UserNotifications.AsNoTracking().Select(row => new { row.Id, row.Type, row.EventKey }).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.CountAsync(),
        Payments = await db.Payments.CountAsync(),
    });

    private static INotificationService NewCourtCheckNotifications(PaymentContext db)
    {
        var service = DispatchProxy.Create<INotificationService, CourtCheckNotifications>();
        ((CourtCheckNotifications)(object)service).Db = db;
        return service;
    }

    public class CourtCheckNotifications : DispatchProxy
    {
        public PaymentContext Db { get; set; } = null!;
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Null(Db.Database.CurrentTransaction);
            Assert.NotEmpty(Db.ClientCases.AsNoTracking().Single().EventHistory);
            Calls++;
            if (Throw) throw new CourtCheckNotificationFault();
            Assert.Contains(method!.Name, new[] { nameof(INotificationService.NotifyCourtSyncFailedAsync), nameof(INotificationService.NotifyCourtSyncStalledAsync) });
            return Task.CompletedTask;
        }
    }

    private sealed class CourtCheckNotificationFault : Exception;
    private sealed class CourtCheckSqlFault : Exception;
    private sealed class CourtCheckAfterSqlFault(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action Cancel { get; set; } = null!;
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            fail = Armed && data.Context!.ChangeTracker.Entries<ClientCase>().Any(entry => entry.State == EntityState.Modified);
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!fail) return result;
            Called = true;
            Executed = await CourtCheckStateAsync((PaymentContext)data.Context!);
            if (cancel) { Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new CourtCheckSqlFault();
        }
    }

    private sealed class CourtCheckProvider(Func<Task> whileWaiting) : ICourtSyncProviderRegistry, ICourtSyncCaseReader
    {
        public int Calls { get; private set; }
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
            Calls++;
            await whileWaiting();
            throw new SudrfCourtSyncException("private-source-error");
        }
    }

    private static TestServer CourtCheckServer(PaymentContext db, User user, ICourtSyncProviderRegistry provider,
        INotificationService notifications, Func<Task>? afterAuthentication = null)
        => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IDbContextFactory<PaymentContext>>(new CourtSearchContextFactory(db));
                services.AddAuthentication("court-check-fixture").AddScheme<AuthenticationSchemeOptions, CourtSearchAuthentication>("court-check-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly);
                services.AddSingleton<IControllerActivator>(new CourtCheckControllerActivator(db, user, provider, notifications));
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication();
                if (afterAuthentication is not null) app.Use(async (context, next) => { await afterAuthentication(); await next(); });
                app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

    private sealed class CourtCheckControllerActivator(PaymentContext db, User user, ICourtSyncProviderRegistry provider,
        INotificationService notifications) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            var controller = CreateController(db, user, courtSyncProviderRegistry: provider, notificationService: notifications);
            controller.ControllerContext = context;
            return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }
}

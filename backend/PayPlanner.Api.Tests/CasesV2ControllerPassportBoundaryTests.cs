using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task<PaymentContext> PassportBoundaryContextAsync(params IInterceptor[] hooks)
    {
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").AddInterceptors(hooks).Options);
        try { await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db; }
        catch { await db.DisposeAsync(); throw; }
    }

    private static async Task<(User Actor, int[] Cases, int Client, int? Passport)> SeedPassportBoundaryAsync(PaymentContext db, bool exists)
    {
        var (actor, cases, _) = await SeedBirthdayClientAsync(db);
        var client = new Client { Name = "Паспортный профиль", ResponsibleUserId = actor.Id }; db.Clients.Add(client); await db.SaveChangesAsync();
        ClientPassport? passport = null;
        if (exists) { passport = new ClientPassport { ClientId = client.Id, Series = "OLD", Number = "000111", IsPrimary = true, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc) }; db.ClientPassports.Add(passport); await db.SaveChangesAsync(); }
        return (actor, cases, client.Id, passport?.Id);
    }

    private static UpsertClientPassportRequest PassportBoundaryInput() => new()
    {
        Series = " NEW ",
        Number = " 999999 ",
        IssuedBy = " Синтетический отдел ",
        IssuedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        DepartmentCode = " 540-001 ",
        BirthPlacePassport = " Тестовый город ",
        BirthPlaceFias = " TEST-FIAS ",
        ValidFrom = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        ValidTo = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ClientPassportController PassportBoundaryController(PaymentContext db, User actor, bool authenticated = true)
    {
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id);
        if (!authenticated) root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(root.User.Claims));
        return new ClientPassportController(db) { ControllerContext = root.ControllerContext };
    }

    public static IEnumerable<object[]> PassportBoundaryAdmissionCases()
    {
        foreach (var write in new[] { false, true })
            foreach (var state in new[] { "unapproved", "inactive", "missing", "denied-view", "no-section", "unauthenticated", "stale-admin" }) yield return new object[] { write, state };
        yield return new object[] { true, "denied-edit" };
    }

    [Theory]
    [MemberData(nameof(PassportBoundaryAdmissionCases))]
    public async Task PassportBoundary_RequiresFreshApprovedActorAndCurrentAction(bool write, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, true); var before = await ClientBatchSnapshotAsync(db);
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state is "denied-view" or "stale-admin") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state == "denied-edit") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        if (state == "no-section") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteDeleteAsync();
        var controller = PassportBoundaryController(db, actor, state != "unauthenticated"); if (state == "stale-admin") ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        var result = write ? await controller.Upsert(client, PassportBoundaryInput(), default) : await controller.Get(client, default);
        Assert.True(result is UnauthorizedResult or ForbidResult, $"write={write}; state={state}; result={result.GetType().Name}"); Assert.Equal(before, await ClientBatchSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassportBoundary_EmptyUserDatabaseNeverGrantsAnonymousBootstrap(bool write)
    {
        await using var db = await PassportBoundaryContextAsync(); var client = new Client { Name = "Без сотрудника" }; db.Clients.Add(client); await db.SaveChangesAsync();
        var controller = new ClientPassportController(db); var result = write ? await controller.Upsert(client.Id, PassportBoundaryInput(), default) : await controller.Get(client.Id, default);
        Assert.IsType<UnauthorizedResult>(result); Assert.Empty(await db.ClientPassports.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PassportBoundary_MissingOrHiddenClientKeepsAllProfiles(bool write, bool hidden)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, true);
        var other = await CreateUserAsync(db, "passport-hidden@example.local", "Другой сотрудник"); await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        if (hidden) await db.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        var before = await ClientBatchSnapshotAsync(db); var controller = PassportBoundaryController(db, actor); var id = hidden ? client : int.MaxValue;
        Assert.IsType<NotFoundResult>(write ? await controller.Upsert(id, PassportBoundaryInput(), default) : await controller.Get(id, default)); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassportBoundary_LeavesForeignTransactionOrPendingWorkUntouched(bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, true); var before = await ClientBatchSnapshotAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужой результат";
        Assert.IsType<ConflictObjectResult>(await PassportBoundaryController(db, actor).Upsert(client, PassportBoundaryInput(), default)); Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужой результат", task.Result); }
        Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    public static IEnumerable<object[]> PassportBoundarySuccessCases()
        => from postgres in new[] { false, true } from exists in new[] { false, true } select new object[] { postgres, exists };

    [Theory]
    [MemberData(nameof(PassportBoundarySuccessCases))]
    public async Task PassportBoundary_KeepsPrimaryIdentityNormalizesAndReturnsOnlyAllowedFields(bool postgres, bool exists)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var (actor, cases, client, originalId) = await SeedPassportBoundaryAsync(db, exists); var input = PassportBoundaryInput(); var start = DateTime.UtcNow;
        var reply = Assert.IsType<ClientPassportResponse>(Assert.IsType<OkObjectResult>(await PassportBoundaryController(db, actor).Upsert(client, input, default)).Value);
        var saved = await db.ClientPassports.AsNoTracking().SingleAsync(row => row.ClientId == client);
        Assert.Equal(exists ? originalId : saved.Id, reply.Id); Assert.Equal(client, reply.ClientId); Assert.Equal("NEW", reply.Series); Assert.Equal("999999", saved.Number); Assert.Equal("Синтетический отдел", reply.IssuedBy); Assert.Equal(input.ValidFrom, reply.ValidFrom); Assert.Equal(input.ValidTo, reply.ValidTo); Assert.True(saved.IsPrimary); Assert.True(saved.UpdatedAt >= start);
        if (exists) Assert.Equal(new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc), saved.CreatedAt);
        PassportWireTestSupport.AssertSafe(reply); Assert.IsType<OkObjectResult>(await PassportBoundaryController(db, actor).Get(client, default)); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Fact]
    public async Task PassportBoundary_LegacyDefaultsKeepOwnProfileWithoutBypassingConfiguredDenials()
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, false); await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        Assert.IsType<OkObjectResult>(await PassportBoundaryController(db, actor).Upsert(client, PassportBoundaryInput(), default)); Assert.IsType<OkObjectResult>(await PassportBoundaryController(db, actor).Get(client, default));
    }

    public static IEnumerable<object[]> PassportBoundaryFailureCases()
        => from postgres in new[] { false, true } from exists in new[] { false, true } from cancel in new[] { false, true } select new object[] { postgres, exists, cancel };

    [Theory]
    [MemberData(nameof(PassportBoundaryFailureCases))]
    public async Task PassportBoundary_RollsBackActualPassportWriteOnFailureOrCancellation(bool postgres, bool exists, bool cancel)
    {
        var fault = new PassportBoundaryAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault);
        var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, exists); var before = await ClientBatchSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => PassportBoundaryController(db, actor).Upsert(client, PassportBoundaryInput(), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<PassportBoundarySqlFault>(Write);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassportBoundary_DualWriteKeepsEncryptedStorageAndSafeReply(bool exists)
    {
        await using var db = await PassportWireTestSupport.ContextAsync(true); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, exists);
        var reply = Assert.IsType<OkObjectResult>(await PassportBoundaryController(db, actor).Upsert(client, PassportBoundaryInput(), default)); PassportWireTestSupport.AssertSafe(reply.Value);
        var saved = await db.ClientPassports.AsNoTracking().SingleAsync(row => row.ClientId == client); Assert.All(PassportWireTestSupport.CryptoNames, name => Assert.NotNull(PassportWireTestSupport.Property(name).GetValue(saved)));
        Assert.Equal("NEW", await PassportWireTestSupport.Encryptor().DecryptAsync(saved.SeriesEncrypted!, $"ClientPassport:Series:{saved.Id}", default)); Assert.Equal("999999", await PassportWireTestSupport.Encryptor().DecryptAsync(saved.NumberEncrypted!, $"ClientPassport:Number:{saved.Id}", default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PassportBoundary_EncryptedStageFailureRollsBackPlainAndCipherValues(bool exists, bool cancel)
    {
        var fault = new PassportBoundaryAfterSql(cancel) { OnlyEncrypted = true };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FieldEncryption:Enabled"] = "true", ["FieldEncryption:ClientPassportDualWriteEnabled"] = "true" }).Build();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite("Data Source=:memory:").AddInterceptors(fault).Options, clientPassportEncryption: new ClientPassportEncryptionDualWriteService(PassportWireTestSupport.Encryptor(), configuration));
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, exists);
        async Task<string> CipherSnapshot() => JsonSerializer.Serialize((await db.ClientPassports.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()).Select(row => new { row.Id, Values = PassportWireTestSupport.CryptoNames.Select(name => PassportWireTestSupport.Property(name).GetValue(row)).ToArray() }));
        var before = await ClientBatchSnapshotAsync(db); var cipherBefore = await CipherSnapshot(); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel;
        Task Write() => PassportBoundaryController(db, actor).Upsert(client, PassportBoundaryInput(), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<PassportBoundarySqlFault>(Write);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await ClientBatchSnapshotAsync(db)); Assert.Equal(cipherBefore, await CipherSnapshot());
    }

    public static IEnumerable<object[]> PassportBoundaryHttpCases()
    {
        foreach (var write in new[] { false, true }) foreach (var state in new[] { "unapproved", "denied-view" }) yield return new object[] { write, state };
        yield return new object[] { true, "denied-edit" }; yield return new object[] { true, "after-filter" };
    }

    [Theory]
    [MemberData(nameof(PassportBoundaryHttpCases))]
    public async Task PassportBoundary_HttpRechecksApprovalAndEditAfterAttributeFilters(bool write, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(db, true);
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state == "denied-view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state == "denied-edit") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var before = await ClientBatchSnapshotAsync(db); using var server = PassportBoundaryServer(db, state == "after-filter" ? async () => { await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false)); } : null); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = write ? await http.PutAsJsonAsync($"/api/clients/{client}/passport", PassportBoundaryInput()) : await http.GetAsync($"/api/clients/{client}/passport"); Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"state={state}; write={write}; HTTP={(int)response.StatusCode}"); Assert.Equal(before, await ClientBatchSnapshotAsync(db));
    }

    [Fact]
    public async Task PassportBoundary_HttpIgnoresForeignIdentityCryptoAndServerStampFields()
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, id) = await SeedPassportBoundaryAsync(db, true); var oldOther = await db.ClientPassports.AsNoTracking().SingleAsync(row => row.ClientId != client);
        var input = PassportWireTestSupport.Input(); input["id"] = oldOther.Id; input["clientId"] = oldOther.ClientId; input["isPrimary"] = false; input["createdAt"] = "1900-01-01T00:00:00Z";
        using var server = PassportBoundaryServer(db); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await http.PutAsJsonAsync($"/api/clients/{client}/passport", input); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(id, json.RootElement.GetProperty("id").GetInt32()); Assert.True(json.RootElement.GetProperty("isPrimary").GetBoolean()); PassportWireTestSupport.AssertSafe(json.RootElement);
        Assert.Equal("000111", await db.ClientPassports.Where(row => row.Id == oldOther.Id).Select(row => row.Number).SingleAsync()); Assert.Equal(2, await db.ClientPassports.CountAsync());
    }

    private static TestServer PassportBoundaryServer(PaymentContext db, Func<Task>? afterPermissions = null) => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services => { services.AddLogging(); services.AddSingleton(db); services.AddAuthentication("write-fixture").AddScheme<AuthenticationSchemeOptions, ConfirmationAuthentication>("write-fixture", _ => { }); services.AddAuthorization(); services.AddControllers(options => { if (afterPermissions is not null) options.Filters.Add(new PassportPermissionChange(afterPermissions)); }).AddApplicationPart(typeof(ClientPassportController).Assembly); })
        .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class PassportPermissionChange(Func<Task> change) : IAsyncActionFilter, IOrderedFilter
    {
        public int Order => 100;
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next) { await change(); await next(); }
    }

    private sealed class PassportBoundarySqlFault : Exception;
    private sealed class PassportBoundaryAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool OnlyEncrypted { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { fail = Armed && eventData.Context!.ChangeTracker.Entries<ClientPassport>().Any(entry => entry.State is EntityState.Added or EntityState.Modified && (!OnlyEncrypted || PassportWireTestSupport.CryptoNames.Any(name => entry.Property(name).IsModified && entry.Property(name).CurrentValue is not null))); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) { if (!fail) return result; Called = true; Executed = await ClientBatchSnapshotAsync((PaymentContext)eventData.Context!); if (cancel) { Cancel!(); cancellationToken.ThrowIfCancellationRequested(); } throw new PassportBoundarySqlFault(); }
    }
}

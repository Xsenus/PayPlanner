using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static Task<IActionResult> UpdateAtomicClientAsync(PaymentContext db, User actor, int id, Client input, int route,
        CancellationToken ct = default, bool authenticated = true, bool staleAdmin = false)
    {
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id);
        if (!authenticated) root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(root.User.Claims));
        if (staleAdmin) ((ClaimsIdentity)root.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return route switch
        {
            0 => root.Update(id, input, ct),
            1 => new ClientsV1Controller(db) { ControllerContext = root.ControllerContext }.Update(id, input, ct),
            _ => new ClientsV2Controller(db) { ControllerContext = root.ControllerContext }.Update(id, input, ct),
        };
    }

    private static Client AtomicUpdateInput(int responsible) => new()
    {
        Name = "Изменённое имя",
        Email = "updated@example.local",
        Phone = "+79990001122",
        ResponsibleUserId = responsible,
        Notes = BirthdayProfilePrefix + "{\"birthDate\":\"1990-01-02\",\"registrationAddress\":\"Новый адрес\",\"passportSeries\":\"NEW\",\"passportNumber\":\"999999\"}",
    };

    private static async Task AssertAtomicClientOriginalAsync(PaymentContext db, int clientId)
    {
        var old = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == clientId);
        Assert.Equal("Синтетический клиент", old.Name); Assert.Equal(OldClientBirthday, old.BirthDate); Assert.Equal(BirthdayNotes("1980-05-12"), old.Notes);
        Assert.Null(old.LegalEntityId); Assert.Null(old.ClientStatusId);
        Assert.Equal("Прежний адрес", await db.AddressRecords.Select(row => row.Value).SingleAsync());
        Assert.Equal("000111", await db.ClientPassports.Select(row => row.Number).SingleAsync());
    }

    public static IEnumerable<object[]> ClientUpdateAdmissionCases()
        => from route in new[] { 0, 1, 2 } from state in new[] { "unapproved", "inactive", "missing", "denied-view", "denied-edit", "unauthenticated", "stale-admin" } select new object[] { route, state };

    [Theory]
    [MemberData(nameof(ClientUpdateAdmissionCases))]
    public async Task ClientUpdateAtomic_RequiresFreshApprovedActorAndCurrentViewEdit(int route, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state == "denied-view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state is "denied-edit" or "stale-admin") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var result = await UpdateAtomicClientAsync(db, actor, client, AtomicUpdateInput(actor.Id), route, authenticated: state != "unauthenticated", staleAdmin: state == "stale-admin");
        Assert.True(result is UnauthorizedResult or ForbidResult, $"state={state}; result={result.GetType().Name}");
        await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientUpdateAtomic_PreservesForeignTransactionAndPendingWork(int route, bool pending)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужие изменения";
        var result = await UpdateAtomicClientAsync(db, actor, client, AtomicUpdateInput(actor.Id), route);
        Assert.IsType<ConflictObjectResult>(result); Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужие изменения", task.Result); }
        await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientUpdateLinkCases()
        => from route in new[] { 0, 1, 2 } from kind in new[] { "hidden-legal", "missing-legal", "zero-legal", "missing-status", "zero-status" } select new object[] { route, kind };

    [Theory]
    [MemberData(nameof(ClientUpdateLinkCases))]
    public async Task ClientUpdateAtomic_RejectsUnavailableScalarLinksWithoutChangingProfile(int route, string kind)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        var other = await CreateUserAsync(db, "update-hidden@example.local", "Другой сотрудник");
        var hidden = new LegalEntity { ShortName = "Скрытая организация", ResponsibleUserId = other.Id }; db.LegalEntities.Add(hidden); await db.SaveChangesAsync();
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        var input = AtomicUpdateInput(actor.Id);
        if (kind.EndsWith("legal")) input.LegalEntityId = kind == "hidden-legal" ? hidden.Id : kind == "zero-legal" ? 0 : int.MaxValue;
        else input.ClientStatusId = kind == "zero-status" ? 0 : int.MaxValue;
        IActionResult? result = null; Exception? error = null;
        try { result = await UpdateAtomicClientAsync(db, actor, client, input, route); } catch (Exception caught) { error = caught; }
        Assert.True(result is BadRequestObjectResult && error is null, $"link={kind}; result={result?.GetType().Name}; error={error?.GetType().Name}");
        await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> ClientUpdateFailureCases()
        => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { route, postgres, cancel };

    [Theory]
    [MemberData(nameof(ClientUpdateFailureCases))]
    public async Task ClientUpdateAtomic_RollsBackExecutedClientAndProfileWrite(int route, bool postgres, bool cancel)
    {
        var fault = new ClientUpdateAfterSqlFailure(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, ids, client) = await SeedBirthdayClientAsync(db); using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => UpdateAtomicClientAsync(db, actor, client, AtomicUpdateInput(actor.Id), route, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ClientUpdateSqlFault>(Write);
        Assert.True(fault.Called); Assert.Equal("Изменённое имя", fault.NameAtFault);
        if (route == 0) { Assert.Equal("Новый адрес", fault.AddressAtFault); Assert.Equal("999999", fault.PassportAtFault); }
        Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientUpdatePositiveCases()
        => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from admin in new[] { false, true } select new object[] { route, postgres, admin };

    [Theory]
    [MemberData(nameof(ClientUpdatePositiveCases))]
    public async Task ClientUpdateAtomic_KeepsValidAssignmentInactiveStatusAndProfileRules(int route, bool postgres, bool admin)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids, client) = await SeedBirthdayClientAsync(db); var other = await CreateUserAsync(db, "update-responsible@example.local", "Новый ответственный");
        if (admin) await db.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var legal = new LegalEntity { ShortName = "Доступная организация", ResponsibleUserId = actor.Id }; var status = new ClientStatusEntity { Name = "Существующий неактивный", IsActive = false }; db.AddRange(legal, status); await db.SaveChangesAsync();
        var input = AtomicUpdateInput(other.Id); input.LegalEntityId = legal.Id; input.ClientStatusId = status.Id;
        var reply = Assert.IsType<Client>(Assert.IsType<OkObjectResult>(await UpdateAtomicClientAsync(db, actor, client, input, route)).Value);
        var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == client);
        Assert.Equal(admin ? other.Id : actor.Id, saved.ResponsibleUserId); Assert.Equal(saved.ResponsibleUserId, reply.ResponsibleUserId); Assert.Equal(legal.Id, saved.LegalEntityId); Assert.Equal(status.Id, saved.ClientStatusId);
        Assert.Equal(input.Name, saved.Name); Assert.Equal(input.Notes, saved.Notes); Assert.Equal(new DateTime(1990, 1, 2), saved.BirthDate);
        Assert.Equal(route == 0 ? "Новый адрес" : "Прежний адрес", await db.AddressRecords.Select(row => row.Value).SingleAsync());
        Assert.Equal(route == 0 ? "999999" : "000111", await db.ClientPassports.Select(row => row.Number).SingleAsync());
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientUpdateResponsibleCases()
        => from route in new[] { 0, 1, 2 } from state in new[] { "zero", "missing", "inactive", "null", "legacy" } select new object[] { route, state };

    [Theory]
    [MemberData(nameof(ClientUpdateResponsibleCases))]
    public async Task ClientUpdateAtomic_ValidatesAdminAssigneeAndKeepsLegacyDefaults(int route, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        var other = await CreateUserAsync(db, "update-choice@example.local", "Другой сотрудник");
        if (state == "legacy") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        else await db.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        if (state == "inactive") await db.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var input = AtomicUpdateInput(state == "zero" ? 0 : state == "missing" ? int.MaxValue : other.Id); if (state == "null") input.ResponsibleUserId = null;
        var result = await UpdateAtomicClientAsync(db, actor, client, input, route);
        if (state is "null" or "legacy")
        {
            Assert.IsType<OkObjectResult>(result); Assert.Equal(state == "null" ? (int?)null : actor.Id, await db.Clients.Select(row => row.ResponsibleUserId).SingleAsync());
        }
        else { Assert.IsType<BadRequestObjectResult>(result); await AssertAtomicClientOriginalAsync(db, client); }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientUpdateAtomic_HttpUsesCurrentApprovalAndEditPermission(int route, bool denied)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        if (denied) await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        else await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        using var server = CaseWriteServer(db, actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        var path = route == 0 ? "/api/clients" : $"/api/v{route}/clients";
        using var response = await http.PutAsJsonAsync($"{path}/{client}", AtomicUpdateInput(actor.Id));
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP={(int)response.StatusCode}");
        await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientUpdateAtomic_DoesNotRevealOrModifyMissingAndHiddenClients(int route, bool hidden)
    {
        await using var db = await CreateContextAsync(); var (actor, ids, client) = await SeedBirthdayClientAsync(db);
        var other = await CreateUserAsync(db, "update-invisible@example.local", "Другой сотрудник");
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        if (hidden) await db.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        Assert.IsType<NotFoundResult>(await UpdateAtomicClientAsync(db, actor, hidden ? client : int.MaxValue, AtomicUpdateInput(actor.Id), route));
        await AssertAtomicClientOriginalAsync(db, client); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    private sealed class ClientUpdateSqlFault : Exception;

    private sealed class ClientUpdateAfterSqlFailure(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? NameAtFault { get; private set; }
        public string? AddressAtFault { get; private set; }
        public string? PassportAtFault { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            fail = Armed && eventData.Context!.ChangeTracker.Entries<Client>().Any(entry => entry.State == EntityState.Modified);
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!fail) return result;
            Called = true; var db = (PaymentContext)eventData.Context!; NameAtFault = await db.Clients.AsNoTracking().Select(row => row.Name).SingleAsync(CancellationToken.None);
            AddressAtFault = await db.AddressRecords.Select(row => row.Value).SingleAsync(CancellationToken.None); PassportAtFault = await db.ClientPassports.Select(row => row.Number).SingleAsync(CancellationToken.None);
            if (cancel) { Cancel!(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new ClientUpdateSqlFault();
        }
    }
}

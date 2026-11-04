using System.Security.Claims;
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
    private static Task<IActionResult> CreateAtomicClientAsync(PaymentContext db, User actor, Client input, int route,
        CancellationToken ct = default, bool staleAdmin = false, bool authenticated = true)
    {
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id);
        if (staleAdmin) ((ClaimsIdentity)root.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        if (!authenticated) root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(root.User.Claims));
        return route switch
        {
            0 => root.Create(input, ct),
            1 => new ClientsV1Controller(db) { ControllerContext = root.ControllerContext }.Create(input, ct),
            _ => new ClientsV2Controller(db) { ControllerContext = root.ControllerContext }.Create(input, ct),
        };
    }

    public static IEnumerable<object[]> ClientCreateAdmissionCases()
        => from route in new[] { 0, 1, 2 }
           from state in new[] { "inactive", "unapproved", "missing", "denied-view", "denied-create", "stale-admin", "unauthenticated" }
           select new object[] { route, state };

    [Theory]
    [MemberData(nameof(ClientCreateAdmissionCases))]
    public async Task ClientCreateAtomic_RequiresCurrentApprovedActorAndViewCreate(int route, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db);
        await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state is "denied-create" or "stale-admin") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false));
        if (state == "denied-view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state == "missing") actor = new User { Id = 987654 };
        var result = await CreateAtomicClientAsync(db, actor, new Client { Name = "Нет действующего допуска" }, route, staleAdmin: state == "stale-admin", authenticated: state != "unauthenticated");
        var count = await db.Clients.CountAsync();
        Assert.True(result is UnauthorizedResult or ForbidResult && count == 1, $"state={state}; result={result.GetType().Name}; clients={count}");
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientCreateAtomic_PreservesForeignTransactionAndPendingWork(int route, bool pending)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужой несохранённый результат";
        IActionResult? result = null; Exception? error = null;
        try { result = await CreateAtomicClientAsync(db, actor, new Client { Name = "Не сохранять чужую работу" }, route); } catch (Exception caught) { error = caught; }
        Assert.True(result is ConflictObjectResult, $"result={result?.GetType().Name}; error={error?.GetType().Name}; clients={await db.Clients.CountAsync()}");
        Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Сохранённый результат", (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientCreateFailureCases()
        => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { route, postgres, cancel };

    [Theory]
    [MemberData(nameof(ClientCreateFailureCases))]
    public async Task ClientCreateAtomic_RollsBackActualInsertOnFailureAndCancellation(int route, bool postgres, bool cancel)
    {
        var fault = new ClientCreateAfterSqlFailure(cancel, profileOnly: false); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => CreateAtomicClientAsync(db, actor, new Client { Name = "Полный откат клиента" }, route, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ClientCreateSqlFault>(Write);
        Assert.True(fault.Called); Assert.Equal(2, fault.ClientsAtFault); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Clients.CountAsync()); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientCreateAtomic_RootProfileRollsBackClientAddressAndPassportTogether(bool postgres, bool cancel)
    {
        var fault = new ClientCreateAfterSqlFailure(cancel, profileOnly: true); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => CreateAtomicClientAsync(db, actor, ClientCreateProfile(0), 0, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ClientCreateSqlFault>(Write);
        Assert.True(fault.Called); Assert.Equal(2, fault.ClientsAtFault); Assert.Equal(2, fault.ProfileRowsAtFault); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear();
        Assert.True(await db.Clients.CountAsync() == 1 && !await db.AddressRecords.AnyAsync() && !await db.ClientPassports.AnyAsync(), $"clients={await db.Clients.CountAsync()}; addresses={await db.AddressRecords.CountAsync()}; passports={await db.ClientPassports.CountAsync()}");
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientCreateLinkCases()
        => from route in new[] { 0, 1, 2 } from link in new[] { "legal-hidden", "legal-missing", "legal-zero", "legal-negative", "status-missing", "status-zero", "status-negative" } select new object[] { route, link };

    [Theory]
    [MemberData(nameof(ClientCreateLinkCases))]
    public async Task ClientCreateAtomic_RejectsUnknownAndInvisibleScalarLinksBeforeInsert(int route, string link)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients, viewScope: DataVisibilityScope.Own);
        var other = await CreateUserAsync(db, "client-atomic-hidden@example.local", "Другой сотрудник"); var hidden = new LegalEntity { ShortName = "Недоступная организация", ResponsibleUserId = other.Id }; db.LegalEntities.Add(hidden); await db.SaveChangesAsync();
        var input = new Client { Name = "Не связывать недоступное" }; var value = link.EndsWith("zero", StringComparison.Ordinal) ? 0 : link.EndsWith("negative", StringComparison.Ordinal) ? -987654 : 987654;
        if (link.StartsWith("legal", StringComparison.Ordinal)) input.LegalEntityId = link == "legal-hidden" ? hidden.Id : value; else input.ClientStatusId = value;
        IActionResult? result = null; Exception? error = null;
        try { result = await CreateAtomicClientAsync(db, actor, input, route); } catch (Exception caught) { error = caught; }
        Assert.True(result is BadRequestObjectResult && await db.Clients.CountAsync() == 1, $"link={link}; result={result?.GetType().Name}; error={error?.GetType().Name}; clients={await db.Clients.CountAsync()}");
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    private static Client ClientCreateProfile(int route) => route == 0
        ? new Client { Name = "Новый составной профиль", Notes = "__PP_CLIENT_PROFILE_V2__:{\"registrationAddress\":\"Новый адрес\",\"passportSeries\":\"4510\",\"passportNumber\":\"123456\"}" }
        : new Client { Name = "Новый составной профиль", Addresses = [new AddressRecord { Value = "Новый адрес", IsPrimary = true }], Passports = [new ClientPassport { Series = "4510", Number = "123456", IsPrimary = true }] };

    public static IEnumerable<object[]> ClientCreatePositiveCases()
        => from route in new[] { 0, 1, 2 } from postgres in new[] { false, true } from admin in new[] { false, true } select new object[] { route, postgres, admin };

    [Theory]
    [MemberData(nameof(ClientCreatePositiveCases))]
    public async Task ClientCreateAtomic_StoresWholeProfileAndKeepsExistingAssignmentAndStatusRules(int route, bool postgres, bool admin)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, ids) = await SeedBulkCasesAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Clients, viewScope: DataVisibilityScope.Own);
        await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.LegalEntities);
        if (admin) await db.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var assignee = await CreateUserAsync(db, "client-atomic-assignee@example.local", "Ответственный"); var legal = new LegalEntity { ShortName = "Доступная организация", ResponsibleUserId = actor.Id }; var status = new ClientStatusEntity { Name = "Прежний допустимый статус", IsActive = false };
        db.AddRange(legal, status); await db.SaveChangesAsync(); var input = ClientCreateProfile(route); input.ResponsibleUserId = assignee.Id; input.LegalEntityId = legal.Id; input.ClientStatusId = status.Id;
        var result = Assert.IsType<CreatedResult>(await CreateAtomicClientAsync(db, actor, input, route)); var reply = Assert.IsType<Client>(result.Value); var saved = await db.Clients.AsNoTracking().SingleAsync(row => row.Id == reply.Id);
        Assert.Equal(admin ? assignee.Id : actor.Id, saved.ResponsibleUserId); Assert.Equal(legal.Id, saved.LegalEntityId); Assert.Equal(status.Id, saved.ClientStatusId); Assert.Equal(input.Name, saved.Name);
        Assert.Equal("Новый адрес", (await db.AddressRecords.AsNoTracking().SingleAsync()).Value); Assert.Equal("4510", (await db.ClientPassports.AsNoTracking().SingleAsync()).Series); Assert.Equal("123456", (await db.ClientPassports.AsNoTracking().SingleAsync()).Number);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges()); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientCreateInvalidAssigneeCases()
        => from route in new[] { 0, 1, 2 } from target in new[] { "zero", "negative", "missing", "inactive" } select new object[] { route, target };

    [Theory]
    [MemberData(nameof(ClientCreateInvalidAssigneeCases))]
    public async Task ClientCreateAtomic_RejectsAnInvalidOrDisabledExplicitAssignee(int route, string target)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db);
        await db.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var disabled = await CreateUserAsync(db, "client-disabled-assignee@example.local", "Отключённый сотрудник");
        await db.Users.Where(row => row.Id == disabled.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var requested = target switch { "zero" => 0, "negative" => -987654, "inactive" => disabled.Id, _ => 987654 };
        var result = await CreateAtomicClientAsync(db, actor, new Client { Name = "Недопустимый ответственный", ResponsibleUserId = requested }, route);
        Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(1, await db.Clients.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
        await AssertBulkCasesUnchangedAsync(db, ids);
    }

    public static IEnumerable<object[]> ClientCreateLegacyCases()
        => from route in new[] { 0, 1, 2 } from state in new[] { "allowed", "unapproved", "inactive", "hidden-legal", "configured-other-section", "nested-payment" } select new object[] { route, state };

    [Theory]
    [MemberData(nameof(ClientCreateLegacyCases))]
    public async Task ClientCreateAtomic_LegacyDefaultsKeepApprovalScopeAndConfiguredDenials(int route, string state)
    {
        await using var db = await CreateContextAsync(); var (actor, ids) = await SeedBulkCasesAsync(db);
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        if (state == "configured-other-section") await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Cases);
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        var other = await CreateUserAsync(db, "legacy-client-other@example.local", "Другой сотрудник");
        var legal = new LegalEntity { ShortName = "Организация legacy-роли", ResponsibleUserId = state == "hidden-legal" ? other.Id : actor.Id };
        db.LegalEntities.Add(legal); await db.SaveChangesAsync(); var input = ClientCreateProfile(route); input.LegalEntityId = legal.Id;
        if (state == "nested-payment") input.Payments.Add(new Payment { Amount = 999m });
        var result = await CreateAtomicClientAsync(db, actor, input, route);
        if (state == "allowed" || state == "nested-payment" && route == 0)
        {
            var saved = Assert.IsType<Client>(Assert.IsType<CreatedResult>(result).Value); Assert.Equal(actor.Id, saved.ResponsibleUserId); Assert.Equal(legal.Id, saved.LegalEntityId);
            Assert.Equal(2, await db.Clients.CountAsync()); Assert.Equal("Новый адрес", (await db.AddressRecords.SingleAsync()).Value); Assert.Equal("4510", (await db.ClientPassports.SingleAsync()).Series);
        }
        else
        {
            if (state is "hidden-legal" or "nested-payment") Assert.IsType<BadRequestObjectResult>(result);
            else Assert.True(result is UnauthorizedResult or ForbidResult, result.GetType().Name);
            Assert.Equal(1, await db.Clients.CountAsync()); Assert.Empty(await db.AddressRecords.ToArrayAsync()); Assert.Empty(await db.ClientPassports.ToArrayAsync());
        }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    private sealed class ClientCreateSqlFault : Exception;
    private sealed class ClientCreateAfterSqlFailure(bool cancel, bool profileOnly) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public int ClientsAtFault { get; private set; }
        public int ProfileRowsAtFault { get; private set; }
        public Action? Cancel { get; set; }
        private bool faultThisSave;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            faultThisSave = Armed && (profileOnly
                ? eventData.Context!.ChangeTracker.Entries<ClientPassport>().Any(entry => entry.State == EntityState.Added)
                : eventData.Context!.ChangeTracker.Entries<Client>().Any(entry => entry.State == EntityState.Added));
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!faultThisSave) return result;
            Called = true; var db = (PaymentContext)eventData.Context!; ClientsAtFault = await db.Clients.CountAsync(CancellationToken.None);
            ProfileRowsAtFault = await db.AddressRecords.CountAsync(CancellationToken.None) + await db.ClientPassports.CountAsync(CancellationToken.None);
            if (cancel) { Cancel!(); cancellationToken.ThrowIfCancellationRequested(); }
            throw new ClientCreateSqlFault();
        }
    }
}

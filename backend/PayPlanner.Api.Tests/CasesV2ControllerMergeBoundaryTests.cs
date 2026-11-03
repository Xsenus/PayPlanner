using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private sealed record MergeSeed(User Actor, string Kind, ContactMergeRequest Input);
    private static readonly string[] MergeKinds = ["client", "external", "legal", "client+external", "external+client", "legal+external", "external+legal"];
    private static async Task<MergeSeed> SeedMergeBoundaryAsync(PaymentContext db, string kind = "external")
    {
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.LegalEntities); var parts = kind.Split('+'); var first = parts[0]; var second = parts.Length == 1 ? first : parts[1];
        async Task<int> Add(string type, string name, string phone)
        {
            if (type == "client") { var row = new Client { Name = name, Phone = phone, ResponsibleUserId = actor.Id, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc), BirthDate = new DateTime(1980, 5, 12, 0, 0, 0, DateTimeKind.Utc) }; db.Clients.Add(row); await db.SaveChangesAsync(); return row.Id; }
            if (type == "legal") { var row = new LegalEntity { ShortName = name, Phone = phone, ResponsibleUserId = actor.Id, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc) }; db.LegalEntities.Add(row); await db.SaveChangesAsync(); return row.Id; }
            var contact = new ExternalContact { FullName = name, ContactType = kind.Contains("legal", StringComparison.Ordinal) ? "Organization" : "Person", Phone = phone, PhonesJson = JsonSerializer.Serialize(new[] { phone }), ResponsibleUserId = actor.Id, IsActive = true, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc) }; db.ExternalContacts.Add(contact); await db.SaveChangesAsync(); return contact.Id;
        }
        var primary = await Add(first, "Основная карточка", "79001110001"); var duplicate = await Add(second, "Дубль карточки", "79001110002"); db.ChangeTracker.Clear(); return new(actor, kind, new ContactMergeRequest { PrimarySourceType = first, PrimarySourceId = primary, DuplicateSourceType = second, DuplicateSourceId = duplicate });
    }
    private static async Task<IActionResult?> RunMergeAsync(PaymentContext db, MergeSeed seed, bool execute, CancellationToken ct = default, bool authenticated = true)
    {
        var controller = ClientContactController(db, seed.Actor, authenticated); return (execute ? await controller.MergeContacts(seed.Input, ct) : await controller.PreviewMerge(seed.Input, ct)).Result;
    }
    private static Task<int> MergeOwnerAsync(PaymentContext db, string kind, int id, int owner) => kind switch
    {
        "client" => db.Clients.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, owner)),
        "legal" => db.LegalEntities.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, owner)),
        _ => db.ExternalContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, owner)),
    };
    private static async Task<string> MergeSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await PartySnapshotAsync(db),
        ClientAddresses = await db.Clients.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Address }).ToArrayAsync(),
        LegalFields = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ShortName, x.FullName, x.Phone, x.Email, x.ContactPhonesJson, x.ContactEmailsJson, x.Inn, x.Kpp, x.Ogrn, x.Address, x.Director, x.ClientStatusId, x.IsClientAndSupplier, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(),
    });
    public static IEnumerable<object[]> MergeSuccessCases() => from kind in MergeKinds from execute in new[] { false, true } from postgres in new[] { false, true } select new object[] { kind, execute, postgres };
    [Theory]
    [MemberData(nameof(MergeSuccessCases))]
    public async Task MergeBoundary_PreservesAllSevenDirectionsAndPreview(string kind, bool execute, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); var before = await MergeSnapshotAsync(db); var reply = Assert.IsType<ContactsController.ContactMergePreview>(Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute)).Value); Assert.True(reply.CanMerge); Assert.Empty(reply.Conflicts); Assert.Equal("Основная карточка", reply.PrimaryName); Assert.Contains("79001110001", reply.Phones); Assert.Contains("79001110002", reply.Phones);
        if (!execute) Assert.Equal(before, await MergeSnapshotAsync(db));
        else
        {
            var id = seed.Input.DuplicateSourceId; var deleted = seed.Input.DuplicateSourceType == "client" ? !await db.Clients.AnyAsync(x => x.Id == id) : seed.Input.DuplicateSourceType == "legal" ? !await db.LegalEntities.AnyAsync(x => x.Id == id) : !await db.ExternalContacts.AnyAsync(x => x.Id == id); Assert.True(deleted); Assert.True(await db.ObjectAuditEvents.AnyAsync(x => x.ObjectType == "contact-merge" && x.ObjectId == seed.Input.PrimarySourceId));
            if (kind.StartsWith("external+", StringComparison.Ordinal)) { var contact = await db.ExternalContacts.AsNoTracking().SingleAsync(x => x.Id == seed.Input.PrimarySourceId); Assert.Equal(1945, contact.CreatedAt.Year); Assert.True(kind == "external+client" ? contact.LinkedClientId != id : contact.LinkedLegalEntityId != id); }
        }
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.All(await db.ClientCases.AsNoTracking().ToArrayAsync(), x => { Assert.Equal(123.45m, x.CourtClaimAmount); Assert.Equal("Исходная история", x.EventHistory); }); Assert.Equal("Сохранённый результат", await db.TaskJournalItems.Where(x => x.Status == "done").Select(x => x.Result).SingleAsync());
    }
    public static IEnumerable<object[]> MergeAdmissionCases() => from execute in new[] { false, true } from state in new[] { "unauthenticated", "unapproved", "inactive", "missing", "view", "edit", "delete" } select new object[] { execute, state };
    [Theory]
    [MemberData(nameof(MergeAdmissionCases))]
    public async Task MergeBoundary_RequiresCurrentApprovedActorAndIndividualActions(bool execute, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db);
        if (state == "unapproved") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "missing") seed = seed with { Actor = new User { Id = int.MaxValue } };
        if (state is "view" or "edit" or "delete") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, state != "view").SetProperty(x => x.CanEdit, state != "edit").SetProperty(x => x.CanDelete, state != "delete"));
        var before = await MergeSnapshotAsync(db); var result = await RunMergeAsync(db, seed, execute, authenticated: state != "unauthenticated"); Assert.True(result is UnauthorizedResult or ForbidResult, $"execute={execute}; state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    public static IEnumerable<object[]> MergeCreateCases() => MergeKinds.Select(kind => new object[] { kind });
    [Theory]
    [MemberData(nameof(MergeCreateCases))]
    public async Task MergeBoundary_RequiresCreateOnlyForQuickPrimaryReplacement(string kind)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false)); var before = await MergeSnapshotAsync(db); var result = await RunMergeAsync(db, seed, true); if (kind.StartsWith("external+", StringComparison.Ordinal)) { Assert.IsType<ForbidResult>(result); Assert.Equal(before, await MergeSnapshotAsync(db)); } else Assert.IsType<OkObjectResult>(result);
    }
    public static IEnumerable<object[]> MergeMixedCases() => from kind in new[] { "legal+external", "external+legal" } from execute in new[] { false, true } from section in new[] { RolePermissionCatalog.Clients, RolePermissionCatalog.LegalEntities } select new object[] { kind, execute, section };
    [Theory]
    [MemberData(nameof(MergeMixedCases))]
    public async Task MergeBoundary_MixedOrganizationRequiresBothSectionActions(string kind, bool execute, string section)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false).SetProperty(x => x.CanDelete, false)); var before = await MergeSnapshotAsync(db); Assert.IsType<ForbidResult>(await RunMergeAsync(db, seed, execute)); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    public static IEnumerable<object[]> MergeHiddenSourceCases() => from kind in MergeKinds from execute in new[] { false, true } select new object[] { kind, execute };
    [Theory]
    [MemberData(nameof(MergeHiddenSourceCases))]
    public async Task MergeBoundary_EachSourceUsesItsOwnSectionVisibility(string kind, bool execute)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db, kind); var other = await CreateUserAsync(db, "merge-hidden@example.local", "Другой владелец"); await MergeOwnerAsync(db, seed.Input.DuplicateSourceType, seed.Input.DuplicateSourceId, other.Id); var section = seed.Input.DuplicateSourceType == "legal" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients; await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); db.ChangeTracker.Clear(); var before = await MergeSnapshotAsync(db); Assert.IsType<NotFoundResult>(await RunMergeAsync(db, seed, execute)); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    private static async Task<(MergeSeed Seed, int Dependency, string Type)> SeedMergeDependencyAsync(PaymentContext db, string dependency, bool hidden)
    {
        var kind = dependency == "alias-client" ? "client+external" : dependency == "alias-legal" ? "legal+external" : dependency == "alias-duplicate" ? "legal" : dependency == "client-legal" ? "client" : "external";
        var seed = await SeedMergeBoundaryAsync(db, kind); var other = hidden ? await CreateUserAsync(db, "merge-dependency@example.local", "Владелец зависимости") : seed.Actor; int id; string type;
        if (dependency is "alias-client" or "alias-legal" or "alias-duplicate") { var row = new ExternalContact { FullName = "Закрытая связанная карточка", ResponsibleUserId = other.Id, LinkedClientId = dependency == "alias-client" ? seed.Input.PrimarySourceId : null, LinkedLegalEntityId = dependency == "alias-legal" ? seed.Input.PrimarySourceId : dependency == "alias-duplicate" ? seed.Input.DuplicateSourceId : null, ContactType = dependency == "alias-client" ? "Person" : "Organization", Phone = "79001110003" }; db.ExternalContacts.Add(row); await db.SaveChangesAsync(); id = row.Id; type = "external"; }
        else if (dependency == "linked-client") { var row = new Client { Name = "Закрытый полный клиент", ResponsibleUserId = other.Id }; db.Clients.Add(row); await db.SaveChangesAsync(); id = row.Id; type = "client"; await db.ExternalContacts.Where(x => x.Id == seed.Input.DuplicateSourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedClientId, row.Id)); }
        else if (dependency == "parent") { var row = new ExternalContact { FullName = "Закрытая родительская организация", ResponsibleUserId = other.Id, ContactType = "Organization" }; db.ExternalContacts.Add(row); await db.SaveChangesAsync(); id = row.Id; type = "external"; await db.ExternalContacts.Where(x => x.Id == seed.Input.DuplicateSourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ParentContactId, row.Id)); }
        else { var row = new LegalEntity { ShortName = "Закрытая зависимая организация", ResponsibleUserId = other.Id }; db.LegalEntities.Add(row); await db.SaveChangesAsync(); id = row.Id; type = "legal"; if (dependency == "client-legal") await db.Clients.Where(x => x.Id == seed.Input.DuplicateSourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LegalEntityId, row.Id)); else { await db.ExternalContacts.Where(x => x.Id == seed.Input.PrimarySourceId || x.Id == seed.Input.DuplicateSourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContactType, "Organization")); await db.ExternalContacts.Where(x => x.Id == seed.Input.DuplicateSourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedLegalEntityId, row.Id)); } }
        var section = type == "legal" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients; await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); if (type == "legal" && dependency != "alias-legal") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false).SetProperty(x => x.CanDelete, false)); db.ChangeTracker.Clear(); return (seed, id, type);
    }
    public static IEnumerable<object[]> MergeDependencyCases() => from dependency in new[] { "alias-client", "alias-legal", "alias-duplicate", "linked-client", "linked-legal", "parent", "client-legal" } from execute in new[] { false, true } select new object[] { dependency, execute };
    [Theory]
    [MemberData(nameof(MergeDependencyCases))]
    public async Task MergeBoundary_NeverReadsOrWritesHiddenAdditionalCard(string dependency, bool execute)
    {
        await using var db = await PassportBoundaryContextAsync(); var (seed, _, _) = await SeedMergeDependencyAsync(db, dependency, true); var before = await MergeSnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await RunMergeAsync(db, seed, execute)); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    [Theory]
    [InlineData("linked-client", false)]
    [InlineData("linked-client", true)]
    [InlineData("linked-legal", false)]
    [InlineData("linked-legal", true)]
    [InlineData("parent", false)]
    [InlineData("parent", true)]
    [InlineData("client-legal", false)]
    [InlineData("client-legal", true)]
    public async Task MergeBoundary_ReadOnlyVisibleDependencyDoesNotNeedEdit(string dependency, bool execute)
    {
        await using var db = await PassportBoundaryContextAsync(); var (seed, _, _) = await SeedMergeDependencyAsync(db, dependency, false); Assert.IsType<OkObjectResult>(await RunMergeAsync(db, seed, execute));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MergeBoundary_RejectsForeignTransactionAndPendingWork(bool execute, bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db); var before = await MergeSnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа"; IActionResult? result = null; Exception? fault = null; try { result = await RunMergeAsync(db, seed, execute); } catch (Exception e) { fault = e; }
        Assert.True(fault is null && result is ConflictObjectResult, $"execute={execute}; pending={pending}; result={result?.GetType().Name}; exception={fault?.GetType().Name}"); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    public static IEnumerable<object[]> MergeFailureCases() => from kind in MergeKinds from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { kind, postgres, cancel };
    [Theory]
    [MemberData(nameof(MergeFailureCases))]
    public async Task MergeBoundary_SecondSqlFailureRollsBackEveryMergeDirection(string kind, bool postgres, bool cancel)
    {
        var fault = new MergeAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault); var seed = await SeedMergeBoundaryAsync(db, kind); var before = await MergeSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true; Task Write() => RunMergeAsync(db, seed, true, cancellation.Token); if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<MergeSqlFault>(Write); Assert.Equal(2, fault.Calls); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MergeBoundary_HttpRechecksApprovalAndActionsAfterFilters(bool execute, bool approval)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedMergeBoundaryAsync(db); var before = await MergeSnapshotAsync(db); using var server = ClientContactServer(db, async () => { if (approval) await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); else await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await http.PostAsJsonAsync(execute ? "/api/contacts/merge" : "/api/contacts/merge/preview", seed.Input); Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP={(int)response.StatusCode}"); Assert.Equal(before, await MergeSnapshotAsync(db));
    }
    private sealed class MergeSqlFault : Exception;
    private sealed class MergeAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Calls { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool write;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { write = Armed && data.Context!.ChangeTracker.Entries().Any(x => x.Entity is Client or LegalEntity or ExternalContact && x.State is EntityState.Added or EntityState.Modified); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!write || ++Calls != 2) return result; Executed = await MergeSnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new MergeSqlFault(); }
    }
}

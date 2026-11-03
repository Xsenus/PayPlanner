using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private sealed record PartySeed(PromotionSeed Source, string Kind, int SourceId, int Case, int? Party);
    private static async Task<PartySeed> SeedPartyBoundaryAsync(PaymentContext db, string kind = "externalContact", int op = 0)
    {
        var source = await SeedClientPromotionAsync(db); var id = kind == "client" ? source.Client : kind == "legalEntity" ? source.Legal : source.Contact; ClientCaseRelatedParty? party = null;
        if (op != 0) { party = new ClientCaseRelatedParty { ClientCaseId = source.Cases[0], SourceType = kind, ClientId = kind == "client" ? id : null, ExternalContactId = kind == "externalContact" ? id : null, LegalEntityId = kind == "legalEntity" ? id : null, DisplayName = "Историческое имя", OrganizationName = "Старая организация", Role = "Старая роль", Notes = "Старая заметка", IsArchived = op == 1, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc), ArchivedAt = op == 1 ? new DateTime(2000, 1, 2, 0, 0, 0, DateTimeKind.Utc) : null, ArchivedByUserName = op == 1 ? "Архиватор" : string.Empty, ArchiveReason = op == 1 ? "Историческая причина" : string.Empty }; db.ClientCaseRelatedParties.Add(party); await db.SaveChangesAsync(); }
        return new(source, kind, id, source.Cases[0], party?.Id);
    }
    private static CasesV2Controller.UpsertClientCaseRelatedPartyRequest PartyInput(PartySeed seed) => new() { SourceType = seed.Kind, SourceId = seed.SourceId, Role = " Новая роль ", Notes = " Новая заметка " };
    private static Task<IActionResult?> RunPartyAsync(PaymentContext db, PartySeed seed, int op, CancellationToken ct = default, bool authenticated = true)
    {
        var controller = CreateController(db, seed.Source.Actor); if (!authenticated) controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(controller.User.Claims));
        async Task<IActionResult?> Add() => (await controller.AddRelatedParty(seed.Case, PartyInput(seed), ct)).Result;
        async Task<IActionResult?> Archive() => await controller.DeleteRelatedParty(seed.Case, seed.Party!.Value, ct);
        return op == 2 ? Archive() : Add();
    }
    private static async Task<string> PartySnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await ExternalWriteSnapshotAsync(db),
        Parties = await db.ClientCaseRelatedParties.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ClientCaseId, x.SourceType, x.ClientId, x.ExternalContactId, x.LegalEntityId, x.DisplayName, x.OrganizationName, x.Phone, x.Email, x.Role, x.Notes, x.CreatedAt, x.CreatedByUserId, x.CreatedByUserName, x.IsArchived, x.ArchivedAt, x.ArchivedByUserId, x.ArchivedByUserName, x.ArchiveReason }).ToArrayAsync(),
        Audit = await db.ObjectAuditEvents.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ObjectType, x.ObjectId, x.Action, x.ActorUserId, x.ActorUserName, x.Description, x.MetadataJson }).ToArrayAsync(),
    });
    public static IEnumerable<object[]> PartyAdmissionCases() => from op in new[] { 0, 1, 2 } from state in new[] { "unauthenticated", "unapproved", "inactive", "missing", "denied-view", "denied-edit" } select new object[] { op, state };
    [Theory]
    [MemberData(nameof(PartyAdmissionCases))]
    public async Task PartyBoundary_RequiresCurrentApprovedCaseViewAndEdit(int op, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, op: op); var actor = seed.Source.Actor;
        if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "missing") seed = seed with { Source = seed.Source with { Actor = new User { Id = int.MaxValue } } };
        if (state == "denied-view") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        if (state == "denied-edit") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        var before = await PartySnapshotAsync(db); var result = await RunPartyAsync(db, seed, op, authenticated: state != "unauthenticated"); Assert.True(result is ForbidResult or UnauthorizedResult, $"op={op}; state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> PartySourcePermissionCases() => from kind in new[] { "client", "externalContact", "legalEntity" } from op in new[] { 0, 1 } from missing in new[] { false, true } select new object[] { kind, op, missing };
    [Theory]
    [MemberData(nameof(PartySourcePermissionCases))]
    public async Task PartyBoundary_AttachAndRestoreRequireSourceView(string kind, int op, bool missing)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, kind, op); var section = kind == "legalEntity" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients;
        if (missing) await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == section).ExecuteDeleteAsync(); else await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        var before = await PartySnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await RunPartyAsync(db, seed, op)); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> PartySuccessCases() => from kind in new[] { "client", "externalContact", "legalEntity" } from op in new[] { 0, 1, 2 } from postgres in new[] { false, true } select new object[] { kind, op, postgres };
    [Theory]
    [MemberData(nameof(PartySuccessCases))]
    public async Task PartyBoundary_PreservesSnapshotIdentityAuditAndJournalWithSourceViewOnly(string kind, int op, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, kind, op);
        await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && (x.Section == RolePermissionCatalog.Clients || x.Section == RolePermissionCatalog.LegalEntities)).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false).SetProperty(x => x.CanEdit, false).SetProperty(x => x.CanDelete, false)); db.ChangeTracker.Clear(); var result = await RunPartyAsync(db, seed, op);
        var party = await db.ClientCaseRelatedParties.AsNoTracking().SingleAsync(x => x.ClientCaseId == seed.Case && x.SourceType == kind);
        if (op == 2) { Assert.IsType<NoContentResult>(result); Assert.True(party.IsArchived); Assert.Equal(seed.Source.Actor.Id, party.ArchivedByUserId); Assert.Equal("Archived from case card", party.ArchiveReason); Assert.Equal("Старая роль", party.Role); }
        else { var reply = Assert.IsType<ClientCaseRelatedPartyDto>(op == 0 ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value); Assert.Equal(kind, reply.SourceType); Assert.Equal(seed.SourceId, reply.SourceId); Assert.Equal("Новая роль", reply.Role); Assert.Equal("Новая заметка", reply.Notes); Assert.False(party.IsArchived); if (op == 1) { Assert.Equal(seed.Party, reply.Id); Assert.Equal(1945, party.CreatedAt.Year); Assert.Null(party.ArchivedAt); Assert.Empty(party.ArchiveReason); } else Assert.True(await db.ObjectAuditEvents.AnyAsync(x => x.ObjectType == "client-case-related-party" && x.ObjectId == reply.Id && x.Action == "Created")); }
        Assert.Contains("Связь", await db.ClientCases.Where(x => x.Id == seed.Case).Select(x => x.EventHistory).SingleAsync(), StringComparison.Ordinal); Assert.Equal("Исходная история", await db.ClientCases.Where(x => x.Id != seed.Case).Select(x => x.EventHistory).SingleAsync()); Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal("Сохранённый результат", await db.TaskJournalItems.Where(x => x.Status == "done").Select(x => x.Result).SingleAsync()); Assert.All(await db.ClientCases.AsNoTracking().ToArrayAsync(), x => Assert.Equal(123.45m, x.CourtClaimAmount)); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("externalContact", false)]
    [InlineData("externalContact", true)]
    [InlineData("legalEntity", false)]
    [InlineData("legalEntity", true)]
    public async Task PartyBoundary_SourceScopeIsIndependentOfCaseScope(string kind, bool sourceAll)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, kind); var other = await CreateUserAsync(db, "party-other@example.local", "Другой владелец");
        if (kind == "client") await db.Clients.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); else if (kind == "externalContact") await db.ExternalContacts.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); else await db.LegalEntities.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id));
        var section = kind == "legalEntity" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients; await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, sourceAll ? DataVisibilityScope.All : DataVisibilityScope.Own)); await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, sourceAll ? DataVisibilityScope.Own : DataVisibilityScope.All)); db.ChangeTracker.Clear(); var before = await PartySnapshotAsync(db); var result = await RunPartyAsync(db, seed, 0); if (sourceAll) Assert.IsType<CreatedResult>(result); else { Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await PartySnapshotAsync(db)); }
    }
    [Theory]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("externalContact", false)]
    [InlineData("externalContact", true)]
    public async Task PartyBoundary_HiddenAssociatedOrganizationNeverCreatesOrCopiesItsProfile(string kind, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, kind); var other = await CreateUserAsync(db, "party-legal-hidden@example.local", "Владелец закрытой организации"); await db.LegalEntities.Where(x => x.Id == seed.Source.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id).SetProperty(x => x.ShortName, "Закрытая организация").SetProperty(x => x.Director, "Невидимый директор"));
        if (kind == "client") await db.Clients.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LegalEntityId, seed.Source.Legal).SetProperty(x => x.Company, "Закрытая организация")); await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); db.ChangeTracker.Clear(); var reply = Assert.IsType<ClientCaseRelatedPartyDto>(Assert.IsType<CreatedResult>(await RunPartyAsync(db, seed, 0)).Value); Assert.Empty(reply.OrganizationName); var parties = await db.ClientCaseRelatedParties.AsNoTracking().Where(x => x.ClientCaseId == seed.Case).ToArrayAsync(); Assert.Single(parties); Assert.DoesNotContain(parties, x => x.SourceType == "legalEntity"); Assert.DoesNotContain("Закрытая организация", await db.ClientCases.Where(x => x.Id == seed.Case).Select(x => x.EventHistory).SingleAsync(), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("externalContact", false)]
    [InlineData("externalContact", true)]
    [InlineData("legalEntity", false)]
    [InlineData("legalEntity", true)]
    public async Task PartyBoundary_MissingOrDeletedSourcePreservesArchiveAndJournal(string kind, bool deleted)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, kind, 1);
        if (deleted) { if (kind == "client") await db.Clients.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true)); else if (kind == "externalContact") await db.ExternalContacts.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true)); else await db.LegalEntities.Where(x => x.Id == seed.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true)); } else seed = seed with { SourceId = int.MaxValue };
        db.ChangeTracker.Clear(); var before = await PartySnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await RunPartyAsync(db, seed, 1)); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> PartyForeignCases() => from op in new[] { 0, 1, 2 } from pending in new[] { false, true } select new object[] { op, pending };
    [Theory]
    [MemberData(nameof(PartyForeignCases))]
    public async Task PartyBoundary_DoesNotSaveOrClearForeignContext(int op, bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, op: op); var before = await PartySnapshotAsync(db); await using var tx = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа"; Assert.IsType<ConflictObjectResult>(await RunPartyAsync(db, seed, op)); Assert.Same(tx, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await PartySnapshotAsync(db));
    }
    public static IEnumerable<object[]> PartyFailureCases() => from op in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { op, postgres, cancel };
    [Theory]
    [MemberData(nameof(PartyFailureCases))]
    public async Task PartyBoundary_RollsBackRealPartyCaseAndAuditSql(int op, bool postgres, bool cancel)
    {
        var fault = new PartyAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault); var seed = await SeedPartyBoundaryAsync(db, op: op); db.ChangeTracker.Clear(); var before = await PartySnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel; Task Write() => RunPartyAsync(db, seed, op, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<PartySqlFault>(Write); Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task PartyBoundary_MissingOrHiddenCaseCannotChangeRelation(int op, bool hidden)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, op: op); if (hidden) { var other = await CreateUserAsync(db, "party-case-hidden@example.local", "Другой"); await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); await db.ClientCases.Where(x => x.Id == seed.Case).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.Clients.Where(x => x.Id != seed.Source.Client).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); } else seed = seed with { Case = int.MaxValue }; db.ChangeTracker.Clear(); var before = await PartySnapshotAsync(db); Assert.IsType<NotFoundResult>(await RunPartyAsync(db, seed, op)); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Fact]
    public async Task PartyBoundary_ArchiveNeedsCaseEditButNotSourceViewAndIsIdempotent()
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, op: 2); await db.RolePermissions.Where(x => x.RoleId == seed.Source.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false)); Assert.IsType<NoContentResult>(await RunPartyAsync(db, seed, 2)); var before = await PartySnapshotAsync(db); Assert.IsType<NoContentResult>(await RunPartyAsync(db, seed, 2)); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartyBoundary_HttpRechecksApprovalAfterFilters(int op)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedPartyBoundaryAsync(db, op: op); db.ChangeTracker.Clear(); var before = await PartySnapshotAsync(db); using var server = ClientContactServer(db, async () => { await db.Users.Where(x => x.Id == seed.Source.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Source.Actor.Id.ToString()); using var response = op == 2 ? await http.DeleteAsync($"/api/v2/cases/{seed.Case}/related-parties/{seed.Party}") : await http.PostAsJsonAsync($"/api/v2/cases/{seed.Case}/related-parties", PartyInput(seed)); Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"HTTP={(int)response.StatusCode}"); Assert.Equal(before, await PartySnapshotAsync(db));
    }
    private sealed class PartySqlFault : Exception;
    private sealed class PartyAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { fail = Armed && data.Context!.ChangeTracker.Entries<ClientCaseRelatedParty>().Any(x => x.State is EntityState.Added or EntityState.Modified); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!fail) return result; Called = true; Executed = await PartySnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new PartySqlFault(); }
    }
}

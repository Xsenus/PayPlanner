using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static UpsertExternalContactRequest ExternalWriteInput(PromotionSeed seed, bool organization = false) => new() { FullName = " Новое имя контакта ", ContactType = organization ? "Organization" : "Person", Phone = "111", Phones = ["222", "222"], Email = "one@example.local", Emails = ["two@example.local"], Roles = ["Supplier"], Position = " Юрист ", PowerOfAttorney = " №123 ", Notes = " Новая заметка ", LegalEntityId = seed.Legal, ParentContactId = organization ? null : seed.Parent, IsActive = false, SyncToPhonebook = true, ResponsibleUserId = int.MaxValue };
    private static async Task<IActionResult?> RunExternalWriteAsync(PaymentContext db, PromotionSeed seed, int op, UpsertExternalContactRequest? input = null, int? id = null, bool authenticated = true, CancellationToken ct = default)
    {
        var controller = ClientContactController(db, seed.Actor, authenticated); return op switch { 0 => (await controller.CreateExternalContact(input ?? ExternalWriteInput(seed), ct)).Result, 1 => (await controller.UpdateExternalContact(id ?? seed.Contact, input ?? ExternalWriteInput(seed), ct)).Result, _ => await controller.DeleteExternalContact(id ?? seed.Contact, ct) };
    }
    private static async Task<string> ExternalWriteSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new { Core = await PromotionSnapshotAsync(db), Parties = await db.ClientCaseRelatedParties.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ClientCaseId, x.ExternalContactId, x.DisplayName, x.Role, x.IsArchived }).ToArrayAsync() });
    public static IEnumerable<object[]> ExternalWriteAdmissionCases() => from op in new[] { 0, 1, 2 } from state in new[] { "unauthenticated", "unapproved", "inactive", "missing", "denied-view", "denied-action", "no-section" } select new object[] { op, state };
    [Theory]
    [MemberData(nameof(ExternalWriteAdmissionCases))]
    public async Task ExternalWriteBoundary_RequiresFreshApprovalViewAndIndividualAction(int op, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db);
        if (state == "unapproved") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "missing") seed = seed with { Actor = new User { Id = int.MaxValue } };
        if (state == "no-section") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteDeleteAsync();
        if (state == "denied-view") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        if (state == "denied-action") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false).SetProperty(x => x.CanEdit, false).SetProperty(x => x.CanDelete, false));
        var before = await ExternalWriteSnapshotAsync(db); var result = await RunExternalWriteAsync(db, seed, op, authenticated: state != "unauthenticated"); Assert.True(result is ForbidResult or UnauthorizedResult, $"op={op}; state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    public static IEnumerable<object[]> ExternalWriteForeignCases() => from op in new[] { 0, 1, 2 } from pending in new[] { false, true } select new object[] { op, pending };
    [Theory]
    [MemberData(nameof(ExternalWriteForeignCases))]
    public async Task ExternalWriteBoundary_PreservesForeignTransactionOrPendingTask(int op, bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var before = await ExternalWriteSnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа";
        Assert.IsType<ConflictObjectResult>(await RunExternalWriteAsync(db, seed, op)); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    public static IEnumerable<object[]> ExternalWriteFaultCases() => from op in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { op, postgres, cancel };
    [Theory]
    [MemberData(nameof(ExternalWriteFaultCases))]
    public async Task ExternalWriteBoundary_RollsBackActualSqlAndCancellation(int op, bool postgres, bool cancel)
    {
        var fault = new ExternalWriteAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault); var seed = await SeedClientPromotionAsync(db); db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel; Task Write() => RunExternalWriteAsync(db, seed, op, ct: cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ExternalWriteSqlFault>(Write); Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    public static IEnumerable<object[]> ExternalWriteSuccessCases() => from op in new[] { 0, 1, 2 } from postgres in new[] { false, true } select new object[] { op, postgres };
    [Theory]
    [MemberData(nameof(ExternalWriteSuccessCases))]
    public async Task ExternalWriteBoundary_PreservesBusinessFieldsAssignmentAndSoftDelete(int op, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); db.ChangeTracker.Clear(); var result = await RunExternalWriteAsync(db, seed, op);
        if (op == 2) { Assert.IsType<NoContentResult>(result); Assert.False(await db.ExternalContacts.AnyAsync(x => x.Id == seed.Contact)); Assert.True(await db.ExternalContacts.IgnoreQueryFilters().AnyAsync(x => x.Id == seed.Contact && EF.Property<bool>(x, "IsDeleted"))); }
        else { var reply = Assert.IsType<ContactDirectoryItemDto>(op == 0 ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value); Assert.Equal("Новое имя контакта", reply.FullName); Assert.Equal("Новая организация", reply.OrganizationName); Assert.Equal("Родительская организация", reply.ParentContactName); Assert.Equal(seed.Actor.Id, reply.ResponsibleUserId); Assert.Equal(new[] { "111", "222" }, reply.Phones); Assert.Equal(new[] { "one@example.local", "two@example.local" }, reply.Emails); Assert.Equal("Юрист", reply.Position); Assert.Equal("№123", reply.PowerOfAttorney); Assert.Equal("Новая заметка", reply.Notes); Assert.False(reply.IsActive); Assert.True(reply.SyncToPhonebook); if (op == 1) { Assert.Equal(seed.Contact, reply.SourceId); Assert.Equal(1945, reply.CreatedAt.Year); } else Assert.NotEqual(seed.Contact, reply.SourceId); }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, seed.Cases);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task ExternalWriteBoundary_UsesIndependentLegalVisibility(int op, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var other = await CreateUserAsync(db, "external-legal@example.local", "Другой"); await db.LegalEntities.Where(x => x.Id == seed.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await RunExternalWriteAsync(db, seed, op)); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ExternalWriteBoundary_MissingOrHiddenSourceKeepsData(int op, bool hidden)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); if (hidden) { var other = await CreateUserAsync(db, "external-hidden@example.local", "Другой"); await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); Assert.IsType<NotFoundResult>(await RunExternalWriteAsync(db, seed, op, id: hidden ? seed.Contact : int.MaxValue)); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    public static IEnumerable<object[]> ExternalWriteUsageCases() => from postgres in new[] { false, true } from kind in new[] { "client", "legal", "employee", "archived-party" } select new object[] { postgres, kind };
    [Theory]
    [MemberData(nameof(ExternalWriteUsageCases))]
    public async Task ExternalWriteBoundary_DeleteRetainsUsedAndHistoricalIdentities(bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var target = seed.Contact;
        if (kind == "client") await db.ExternalContacts.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedClientId, seed.Client));
        if (kind == "legal") await db.ExternalContacts.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedLegalEntityId, seed.Legal));
        if (kind == "employee") target = seed.Parent;
        if (kind == "archived-party") { var party = new ClientCaseRelatedParty { ClientCaseId = seed.Cases[0], SourceType = "external", ExternalContactId = target, DisplayName = "Историческая сторона" }; db.ClientCaseRelatedParties.Add(party); await db.SaveChangesAsync(); await db.ClientCaseRelatedParties.Where(x => x.Id == party.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsArchived, true)); }
        db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); Assert.IsType<ConflictObjectResult>(await RunExternalWriteAsync(db, seed, 2, id: target)); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    public static IEnumerable<object[]> ExternalWriteTypeCases() => from postgres in new[] { false, true } from kind in new[] { "client", "legal", "employees" } select new object[] { postgres, kind };
    [Theory]
    [MemberData(nameof(ExternalWriteTypeCases))]
    public async Task ExternalWriteBoundary_TypeCannotBreakFullProfileOrEmployees(bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var target = kind == "employees" ? seed.Parent : seed.Contact; var input = ExternalWriteInput(seed, kind == "client"); input.ParentContactId = null;
        if (kind == "client") await db.ExternalContacts.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedClientId, seed.Client));
        if (kind == "legal") await db.ExternalContacts.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContactType, "Organization").SetProperty(x => x.LinkedLegalEntityId, seed.Legal)); db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); Assert.IsType<ConflictObjectResult>(await RunExternalWriteAsync(db, seed, 1, input, target)); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    [Theory]
    [InlineData(0, "name")]
    [InlineData(0, "inn")]
    [InlineData(0, "parent")]
    [InlineData(1, "name")]
    [InlineData(1, "inn")]
    [InlineData(1, "parent")]
    public async Task ExternalWriteBoundary_KeepsInputValidationBeforeSaving(int op, string field)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var input = ExternalWriteInput(seed); if (field == "name") input.FullName = " "; if (field == "inn") { input.ContactType = "Organization"; input.Inn = "123"; }
        if (field == "parent") input.ParentContactId = seed.Contact; var before = await ExternalWriteSnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await RunExternalWriteAsync(db, seed, op, input)); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalWriteBoundary_LegacyDefaultsRemainAvailable(int op)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId).ExecuteDeleteAsync(); db.ChangeTracker.Clear(); var result = await RunExternalWriteAsync(db, seed, op); Assert.True(op == 0 ? result is CreatedResult : op == 1 ? result is OkObjectResult : result is NoContentResult);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalWriteBoundary_HttpRechecksApprovalAfterFilters(int op)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); db.ChangeTracker.Clear(); var before = await ExternalWriteSnapshotAsync(db); using var server = ClientContactServer(db, async () => { await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = op == 0 ? await http.PostAsJsonAsync("/api/contacts/external", ExternalWriteInput(seed)) : op == 1 ? await http.PutAsJsonAsync($"/api/contacts/external/{seed.Contact}", ExternalWriteInput(seed)) : await http.DeleteAsync($"/api/contacts/external/{seed.Contact}"); Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"HTTP={(int)response.StatusCode}"); Assert.Equal(before, await ExternalWriteSnapshotAsync(db));
    }
    private sealed class ExternalWriteSqlFault : Exception;
    private sealed class ExternalWriteAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { fail = Armed && data.Context!.ChangeTracker.Entries<ExternalContact>().Any(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!fail) return result; Called = true; Executed = await ExternalWriteSnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new ExternalWriteSqlFault(); }
    }
}

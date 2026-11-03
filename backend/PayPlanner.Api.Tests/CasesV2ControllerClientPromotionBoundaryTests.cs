using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
    private sealed record PromotionSeed(User Actor, int[] Cases, int Client, int Legal, int Contact, int Parent, int Status);
    private static async Task<PromotionSeed> SeedClientPromotionAsync(PaymentContext db)
    {
        var (actor, cases, client, legal) = await SeedClientContactBoundaryAsync(db);
        var status = await db.ClientStatuses.SingleOrDefaultAsync(x => x.SystemKey == ClientStatusSystemKeys.Supplier);
        if (status is null) { status = new ClientStatusEntity { Name = "Поставщик тест", SystemKey = ClientStatusSystemKeys.Supplier }; db.ClientStatuses.Add(status); await db.SaveChangesAsync(); }
        await db.ClientStatuses.Where(x => x.Id == status.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        var parent = new ExternalContact { FullName = "Родительская организация", ContactType = "Organization", ResponsibleUserId = actor.Id }; db.ExternalContacts.Add(parent); await db.SaveChangesAsync();
        var contact = new ExternalContact { FullName = "Физическое лицо из контакта", Phone = "111", PhonesJson = "[\"222\",\"222\"]", Email = "one@example.local", EmailsJson = "[\"two@example.local\"]", Position = "Юрист", PowerOfAttorney = "№123", Notes = "Исходная заметка", RolesJson = "[\"Contact\",\"Client\"]", LegalEntityId = legal, ParentContactId = parent.Id, ResponsibleUserId = actor.Id, IsActive = false, SyncToPhonebook = false, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc) }; db.ExternalContacts.Add(contact); await db.SaveChangesAsync(); return new(actor, cases, client, legal, contact.Id, parent.Id, status.Id);
    }
    private static PromoteContactRequest PromotionInput() => new() { Target = " CLIENT ", Role = " supplier " };
    private static async Task<string> PromotionSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await ClientContactSnapshotAsync(db),
        Contacts = await db.ExternalContacts.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.FullName, x.ContactType, x.Phone, x.PhonesJson, x.Email, x.EmailsJson, x.Position, x.PowerOfAttorney, x.Notes, x.RolesJson, x.LegalEntityId, x.ParentContactId, x.LinkedClientId, x.LinkedLegalEntityId, x.ResponsibleUserId, x.IsActive, x.SyncToPhonebook, x.CreatedAt, x.UpdatedAt, Deleted = EF.Property<bool>(x, "IsDeleted") }).ToArrayAsync(),
        Statuses = await db.ClientStatuses.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.SystemKey, x.IsActive }).ToArrayAsync(),
    });
    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("denied-view")]
    [InlineData("denied-create")]
    [InlineData("no-section")]
    [InlineData("stale-admin")]
    public async Task ClientPromotionBoundary_RequiresFreshApprovalViewAndCreate(string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var actor = seed.Actor;
        if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state == "no-section") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteDeleteAsync();
        if (state == "denied-view") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        if (state is "denied-create" or "stale-admin") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false));
        var controller = ClientContactController(db, actor, state != "unauthenticated"); if (state == "stale-admin") ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin")); var before = await PromotionSnapshotAsync(db); var result = (await controller.PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result; Assert.True(result is ForbidResult or UnauthorizedResult, $"state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientPromotionBoundary_MissingOrHiddenSourceDoesNotCreateClient(bool hidden)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db);
        if (hidden) { var other = await CreateUserAsync(db, "promotion-hidden@example.local", "Другой"); await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        var before = await PromotionSnapshotAsync(db); Assert.IsType<NotFoundResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(hidden ? seed.Contact : int.MaxValue, PromotionInput(), default)).Result); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    public static IEnumerable<object[]> PromotionExistingCases() => from postgres in new[] { false, true } from state in new[] { "organization", "linked", "deleted-linked", "linked-legal" } select new object[] { postgres, state };
    [Theory]
    [MemberData(nameof(PromotionExistingCases))]
    public async Task ClientPromotionBoundary_RejectsWrongKindAndExistingFullLinksWithoutRewiring(bool postgres, string state)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db);
        if (state == "organization") await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContactType, "Organization"));
        else if (state == "linked-legal") await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedLegalEntityId, seed.Legal));
        else { await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.LinkedClientId, seed.Client)); if (state == "deleted-linked") await db.Clients.Where(x => x.Id == seed.Client).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true)); }
        db.ChangeTracker.Clear(); var before = await PromotionSnapshotAsync(db); var result = (await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result; if (state == "organization") Assert.IsType<BadRequestObjectResult>(result); else Assert.IsType<ConflictObjectResult>(result); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    public static IEnumerable<object[]> PromotionDependencyCases() => from postgres in new[] { false, true } from state in new[] { "hidden-legal", "deleted-legal", "hidden-parent", "deleted-parent", "inactive-owner" } select new object[] { postgres, state };
    [Theory]
    [MemberData(nameof(PromotionDependencyCases))]
    public async Task ClientPromotionBoundary_RejectsUnavailableDependenciesBeforeAnyInsert(bool postgres, string state)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var other = await CreateUserAsync(db, "promotion-dependency@example.local", "Другой владелец");
        if (state == "hidden-legal") { await db.LegalEntities.Where(x => x.Id == seed.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        if (state == "deleted-legal") await db.LegalEntities.Where(x => x.Id == seed.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true));
        if (state == "hidden-parent") { await db.ExternalContacts.Where(x => x.Id == seed.Parent).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        if (state == "deleted-parent") await db.ExternalContacts.Where(x => x.Id == seed.Parent).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true));
        if (state == "inactive-owner") { await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.Users.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)); }
        db.ChangeTracker.Clear(); var before = await PromotionSnapshotAsync(db); Assert.IsType<BadRequestObjectResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientPromotionBoundary_CreateOnlyPermissionPreservesPayloadOwnerAndInactiveStatus(bool postgres, bool unassigned)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var count = await db.Clients.CountAsync();
        await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false).SetProperty(x => x.ViewScope, DataVisibilityScope.All)); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        if (unassigned) await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, (int?)null).SetProperty(x => x.LegalEntityId, (int?)null));
        db.ChangeTracker.Clear(); var now = DateTime.UtcNow; var reply = Assert.IsType<ContactDirectoryItemDto>(Assert.IsType<OkObjectResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result).Value); Assert.Equal(seed.Contact, reply.SourceId); Assert.Equal("external", reply.SourceType); Assert.NotNull(reply.LinkedClientId); var client = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == reply.LinkedClientId); Assert.Equal(count + 1, await db.Clients.CountAsync()); Assert.Equal("Физическое лицо из контакта", client.Name); Assert.Equal("111", client.Phone); Assert.Equal("one@example.local", client.Email); Assert.Equal(unassigned ? "Родительская организация" : "Новая организация", client.Company); Assert.Equal(unassigned ? null : seed.Legal, client.LegalEntityId); Assert.Equal(unassigned ? null : seed.Actor.Id, client.ResponsibleUserId); Assert.Equal(seed.Status, client.ClientStatusId); Assert.True(client.IsClientAndSupplier); Assert.False(client.IsActive); Assert.Equal("Исходная заметка", client.Notes); Assert.InRange(client.CreatedAt, now, DateTime.UtcNow); Assert.Contains("Supplier", reply.Roles); Assert.Contains("Client", reply.Roles); Assert.False(reply.SyncToPhonebook); Assert.Equal(1945, reply.CreatedAt.Year); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, seed.Cases);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientPromotionBoundary_MissingSystemStatusKeepsLegacyNullableFallback(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); await db.ClientStatuses.Where(x => x.Id == seed.Status).ExecuteUpdateAsync(s => s.SetProperty(x => x.SystemKey, "test.no-supplier")); db.ChangeTracker.Clear(); var reply = Assert.IsType<ContactDirectoryItemDto>(Assert.IsType<OkObjectResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result).Value); Assert.Null(await db.Clients.Where(x => x.Id == reply.LinkedClientId).Select(x => x.ClientStatusId).SingleAsync());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientPromotionBoundary_UsesExistingCalendarDateGuardForNewProfile(bool postgres, bool invalid)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var notes = BirthdayNotes(invalid ? "не дата" : "1980-05-12"); await db.ExternalContacts.Where(x => x.Id == seed.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.Notes, notes)); db.ChangeTracker.Clear(); var before = await PromotionSnapshotAsync(db); var result = (await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result;
        if (invalid) { Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await PromotionSnapshotAsync(db)); } else { var reply = Assert.IsType<ContactDirectoryItemDto>(Assert.IsType<OkObjectResult>(result).Value); var client = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == reply.LinkedClientId); Assert.Equal(OldClientBirthday, client.BirthDate); Assert.Equal(notes, client.Notes); Assert.Equal(notes, await db.ExternalContacts.Where(x => x.Id == seed.Contact).Select(x => x.Notes).SingleAsync()); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientPromotionBoundary_PreservesForeignTransactionOrPendingWork(bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var before = await PromotionSnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа"; Assert.IsType<ConflictObjectResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientPromotionBoundary_RollsBackClientAndContactAfterActualSql(bool postgres, bool cancel)
    {
        var fault = new PromotionAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault); var seed = await SeedClientPromotionAsync(db); db.ChangeTracker.Clear(); var before = await PromotionSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel; Task Write() => ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<PromotionSqlFault>(Write); Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    [Fact]
    public async Task ClientPromotionBoundary_LegacyDefaultsKeepOwnSourceAndLinks()
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId).ExecuteDeleteAsync(); db.ChangeTracker.Clear(); Assert.IsType<OkObjectResult>((await ClientContactController(db, seed.Actor).PromoteExternalContact(seed.Contact, PromotionInput(), default)).Result);
    }
    [Theory]
    [InlineData("unapproved")]
    [InlineData("denied-create")]
    public async Task ClientPromotionBoundary_HttpRechecksCurrentAccessAfterFilters(string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); db.ChangeTracker.Clear(); var before = await PromotionSnapshotAsync(db); using var server = ClientContactServer(db, async () => { if (state == "unapproved") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); else await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await http.PostAsJsonAsync($"/api/contacts/external/{seed.Contact}/promote", PromotionInput()); Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"HTTP={(int)response.StatusCode}"); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    [Fact]
    public async Task ClientPromotionBoundary_HttpTypedInputAndRepeatedRequestKeepOneServerCreatedClient()
    {
        await using var db = await PassportBoundaryContextAsync(); var seed = await SeedClientPromotionAsync(db); var count = await db.Clients.CountAsync(); db.ChangeTracker.Clear(); using var server = ClientContactServer(db); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); var input = new { target = "client", role = "Supplier", id = seed.Client, linkedClientId = seed.Client, fullName = "Подмена", createdAt = "1900-01-01", birthDate = "1900-01-01", payments = new[] { new { amount = 999999 } } };
        using var response = await http.PostAsJsonAsync($"/api/contacts/external/{seed.Contact}/promote", input); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var reply = await response.Content.ReadFromJsonAsync<ContactDirectoryItemDto>(); Assert.NotEqual(seed.Client, reply!.LinkedClientId); var saved = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == reply.LinkedClientId); Assert.Equal("Физическое лицо из контакта", saved.Name); Assert.NotEqual(1900, saved.CreatedAt.Year); Assert.Null(saved.BirthDate); var before = await PromotionSnapshotAsync(db); using var repeat = await http.PostAsJsonAsync($"/api/contacts/external/{seed.Contact}/promote", input); Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode); Assert.Equal(count + 1, await db.Clients.CountAsync()); Assert.Equal(before, await PromotionSnapshotAsync(db));
    }
    private sealed class PromotionSqlFault : Exception;
    private sealed class PromotionAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { fail = Armed && data.Context!.ChangeTracker.Entries<Client>().Any(x => x.State == EntityState.Added); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!fail) return result; Called = true; Executed = await PromotionSnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new PromotionSqlFault(); }
    }
}

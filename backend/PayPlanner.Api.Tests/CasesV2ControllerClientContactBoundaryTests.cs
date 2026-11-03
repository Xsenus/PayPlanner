using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private static async Task<(User Actor, int[] Cases, int Client, int Legal)> SeedClientContactBoundaryAsync(PaymentContext db)
    {
        var (actor, cases, client, _) = await SeedPassportBoundaryAsync(db, false); await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.LegalEntities);
        var row = await db.Clients.SingleAsync(x => x.Id == client); row.Name = "Прежнее имя"; row.Company = "Прежняя компания"; row.BirthDate = OldClientBirthday; row.Notes = BirthdayNotes("1980-05-12"); row.CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc); row.IsActive = false; row.IsClientAndSupplier = true;
        var legal = new LegalEntity { ShortName = "Новая организация", ResponsibleUserId = actor.Id }; db.LegalEntities.Add(legal); await db.SaveChangesAsync(); return (actor, cases, client, legal.Id);
    }

    private static UpdateClientContactRequest ClientContactInput(int? legal = null) => new() { FullName = "Новое имя", Phone = " 111 ", Phones = [" 222 ", "222", "333"], Email = "one@example.local", Emails = ["two@example.local", "two@example.local"], Position = " Юрист ", PowerOfAttorney = " Доверенность №123 ", Notes = " Новая заметка ", LegalEntityId = legal, SyncToPhonebook = false };
    private static ContactsController ClientContactController(PaymentContext db, User actor, bool authenticated = true)
    {
        var context = ClientsControllerTests.AtomicCreateController(db, actor.Id).ControllerContext;
        if (!authenticated) context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(context.HttpContext.User.Claims));
        return new(db, new FormRequirementsService(db), new ConfigurationBuilder().Build()) { ControllerContext = context };
    }
    private static async Task<string> ClientContactSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await ClientBatchSnapshotAsync(db),
        Clients = await db.Clients.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Phone, x.Email, x.Company, x.Notes, x.BirthDate, x.CreatedAt, x.LegalEntityId, x.ClientStatusId, x.ResponsibleUserId, x.IsClientAndSupplier, x.IsActive, Deleted = EF.Property<bool>(x, "IsDeleted") }).ToArrayAsync(),
        Legals = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ShortName, x.ResponsibleUserId, x.Notes, Deleted = EF.Property<bool>(x, "IsDeleted") }).ToArrayAsync(),
    });

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    [InlineData("missing")]
    [InlineData("denied-view")]
    [InlineData("denied-edit")]
    [InlineData("no-section")]
    [InlineData("stale-admin")]
    public async Task ClientContactBoundary_RequiresCurrentApprovedActorViewAndEdit(string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db);
        if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        if (state == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state == "no-section") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteDeleteAsync();
        if (state == "denied-view") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        if (state is "denied-edit" or "stale-admin") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        var controller = ClientContactController(db, actor, state != "unauthenticated"); if (state == "stale-admin") ((ClaimsIdentity)controller.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));
        var before = await ClientContactSnapshotAsync(db); var result = (await controller.UpdateClientContact(client, ClientContactInput(), default)).Result; Assert.True(result is ForbidResult or UnauthorizedResult, $"state={state}; result={result?.GetType().Name}"); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientContactBoundary_MissingOrHiddenClientCannotBeChanged(bool hidden)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db);
        if (hidden) { var other = await CreateUserAsync(db, "contact-hidden@example.local", "Другой"); await db.Clients.Where(x => x.Id == client).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        var before = await ClientContactSnapshotAsync(db); Assert.IsType<NotFoundResult>((await ClientContactController(db, actor).UpdateClientContact(hidden ? client : int.MaxValue, ClientContactInput(), default)).Result); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    public static IEnumerable<object[]> ClientContactLegalCases() => from postgres in new[] { false, true } from state in new[] { "missing", "hidden", "denied-view", "no-section" } select new object[] { postgres, state };
    [Theory]
    [MemberData(nameof(ClientContactLegalCases))]
    public async Task ClientContactBoundary_RejectsUnavailableLegalUsingItsOwnSection(bool postgres, string state)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var (actor, _, client, legal) = await SeedClientContactBoundaryAsync(db);
        if (state == "hidden") { var other = await CreateUserAsync(db, "contact-legal@example.local", "Другая организация"); await db.LegalEntities.Where(x => x.Id == legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own)); }
        if (state == "denied-view") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        if (state == "no-section") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteDeleteAsync();
        var before = await ClientContactSnapshotAsync(db); Assert.IsType<BadRequestObjectResult>((await ClientContactController(db, actor).UpdateClientContact(client, ClientContactInput(state == "missing" ? int.MaxValue : legal), default)).Result); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientContactBoundary_PreservesProfileAndSeparateLegalVisibility(bool postgres, bool link)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var (actor, cases, client, legal) = await SeedClientContactBoundaryAsync(db); var other = await CreateUserAsync(db, "contact-other@example.local", "Другой владелец");
        await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, link ? DataVisibilityScope.Own : DataVisibilityScope.All));
        await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.All).SetProperty(x => x.CanEdit, false));
        await db.LegalEntities.Where(x => x.Id == legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id));
        if (!link) { await db.Clients.Where(x => x.Id == client).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id).SetProperty(x => x.LegalEntityId, legal)); }
        db.ChangeTracker.Clear(); var result = await ClientContactController(db, actor).UpdateClientContact(client, ClientContactInput(link ? legal : null), default); var reply = Assert.IsType<ContactDirectoryItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value); var saved = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == client);
        Assert.Equal(client, reply.SourceId); Assert.Equal("Новое имя", saved.Name); Assert.Equal(link ? legal : null, saved.LegalEntityId); Assert.Equal(link ? "Новая организация" : "Прежняя компания", saved.Company); Assert.Equal(link ? actor.Id : other.Id, saved.ResponsibleUserId); Assert.Equal(OldClientBirthday, saved.BirthDate); Assert.Equal(1945, saved.CreatedAt.Year); Assert.False(saved.IsActive); Assert.True(saved.IsClientAndSupplier); Assert.Equal(new[] { "111", "222", "333" }, reply.Phones); Assert.Equal(new[] { "one@example.local", "two@example.local" }, reply.Emails); Assert.Equal("Юрист", reply.Position); Assert.Equal("Доверенность №123", reply.PowerOfAttorney); Assert.Equal("Новая заметка", reply.Notes); Assert.False(reply.SyncToPhonebook);
        using var profile = JsonDocument.Parse(saved.Notes[BirthdayProfilePrefix.Length..]); Assert.Equal("1980-05-12", profile.RootElement.GetProperty("birthDate").GetString()); Assert.Equal("Прежний адрес", profile.RootElement.GetProperty("registrationAddress").GetString()); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientContactBoundary_RollsBackAfterActualSqlErrorOrCancellation(bool postgres, bool cancel)
    {
        var fault = new ClientContactAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault);
        var (actor, _, client, legal) = await SeedClientContactBoundaryAsync(db); db.ChangeTracker.Clear(); var before = await ClientContactSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel;
        Task Write() => ClientContactController(db, actor).UpdateClientContact(client, ClientContactInput(legal), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ClientContactSqlFault>(Write);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientContactBoundary_PreservesForeignTransactionAndPendingWork(bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db); var before = await ClientContactSnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync(); var task = await db.TaskJournalItems.SingleAsync(x => x.Status == "done"); if (pending) task.Result = "Чужая работа";
        Assert.IsType<ConflictObjectResult>((await ClientContactController(db, actor).UpdateClientContact(client, ClientContactInput(), default)).Result); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Fact]
    public async Task ClientContactBoundary_KeepsLegacyDefaultsAndConfiguredRequiredFields()
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db); await db.RolePermissions.Where(x => x.RoleId == actor.RoleId).ExecuteDeleteAsync();
        Assert.IsType<OkObjectResult>((await ClientContactController(db, actor).UpdateClientContact(client, ClientContactInput(), default)).Result);
        db.FormFieldRequirements.Add(new FormFieldRequirement { Section = "contacts-client", FieldKey = "FullName", IsRequired = true }); await db.SaveChangesAsync(); var before = await ClientContactSnapshotAsync(db); var input = ClientContactInput(); input.FullName = " "; Assert.IsType<BadRequestObjectResult>((await ClientContactController(db, actor).UpdateClientContact(client, input, default)).Result); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("denied-edit")]
    public async Task ClientContactBoundary_HttpRechecksAccessAfterFilters(string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db); db.ChangeTracker.Clear(); var before = await ClientContactSnapshotAsync(db);
        using var server = ClientContactServer(db, async () => { if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)); else await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await http.PutAsJsonAsync($"/api/contacts/clients/{client}", ClientContactInput()); Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized); Assert.Equal(before, await ClientContactSnapshotAsync(db));
    }

    [Fact]
    public async Task ClientContactBoundary_HttpTypedRequestKeepsIdentityAndBusinessMetadata()
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(db); db.ChangeTracker.Clear(); using var server = ClientContactServer(db); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        using var response = await http.PutAsJsonAsync($"/api/contacts/clients/{client}", new { fullName = "HTTP-имя", id = int.MaxValue, responsibleUserId = int.MaxValue, birthDate = "1900-01-01", createdAt = "1900-01-01", clientStatusId = int.MaxValue, isActive = true }); Assert.Equal(HttpStatusCode.OK, response.StatusCode); var saved = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == client); Assert.Equal("HTTP-имя", saved.Name); Assert.Equal(actor.Id, saved.ResponsibleUserId); Assert.Equal(OldClientBirthday, saved.BirthDate); Assert.Equal(1945, saved.CreatedAt.Year); Assert.Null(saved.ClientStatusId); Assert.False(saved.IsActive); var reply = await response.Content.ReadFromJsonAsync<ContactDirectoryItemDto>(); Assert.Equal(client, reply!.SourceId);
    }

    private static TestServer ClientContactServer(PaymentContext db, Func<Task>? change = null) => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("Email:Enabled", "false").UseSetting("BackgroundServices:Enabled", "false")
        .ConfigureServices(s => { s.AddLogging(); s.AddSingleton(db); s.AddSingleton<IFormRequirementsService>(new FormRequirementsService(db)); s.AddAuthentication("write-fixture").AddScheme<AuthenticationSchemeOptions, ConfirmationAuthentication>("write-fixture", _ => { }); s.AddAuthorization(); s.AddControllers(o => { if (change is not null) o.Filters.Add(new PassportPermissionChange(change)); }).AddApplicationPart(typeof(ContactsController).Assembly); })
        .Configure(a => { a.UseRouting(); a.UseAuthentication(); a.UseAuthorization(); a.UseEndpoints(e => e.MapControllers()); }));
    private sealed class ClientContactSqlFault : Exception;
    private sealed class ClientContactAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { fail = Armed && data.Context!.ChangeTracker.Entries<Client>().Any(e => e.State == EntityState.Modified); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) { if (!fail) return result; Called = true; Executed = await ClientContactSnapshotAsync((PaymentContext)data.Context!); if (cancel) { Cancel!(); ct.ThrowIfCancellationRequested(); } throw new ClientContactSqlFault(); }
    }
}

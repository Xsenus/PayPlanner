using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
    private static async Task<(User Actor, int[] Cases, int Client, int? Power)> SeedPoaBoundaryAsync(PaymentContext db, bool exists)
    {
        var (actor, cases, client, _) = await SeedPassportBoundaryAsync(db, false); PowerOfAttorney? power = null;
        if (exists) { power = new PowerOfAttorney { ClientId = client, Number = "OLD", IssuedAt = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), Issuer = "Старый выдавший", Notes = "Старая заметка", IsActive = true, CreatedAt = new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc) }; db.PowersOfAttorney.Add(power); await db.SaveChangesAsync(); }
        return (actor, cases, client, power?.Id);
    }

    private static UpsertPowerOfAttorneyRequest PoaInput() => new()
    {
        Number = " NEW-123 ",
        IssuedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        ValidUntil = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        Issuer = " Новый выдавший ",
        Notes = " Новая заметка ",
        IsActive = false,
    };

    private static Task<IActionResult> WritePoaAsync(PaymentContext db, User actor, int clientId, int id, int operation, CancellationToken ct = default, bool authenticated = true)
    {
        var root = ClientsControllerTests.AtomicCreateController(db, actor.Id); if (!authenticated) root.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(root.User.Claims));
        var controller = new PowerOfAttorneyController(db) { ControllerContext = root.ControllerContext };
        return operation switch { 0 => controller.Create(clientId, PoaInput(), ct), 1 => controller.Update(clientId, id, PoaInput(), ct), 2 => controller.Delete(clientId, id, ct), _ => controller.Get(clientId, ct) };
    }

    private static async Task<string> PoaSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Core = await ClientBatchSnapshotAsync(db),
        Powers = await db.PowersOfAttorney.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientId, row.Number, row.IssuedAt, row.ValidUntil, row.Issuer, row.Notes, row.IsActive, row.CreatedAt, row.UpdatedAt, Deleted = EF.Property<bool>(row, "IsDeleted") }).ToArrayAsync(),
    });

    public static IEnumerable<object[]> PoaAdmissionCases()
    {
        foreach (var operation in new[] { 0, 1, 2, 3 }) foreach (var state in new[] { "unauthenticated", "unapproved", "inactive", "denied-view", "no-section", "missing" }) yield return new object[] { operation, state };
        foreach (var operation in new[] { 0, 1, 2 }) yield return new object[] { operation, "denied-action" };
    }

    [Theory]
    [MemberData(nameof(PoaAdmissionCases))]
    public async Task PoaBoundary_KeepsCurrentApprovalScopeAndSeparateActionPermission(int operation, string state)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, true); var before = await PoaSnapshotAsync(db);
        if (state == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (state == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (state == "missing") actor = new User { Id = int.MaxValue };
        if (state == "no-section") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteDeleteAsync();
        if (state == "denied-view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (state == "denied-action") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false).SetProperty(row => row.CanEdit, false).SetProperty(row => row.CanDelete, false));
        var result = await WritePoaAsync(db, actor, client, power!.Value, operation, authenticated: state != "unauthenticated"); Assert.True(result is ForbidResult or UnauthorizedResult, $"operation={operation}; state={state}; result={result.GetType().Name}"); Assert.Equal(before, await PoaSnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task PoaBoundary_RejectsForeignTransactionOrPendingWork(int operation, bool pending)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, true); var before = await PoaSnapshotAsync(db); await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); if (pending) task.Result = "Чужая работа";
        Assert.IsType<ConflictObjectResult>(await WritePoaAsync(db, actor, client, power!.Value, operation)); Assert.Same(transaction, db.Database.CurrentTransaction); if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal("Чужая работа", task.Result); }
        Assert.Equal(before, await PoaSnapshotAsync(db));
    }

    public static IEnumerable<object[]> PoaFailureCases() => from operation in new[] { 0, 1, 2 } from postgres in new[] { false, true } from cancel in new[] { false, true } select new object[] { operation, postgres, cancel };

    [Theory]
    [MemberData(nameof(PoaFailureCases))]
    public async Task PoaBoundary_RollsBackActualSqlIncludingDeletionOnFailureOrCancellation(int operation, bool postgres, bool cancel)
    {
        var fault = new PoaAfterSql(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await PassportBoundaryContextAsync(fault);
        var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, operation != 0); var before = await PoaSnapshotAsync(db); using var cancellation = new CancellationTokenSource(); fault.Armed = true; fault.Cancel = cancellation.Cancel;
        Task Write() => WritePoaAsync(db, actor, client, power ?? 0, operation, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<PoaSqlFault>(Write);
        Assert.True(fault.Called); Assert.NotEqual(before, fault.Executed); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); Assert.Equal(before, await PoaSnapshotAsync(db));
    }

    public static IEnumerable<object[]> PoaSuccessCases() => from operation in new[] { 0, 1, 2 } from postgres in new[] { false, true } select new object[] { operation, postgres };

    [Theory]
    [MemberData(nameof(PoaSuccessCases))]
    public async Task PoaBoundary_PreservesTypedBusinessFieldsIdentityDatesAndSoftDelete(int operation, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var (actor, cases, client, power) = await SeedPoaBoundaryAsync(db, operation != 0); var result = await WritePoaAsync(db, actor, client, power ?? 0, operation);
        if (operation == 2) { Assert.IsType<NoContentResult>(result); Assert.False(await db.PowersOfAttorney.AnyAsync(row => row.Id == power)); Assert.True(await db.PowersOfAttorney.IgnoreQueryFilters().AnyAsync(row => row.Id == power && EF.Property<bool>(row, "IsDeleted"))); }
        else
        {
            var reply = Assert.IsType<PowerOfAttorney>(operation == 0 ? Assert.IsType<CreatedResult>(result).Value : Assert.IsType<OkObjectResult>(result).Value); var saved = await db.PowersOfAttorney.AsNoTracking().SingleAsync(row => row.ClientId == client);
            Assert.Equal(operation == 0 ? saved.Id : power, reply.Id); Assert.Equal(client, reply.ClientId); Assert.Equal("NEW-123", saved.Number); Assert.Equal("Новый выдавший", saved.Issuer); Assert.Equal("Новая заметка", saved.Notes); Assert.False(saved.IsActive); Assert.Equal(PoaInput().IssuedAt, saved.IssuedAt); Assert.Equal(PoaInput().ValidUntil, saved.ValidUntil);
            if (operation == 1) { Assert.Equal(new DateTime(1945, 3, 15, 0, 0, 0, DateTimeKind.Utc), saved.CreatedAt); Assert.NotNull(saved.UpdatedAt); }
        }
        Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, cases);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task PoaBoundary_ForeignChildIdentityNeverChangesEitherClient(int operation, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await PassportBoundaryContextAsync();
        var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, true); var other = new Client { Name = "Другая видимая карточка", ResponsibleUserId = actor.Id }; db.Clients.Add(other); await db.SaveChangesAsync(); var before = await PoaSnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await WritePoaAsync(db, actor, other.Id, power!.Value, operation)); Assert.Equal(before, await PoaSnapshotAsync(db));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PoaBoundary_HttpRechecksPermissionAfterAttributeFilters(int operation)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, true); var before = await PoaSnapshotAsync(db);
        using var server = PassportBoundaryServer(db, async () => { await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false).SetProperty(row => row.CanEdit, false).SetProperty(row => row.CanDelete, false)); }); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        var path = $"/api/clients/{client}/powers-of-attorney"; using var response = operation == 0 ? await http.PostAsJsonAsync(path, PoaInput()) : operation == 1 ? await http.PutAsJsonAsync($"{path}/{power}", PoaInput()) : await http.DeleteAsync($"{path}/{power}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(before, await PoaSnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoaBoundary_HttpNeverAcceptsForeignIdentityOrServerStamps(bool update)
    {
        await using var db = await PassportBoundaryContextAsync(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(db, true);
        db.ChangeTracker.Clear();
        var input = new { id = int.MaxValue, clientId = int.MaxValue, createdAt = "1900-01-01T00:00:00Z", updatedAt = "1900-01-01T00:00:00Z", number = " NEW-123 ", issuedAt = "2026-01-02T00:00:00Z", issuer = " Выдавший " };
        using var server = PassportBoundaryServer(db); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); var path = $"/api/clients/{client}/powers-of-attorney"; using var response = update ? await http.PutAsJsonAsync($"{path}/{power}", input) : await http.PostAsJsonAsync(path, input); Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<PowerOfAttorney>(); Assert.NotNull(reply); Assert.Equal(client, reply.ClientId); Assert.NotEqual(int.MaxValue, reply.Id); Assert.NotEqual(1900, reply.CreatedAt.Year); if (update) Assert.Equal(power, reply.Id);
    }

    private sealed class PoaSqlFault : Exception;
    private sealed class PoaAfterSql(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public string? Executed { get; private set; }
        public Action? Cancel { get; set; }
        private bool fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { fail = Armed && eventData.Context!.ChangeTracker.Entries<PowerOfAttorney>().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted); return ValueTask.FromResult(result); }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) { if (!fail) return result; Called = true; Executed = await PoaSnapshotAsync((PaymentContext)eventData.Context!); if (cancel) { Cancel!(); cancellationToken.ThrowIfCancellationRequested(); } throw new PoaSqlFault(); }
    }
}

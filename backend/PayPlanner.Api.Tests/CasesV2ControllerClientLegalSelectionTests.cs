using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static async Task<LegalReferenceSeed> SeedLegalSelectionAsync(PaymentContext db, string state, bool retain = false)
    {
        var seed = await SeedLegalReferenceAsync(db, state is "client-own-legal-all" or "department-allowed" or "department-denied" or "missing-permission" or "inactive-owner" ? "visible" : state);
        await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false));
        if (state == "missing-permission") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteDeleteAsync();
        if (retain || state == "client-own-legal-all") await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own));
        if (state.StartsWith("department-", StringComparison.Ordinal))
        {
            await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department));
            if (state == "department-allowed")
            {
                var department = await db.Users.Where(x => x.Id == seed.Actor.Id).Select(x => new { x.DepartmentId, x.Department }).SingleAsync();
                await db.Users.Where(x => x.Id == seed.Other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.DepartmentId).SetProperty(x => x.Department, department.Department));
            }
        }
        if (state == "inactive-owner") await db.Users.Where(x => x.Id == seed.Other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        if (!retain) await db.Clients.Where(x => x.Id == seed.Client).ExecuteUpdateAsync(s => s.SetProperty(x => x.LegalEntityId, (int?)null));
        db.ChangeTracker.Clear(); return seed;
    }

    private static Client LegalSelectionInput(LegalReferenceSeed seed, int? legalId) => new()
    {
        Name = "Выбор организации",
        Phone = "+79990002233",
        LegalEntityId = legalId,
        ResponsibleUserId = seed.Actor.Id,
    };

    private static async Task<string> LegalSelectionSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Clients = await db.Clients.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name, x.Company, x.Notes, x.BirthDate, x.LegalEntityId, x.ResponsibleUserId, x.IsActive, x.CreatedAt }).ToArrayAsync(),
        Legal = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
    });

    private static bool LegalSelectionAllowed(string state) => state is "visible" or "own-assigned" or "own-only-profile" or "own-archived" or "client-own-legal-all" or "department-allowed" or "inactive-owner";

    public static IEnumerable<object[]> LegalSelectionAdmissionCases() =>
        (from route in new[] { 0, 1, 2 } from create in new[] { false, true } from state in new[] { "visible", "hidden", "denied", "missing-permission", "own-assigned", "own-unassigned", "own-no-profile", "own-only-profile", "own-archived", "client-own-legal-all", "department-allowed", "department-denied" } select new object[] { route, create, state, false })
        .Concat(from route in new[] { 0, 1, 2 } from state in new[] { "hidden", "client-own-legal-all" } select new object[] { route, true, state, true })
        .Concat(from route in new[] { 0, 2 } from state in new[] { "own-assigned", "own-unassigned" } select new object[] { route, false, state, true })
        .Append(new object[] { 1, false, "own-only-profile", true });

    [Theory]
    [MemberData(nameof(LegalSelectionAdmissionCases))]
    public async Task ClientLegalSelection_NewTargetRequiresItsOwnViewScopeWithoutEdit(int route, bool create, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; var recorder = new LegalReferenceCommands();
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options) : await CreateContextAsync(recorder);
        var seed = await SeedLegalSelectionAsync(db, state); var before = await LegalSelectionSnapshotAsync(db); var parent = JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); recorder.Armed = true;
        using var response = await SendRequiredClientAsync(http, route, create, seed.Client, LegalSelectionInput(seed, seed.Legal.Id)); recorder.Armed = false;
        var expected = LegalSelectionAllowed(state) ? create ? HttpStatusCode.Created : HttpStatusCode.OK : HttpStatusCode.BadRequest;
        Assert.True(response.StatusCode == expected, $"route={route};create={create};state={state};postgres={postgres};expected={(int)expected};HTTP={(int)response.StatusCode};body={await response.Content.ReadAsStringAsync()}");
        if (!LegalSelectionAllowed(state))
        {
            Assert.Equal(before, await LegalSelectionSnapshotAsync(db)); Assert.DoesNotContain(LegalReferenceSecret, await response.Content.ReadAsStringAsync());
        }
        else
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var id = json.RootElement.GetProperty("id").GetInt32(); var saved = await db.Clients.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(seed.Legal.Id, saved.LegalEntityId); Assert.Equal("Выбор организации", saved.Name); Assert.Equal(seed.Actor.Id, saved.ResponsibleUserId); Assert.Equal(create ? 4 : 3, await db.Clients.CountAsync());
        }
        Assert.Equal(parent, JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync())); recorder.AssertNoInlineAssets(); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    public static IEnumerable<object[]> LegalSelectionRetentionCases() =>
        (from route in new[] { 0, 1, 2 } from state in new[] { "hidden", "denied", "own-unassigned", "own-no-profile", "deleted" } select new object[] { route, state, false })
        .Concat(from route in new[] { 0, 1, 2 } from state in new[] { "hidden", "own-unassigned" } select new object[] { route, state, true });

    [Theory]
    [MemberData(nameof(LegalSelectionRetentionCases))]
    public async Task ClientLegalSelection_KnownHiddenOrDeletedLinkDoesNotBlockUnrelatedClientEdit(int route, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalSelectionAsync(db, state, retain: true); var parent = JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, false, seed.Client, LegalSelectionInput(seed, seed.Legal.Id));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"route={route};state={state};postgres={postgres};HTTP={(int)response.StatusCode};body={await response.Content.ReadAsStringAsync()}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("legalEntity").ValueKind); Assert.Equal(seed.Legal.Id, json.RootElement.GetProperty("legalEntityId").GetInt32());
        Assert.Equal("Выбор организации", await db.Clients.Where(x => x.Id == seed.Client).Select(x => x.Name).SingleAsync()); Assert.Equal(3, await db.Clients.CountAsync()); Assert.Equal(parent, JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync())); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> LegalSelectionInvalidCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from id in new[] { 0, -3, int.MaxValue } select new object[] { route, create, id };

    [Theory]
    [MemberData(nameof(LegalSelectionInvalidCases))]
    public async Task ClientLegalSelection_RejectsInvalidOrMissingNewTargetAtomically(int route, bool create, int id)
    {
        await using var db = await CreateContextAsync(); var seed = await SeedLegalSelectionAsync(db, "visible"); var before = await LegalSelectionSnapshotAsync(db);
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, seed.Client, LegalSelectionInput(seed, id));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await LegalSelectionSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> LegalSelectionLateCases() => from route in new[] { 0, 1, 2 } from create in new[] { false, true } from target in new[] { "approval", "client", "legal", "profile", "assignment" } select new object[] { route, create, target };

    [Theory]
    [MemberData(nameof(LegalSelectionLateCases))]
    public async Task ClientLegalSelection_RechecksCommittedChangesAfterAuthentication(int route, bool create, string target)
    {
        await using var db = await CreateContextAsync(); var seed = await SeedLegalSelectionAsync(db, target is "profile" or "assignment" ? "own-assigned" : "visible"); var before = await LegalSelectionSnapshotAsync(db);
        using var server = RequiredClientServer(db, seed.Actor, async () =>
        {
            if (target == "approval") await db.Users.Where(x => x.Id == seed.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
            if (target == "client") { if (create) await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false)); else await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false)); }
            if (target is "legal" or "profile") { var section = target == "legal" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.CompanyProfile; await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false)); }
            if (target == "assignment") await db.UserCompanyAccesses.Where(x => x.UserId == seed.Actor.Id).ExecuteDeleteAsync();
        });
        using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, create, seed.Client, LegalSelectionInput(seed, seed.Legal.Id));
        Assert.Equal(target == "approval" ? HttpStatusCode.Unauthorized : target == "client" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await LegalSelectionSnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ClientLegalSelection_CanRemoveKnownHiddenOrDeletedLink(int route, bool deleted)
    {
        await using var db = await CreateContextAsync(); var seed = await SeedLegalSelectionAsync(db, deleted ? "deleted" : "hidden", retain: true); var parent = JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await SendRequiredClientAsync(http, route, false, seed.Client, LegalSelectionInput(seed, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(await db.Clients.Where(x => x.Id == seed.Client).Select(x => x.LegalEntityId).SingleAsync()); Assert.Equal(parent, JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync()));
    }

    public static IEnumerable<object[]> LegalSelectionInactiveOwnerCases() =>
        (from route in new[] { 0, 1, 2 } from create in new[] { false, true } select new object[] { route, create, false }).Append(new object[] { 2, false, true });

    [Theory]
    [MemberData(nameof(LegalSelectionInactiveOwnerCases))]
    public async Task ClientLegalSelection_AllScopeDoesNotInventAnActiveParentOwnerRequirement(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalSelectionAsync(db, "inactive-owner"); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, create, seed.Client, LegalSelectionInput(seed, seed.Legal.Id)); Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode); Assert.False(await db.Users.Where(x => x.Id == seed.Other.Id).Select(x => x.IsActive).SingleAsync());
    }
}

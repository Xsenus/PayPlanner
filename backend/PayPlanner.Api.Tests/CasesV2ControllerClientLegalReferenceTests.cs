using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly int[] LegalReferenceRoutes = [0, 1, 6, 7, 8];
    private static readonly string[] LegalReferenceAssets = [nameof(LegalEntity.LogoDataUrl), nameof(LegalEntity.SealDataUrl), nameof(LegalEntity.DirectorSignatureDataUrl)];
    private const string LegalReferenceSecret = "linked-private-asset";
    private sealed record LegalReferenceSeed(User Actor, User Other, int Client, LegalEntity Legal);

    private static async Task<LegalReferenceSeed> SeedLegalReferenceAsync(PaymentContext db, string state = "visible")
    {
        var (actor, other, ids) = await SeedClientReadAsync(db);
        await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.LegalEntities,
            canView: state is not ("denied" or "own-only-profile"), viewScope: state == "hidden" ? DataVisibilityScope.Own : DataVisibilityScope.All);
        var legal = new LegalEntity { ResponsibleUserId = other.Id };
        foreach (var property in typeof(LegalEntity).GetProperties().Where(p => p.PropertyType == typeof(string)))
        {
            var value = "ref-" + property.Name.ToLowerInvariant();
            var max = property.GetCustomAttribute<MaxLengthAttribute>()?.Length ?? value.Length;
            property.SetValue(legal, value[..Math.Min(max, value.Length)]);
        }
        legal.ProfileStatus = state == "own-archived" ? CompanyProfileStatuses.Archived : CompanyProfileStatuses.Active;
        legal.CreatedAt = new DateTime(2003, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        legal.UpdatedAt = new DateTime(2004, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        legal.IsClientAndSupplier = true; legal.VatWithoutVat = true;
        foreach (var name in LegalReferenceAssets) typeof(LegalEntity).GetProperty(name)!.SetValue(legal, "data:image/png;base64," + LegalReferenceSecret + name);
        db.LegalEntities.Add(legal); await db.SaveChangesAsync();
        await db.Clients.Where(x => x.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.LegalEntityId, legal.Id));
        if (state.StartsWith("own-", StringComparison.Ordinal))
        {
            db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{legal.Id}]" });
            if (state != "own-no-profile") await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.CompanyProfile, canEdit: false);
            if (state is not ("own-unassigned" or "own-admin")) db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor.Id, LegalEntityId = legal.Id });
            await db.SaveChangesAsync();
            if (state == "own-admin") await db.Roles.Where(x => x.Id == actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "admin"));
        }
        if (state == "deleted") await db.LegalEntities.Where(x => x.Id == legal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true));
        db.ChangeTracker.Clear();
        if (db.Database.IsNpgsql())
        {
            // PostgreSQL persists microseconds, while the audit stamper uses .NET
            // ticks. Assert the exact durable values, not the pre-roundtrip clock.
            var timestamps = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == legal.Id)
                .Select(x => new { x.CreatedAt, x.UpdatedAt }).SingleAsync();
            legal.CreatedAt = timestamps.CreatedAt; legal.UpdatedAt = timestamps.UpdatedAt;
        }
        return new(actor, other, ids[0], legal);
    }

    private static bool LegalReferenceVisible(string state) => state is "visible" or "own-assigned" or "own-only-profile" or "own-archived" or "own-admin";
    private static JsonElement LegalReferenceItem(JsonElement body, int route, int client) => route == 7
        ? body.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == client)
        : route == 0 ? body.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == client) : body;

    private static void AssertLegalReferenceJson(JsonElement item, LegalReferenceSeed seed, string state, int route)
    {
        Assert.Equal(seed.Legal.Id, item.GetProperty("legalEntityId").GetInt32());
        var legal = item.GetProperty("legalEntity");
        if (!LegalReferenceVisible(state))
        {
            Assert.True(legal.ValueKind == JsonValueKind.Null, $"route={route};state={state};hidden-profile-leak={legal.GetRawText()}");
            Assert.DoesNotContain(LegalReferenceSecret, item.GetRawText()); return;
        }
        foreach (var name in LegalReferenceAssets)
            Assert.True(legal.GetProperty(JsonNamingPolicy.CamelCase.ConvertName(name)).ValueKind == JsonValueKind.Null, $"route={route};state={state};inline-asset={name}");
        foreach (var property in typeof(LegalEntity).GetProperties().Where(p => (p.PropertyType == typeof(string) || p.PropertyType.IsValueType) && !LegalReferenceAssets.Contains(p.Name)))
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var expected = JsonSerializer.SerializeToElement(property.GetValue(seed.Legal), property.PropertyType);
            if (property.PropertyType == typeof(DateTime) || property.PropertyType == typeof(DateTime?))
                Assert.Equal(expected.GetDateTime().Ticks, legal.GetProperty(key).GetDateTime().Ticks);
            else Assert.Equal(expected.GetRawText(), legal.GetProperty(key).GetRawText());
        }
        Assert.Equal(JsonValueKind.Null, legal.GetProperty("responsibleUser").ValueKind);
        foreach (var name in new[] { "clients", "cases", "payments", "acts", "contracts", "userCompanyAccesses", "branches", "addresses", "legalEntityContracts" }) Assert.Empty(legal.GetProperty(name).EnumerateArray());
    }

    private sealed class LegalReferenceCommands : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public List<string> Commands { get; } = [];
        public int RegistryReads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Armed)
            {
                Commands.Add(command.CommandText);
                if (command.Parameters.Cast<DbParameter>().Any(p => p.Value is string key && key.StartsWith("legalEntities.mainCompany", StringComparison.Ordinal)) || command.CommandText.Contains("legalEntities.mainCompany", StringComparison.Ordinal)) RegistryReads++;
            }
            return ValueTask.FromResult(result);
        }
        public void AssertNoInlineAssets() { foreach (var name in LegalReferenceAssets) Assert.DoesNotContain(Commands, sql => sql.Contains('"' + name + '"', StringComparison.Ordinal)); }
    }

    public static IEnumerable<object[]> LegalReferencePayloadCases() =>
        (from route in LegalReferenceRoutes from state in new[] { "visible", "hidden", "denied", "own-assigned", "own-unassigned", "own-no-profile", "own-only-profile", "own-archived", "deleted" } select new object[] { route, state, false })
        .Concat(from route in new[] { 1, 6, 7 } from state in new[] { "visible", "hidden", "own-assigned", "own-unassigned" } select new object[] { route, state, true });

    [Theory]
    [MemberData(nameof(LegalReferencePayloadCases))]
    public async Task ClientLegalReference_UsesSeparateParentRightsAndNeverLoadsInlineAssets(int route, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; var recorder = new LegalReferenceCommands();
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options) : await CreateContextAsync(recorder);
        var seed = await SeedLegalReferenceAsync(db, state); var before = JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync());
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); http.DefaultRequestHeaders.Add("X-Forwarded-For", Guid.NewGuid().ToString("N"));
        recorder.Armed = true; using var response = await http.GetAsync(ClientReadPath(route, seed.Client)); recorder.Armed = false;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); AssertLegalReferenceJson(LegalReferenceItem(json.RootElement, route, seed.Client), seed, state, route); recorder.AssertNoInlineAssets();
        Assert.Equal(before, JsonSerializer.Serialize(await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().SingleAsync())); Assert.Equal(seed.Legal.Id, await db.Clients.Where(x => x.Id == seed.Client).Select(x => x.LegalEntityId).SingleAsync()); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    public static IEnumerable<object[]> LegalReferenceWriteCases() =>
        (from create in new[] { false, true } from state in new[] { "visible", "hidden", "denied", "own-assigned", "own-unassigned" } select new object[] { create, state, false })
        .Concat(from create in new[] { false, true } from state in new[] { "hidden", "own-assigned" } select new object[] { create, state, true });

    [Theory]
    [MemberData(nameof(LegalReferenceWriteCases))]
    public async Task ClientLegalReference_RootSaveResponseDoesNotBypassParentReadRights(bool create, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; var recorder = new LegalReferenceCommands();
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options) : await CreateContextAsync(recorder);
        var seed = await SeedLegalReferenceAsync(db, state); var before = JsonSerializer.Serialize(await db.LegalEntities.AsNoTracking().SingleAsync());
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        recorder.Armed = true; using var response = await SendRequiredClientAsync(http, 0, create, seed.Client, new Client { Name = "Разрешённое сохранение", LegalEntityId = seed.Legal.Id, ResponsibleUserId = seed.Actor.Id }); recorder.Armed = false;
        Assert.True(response.StatusCode == (create ? HttpStatusCode.Created : HttpStatusCode.OK), $"HTTP={(int)response.StatusCode};body={await response.Content.ReadAsStringAsync()}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); AssertLegalReferenceJson(json.RootElement, seed, state, 9); recorder.AssertNoInlineAssets();
        var id = json.RootElement.GetProperty("id").GetInt32(); Assert.Equal("Разрешённое сохранение", await db.Clients.Where(x => x.Id == id).Select(x => x.Name).SingleAsync()); Assert.Equal(seed.Legal.Id, await db.Clients.Where(x => x.Id == id).Select(x => x.LegalEntityId).SingleAsync());
        Assert.Equal(before, JsonSerializer.Serialize(await db.LegalEntities.AsNoTracking().SingleAsync())); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> LegalReferenceVersionedWriteCases() => from route in new[] { 1, 2 } from create in new[] { false, true } from postgres in new[] { false, true } select new object[] { route, create, postgres };

    [Theory]
    [MemberData(nameof(LegalReferenceVersionedWriteCases))]
    public async Task ClientLegalReference_VersionedSaveKeepsItsExistingUnexpandedResponse(int route, bool create, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db, "hidden"); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await SendRequiredClientAsync(http, route, create, seed.Client, new Client { Name = "Сохранение без раскрытия", LegalEntityId = seed.Legal.Id, ResponsibleUserId = seed.Actor.Id });
        Assert.Equal(create ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); AssertLegalReferenceJson(json.RootElement, seed, "hidden", route); Assert.Null(db.Database.CurrentTransaction);
    }

    private static string LegalReferenceFilter(LegalReferenceSeed seed, string field) => field switch
    {
        "search" => "search=" + seed.Legal.ShortName,
        "short" => "legalEntityShortName=" + seed.Legal.ShortName,
        "full" => "legalEntityFullName=" + seed.Legal.FullName,
        "inn" => "legalEntityInn=" + seed.Legal.Inn,
        "ogrn" => "legalEntityOgrn=" + seed.Legal.Ogrn,
        _ => "legalEntityKpp=" + seed.Legal.Kpp,
    };

    public static IEnumerable<object[]> LegalReferenceSearchCases() =>
        (from field in new[] { "search", "short", "full", "inn", "ogrn", "kpp" } from state in new[] { "visible", "hidden", "own-unassigned", "own-only-profile" } select new object[] { field, state, false })
        .Concat(from field in new[] { "search", "inn" } from state in new[] { "visible", "hidden", "own-unassigned", "own-only-profile" } select new object[] { field, state, true });

    [Theory]
    [MemberData(nameof(LegalReferenceSearchCases))]
    public async Task ClientLegalReference_SearchAndTotalDoNotRevealHiddenParentFields(string field, string state, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db, state); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await http.GetAsync("/api/v2/clients?" + LegalReferenceFilter(seed, field)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("total").GetInt32() == (LegalReferenceVisible(state) ? 1 : 0), $"field={field};state={state};hidden-field-search={json.RootElement.GetRawText()}");
        if (LegalReferenceVisible(state)) AssertLegalReferenceJson(Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()), seed, state, 7); else Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClientLegalReference_ClientTextAndKnownLinkIdRemainSearchable(bool byId, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db, "hidden");
        // SQLite's built-in lower() is ASCII-only; keep this cross-provider
        // own-field control independent of that existing provider limitation.
        await db.Clients.Where(x => x.Id == seed.Client).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "Public client reference"));
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        var filter = byId ? "legalEntityId=" + seed.Legal.Id : "search=public"; using var response = await http.GetAsync("/api/v2/clients?" + filter); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32()); AssertLegalReferenceJson(Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()), seed, "hidden", 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientLegalReference_V1ListKeepsExistingUnexpandedShape(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await http.GetAsync("/api/v1/clients"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var row = json.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == seed.Client); AssertLegalReferenceJson(row, seed, "hidden", 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ClientLegalReference_NoLinkedParentsDoNotReadCompanyRegistryOrLegalProfiles(int route)
    {
        var recorder = new LegalReferenceCommands(); await using var db = await CreateContextAsync(recorder); var (actor, _, ids) = await SeedClientReadAsync(db); recorder.Armed = true;
        Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(db, actor, ids[0], route)); recorder.Armed = false; Assert.Equal(0, recorder.RegistryReads); Assert.DoesNotContain(recorder.Commands, sql => sql.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal)); recorder.AssertNoInlineAssets();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public async Task ClientLegalReference_LoadsDistinctProfilesInOneBatchWithoutNPlusOne(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; var recorder = new LegalReferenceCommands();
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options) : await CreateContextAsync(recorder);
        var seed = await SeedLegalReferenceAsync(db); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); http.DefaultRequestHeaders.Add("X-Forwarded-For", Guid.NewGuid().ToString("N"));
        recorder.Armed = true; using var first = await http.GetAsync(ClientReadPath(route, seed.Client) + "?pageSize=500"); recorder.Armed = false; Assert.Equal(HttpStatusCode.OK, first.StatusCode); var count = recorder.Commands.Count; recorder.Commands.Clear();
        for (var i = 0; i < 49; i++) db.Clients.Add(new Client { Name = $"Batch {i}", ResponsibleUserId = seed.Actor.Id, LegalEntity = new LegalEntity { ShortName = $"Batch legal {i}", ResponsibleUserId = seed.Other.Id, LogoDataUrl = LegalReferenceSecret } });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); recorder.Armed = true; using var second = await http.GetAsync(ClientReadPath(route, seed.Client) + "?pageSize=500"); recorder.Armed = false; Assert.Equal(HttpStatusCode.OK, second.StatusCode); Assert.Equal(count, recorder.Commands.Count); recorder.AssertNoInlineAssets();
        using var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync()); var items = route == 7 ? json.RootElement.GetProperty("items") : json.RootElement; Assert.Equal(50, items.EnumerateArray().Count(x => x.GetProperty("legalEntity").ValueKind == JsonValueKind.Object)); Assert.DoesNotContain(LegalReferenceSecret, json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    public async Task ClientLegalReference_AdminCompanyProfileDoesNotRequireEmployeeAssignment(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db, "own-admin"); using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString()); using var response = await http.GetAsync(ClientReadPath(route, seed.Client)); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); AssertLegalReferenceJson(LegalReferenceItem(json.RootElement, route, seed.Client), seed, "own-admin", route);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("[]")]
    [InlineData("missing")]
    public async Task ClientLegalReference_LegacyRegistryFallbackDoesNotBypassCompanyAssignment(string value)
    {
        await using var db = await CreateContextAsync(); var seed = await SeedLegalReferenceAsync(db, "own-unassigned");
        if (value == "missing") await db.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").ExecuteDeleteAsync(); else await db.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value));
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = seed.Legal.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = Assert.IsType<OkObjectResult>(await ReadClientBoundaryAsync(db, seed.Actor, seed.Client, 1)); var client = Assert.IsType<Client>(result.Value); Assert.Equal(seed.Legal.Id, client.LegalEntityId); Assert.Null(client.LegalEntity);
    }

    public static IEnumerable<object[]> LegalReferenceDepartmentCases() =>
        (from route in new[] { 1, 6, 7, 8 } from sameDepartment in new[] { false, true } select new object[] { route, sameDepartment, false })
        .Concat(from route in new[] { 1, 7 } from sameDepartment in new[] { false, true } select new object[] { route, sameDepartment, true });

    [Theory]
    [MemberData(nameof(LegalReferenceDepartmentCases))]
    public async Task ClientLegalReference_DepartmentParentVisibilityAppliesToDetailsAndSearch(int route, bool sameDepartment, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var seed = await SeedLegalReferenceAsync(db);
        var department = await db.Users.Where(x => x.Id == seed.Actor.Id).Select(x => new { x.DepartmentId, x.Department }).SingleAsync();
        if (sameDepartment) await db.Users.Where(x => x.Id == seed.Other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.DepartmentId).SetProperty(x => x.Department, department.Department));
        await db.RolePermissions.Where(x => x.RoleId == seed.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department));
        using var server = RequiredClientServer(db, seed.Actor); using var http = server.CreateClient(); http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Actor.Id.ToString());
        using var response = await http.GetAsync(ClientReadPath(route, seed.Client) + (route == 7 ? "?search=" + seed.Legal.ShortName : "")); Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (route == 7 && !sameDepartment) { Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32()); Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray()); }
        else AssertLegalReferenceJson(LegalReferenceItem(json.RootElement, route, seed.Client), seed, sameDepartment ? "visible" : "hidden", route);
    }
}

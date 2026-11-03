using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private const decimal PrivateCaseFee = 42991.23m;
    private const string PrivateCaseOwnerNote = "Синтетические закрытые заметки владельца";
    private const string PrivateCaseUserHash = "synthetic-private-case-user-hash";

    private static async Task<(User Actor, ClientCase Source, int[] Ids)> SeedReadPrivacyAsync(PaymentContext db, bool edit, bool finance, bool legal)
    {
        var (actor, ids) = await SeedBulkCasesAsync(db);
        await GrantSectionPermissionAsync(db, actor, RolePermissionCatalog.Payments, canView: finance, canCreate: false, canEdit: false, canDelete: false);
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, edit));
        await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.PasswordHash, PrivateCaseUserHash).SetProperty(row => row.AuthTokenVersion, 57).SetProperty(row => row.LastActivityIpAddress, "192.0.2.51"));
        await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.Phone, "+79990000011").SetProperty(row => row.Email, "private-client@example.local").SetProperty(row => row.Address, "Синтетический закрытый адрес").SetProperty(row => row.Notes, PrivateCaseOwnerNote));
        if (legal)
        {
            var owner = new LegalEntity { ShortName = "Синтетическая организация", FullName = "Полное имя организации", ResponsibleUserId = actor.Id, Inn = "1234567890", Kpp = "123456789", Ogrn = "1234567890123", Address = "Синтетический закрытый адрес", Phone = "+79990000011", Email = "private-company@example.local", Director = "Синтетический директор", Notes = PrivateCaseOwnerNote };
            db.LegalEntities.Add(owner); await db.SaveChangesAsync(); await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, (int?)null).SetProperty(row => row.LegalEntityId, owner.Id));
        }
        await db.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.FeeAmount, PrivateCaseFee)); db.ChangeTracker.Clear();
        return (actor, await db.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]), ids);
    }

    public static IEnumerable<object[]> CaseReadPrivacyCases()
        => from version in new[] { "root", "v1" } from list in new[] { false, true } from legal in new[] { false, true } from edit in new[] { false, true } from finance in new[] { false, true } select new object[] { version, list, legal, edit, finance };

    [Theory]
    [MemberData(nameof(CaseReadPrivacyCases))]
    public Task CaseReadPrivacy_HttpKeepsContactsAndFinancePermissionsIndependent(string version, bool list, bool legal, bool edit, bool finance)
        => CheckCaseReadPrivacyAsync(version, list, legal, edit, finance, postgres: false);

    [Theory]
    [InlineData("root", false, false, false, false)]
    [InlineData("root", true, true, true, false)]
    [InlineData("v1", false, true, false, true)]
    [InlineData("v1", true, false, true, true)]
    public Task CaseReadPrivacy_PgKeepsTheSamePermissionBoundaries(string version, bool list, bool legal, bool edit, bool finance)
        => CheckCaseReadPrivacyAsync(version, list, legal, edit, finance, postgres: true);

    private async Task CheckCaseReadPrivacyAsync(string version, bool list, bool legal, bool edit, bool finance, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null; await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync();
        var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit, finance, legal); var route = version == "root" ? "cases" : "v1/cases";
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.GetAsync($"/api/{route}{(list ? "" : $"/{source.Id}")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadFromJsonAsync<JsonElement>(); var row = list ? body.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == source.Id) : body;
        var owner = row.GetProperty(legal ? "legalEntity" : "client"); var phone = owner.GetProperty("phone").GetString(); var notes = owner.GetProperty("notes").GetString(); var fee = row.GetProperty("feeAmount").GetDecimal();
        Assert.True((edit || string.IsNullOrEmpty(phone) && string.IsNullOrEmpty(notes)) && fee == (finance ? PrivateCaseFee : 0m), $"HTTP {(int)response.StatusCode}; phoneRevealed={!string.IsNullOrEmpty(phone)}; ownerNotesRevealed={!string.IsNullOrEmpty(notes)}; fee={fee}; edit={edit}; finance={finance}");
        Assert.Equal(edit, row.GetProperty("canRevealSensitiveData").GetBoolean()); Assert.Equal(edit ? "+79990000011" : legal ? null : "", phone); Assert.Equal(edit ? PrivateCaseOwnerNote : legal ? null : "", notes);
        Assert.Equal(edit ? "Синтетический закрытый адрес" : legal ? null : "", owner.GetProperty("address").GetString());
        if (legal) { Assert.Equal(edit ? "1234567890" : null, owner.GetProperty("inn").GetString()); Assert.Equal(edit ? "Синтетический директор" : null, owner.GetProperty("director").GetString()); Assert.Equal("Синтетическая организация", owner.GetProperty("shortName").GetString()); }
        else Assert.Equal("Синтетический клиент", owner.GetProperty("name").GetString());
        Assert.Equal(PrivateCaseFee, (await db.ClientCases.AsNoTracking().SingleAsync(item => item.Id == source.Id)).FeeAmount); Assert.Equal(PrivateCaseOwnerNote, legal ? (await db.LegalEntities.AsNoTracking().SingleAsync()).Notes : (await db.Clients.AsNoTracking().SingleAsync()).Notes); Assert.False(db.ChangeTracker.HasChanges()); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseReadPrivacy_V2CompanyNotesFollowExistingContactPermission(bool edit)
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit, finance: false, legal: true);
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.GetAsync($"/api/v2/cases/{source.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var row = await response.Content.ReadFromJsonAsync<JsonElement>(); var notes = row.GetProperty("legalEntity").GetProperty("notes").GetString();
        Assert.True(edit || string.IsNullOrEmpty(notes), $"HTTP {(int)response.StatusCode}; companyNotesRevealed={!string.IsNullOrEmpty(notes)}; edit={edit}"); Assert.Equal(edit ? PrivateCaseOwnerNote : null, notes); Assert.Equal(edit, row.GetProperty("canRevealSensitiveData").GetBoolean()); Assert.Equal(PrivateCaseOwnerNote, (await db.LegalEntities.AsNoTracking().SingleAsync()).Notes); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData("v1", false, false)]
    [InlineData("v1", false, true)]
    [InlineData("v1", true, false)]
    [InlineData("v1", true, true)]
    [InlineData("v2", false, false)]
    [InlineData("v2", false, true)]
    public async Task CaseReadPrivacy_HttpKeepsResponsibleCaptionWithoutSecurityFields(string version, bool list, bool edit)
    {
        await using var db = await CreateContextAsync(); var (actor, source, ids) = await SeedReadPrivacyAsync(db, edit, finance: true, legal: false);
        using var server = CaseWriteServer(db, actor); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString()); using var response = await client.GetAsync($"/api/{version}/cases{(list ? "" : $"/{source.Id}")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await response.Content.ReadFromJsonAsync<JsonElement>(); var row = list ? body.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == source.Id) : body; var user = row.GetProperty("responsibleUser");
        var hash = user.TryGetProperty("passwordHash", out var value) ? value.GetString() : null; var tokenVersion = user.TryGetProperty("authTokenVersion", out value) ? value.GetInt32() : 0;
        Assert.True(string.IsNullOrEmpty(hash) && tokenVersion == 0, $"HTTP {(int)response.StatusCode}; responsibleHashRevealed={!string.IsNullOrEmpty(hash)}; authVersionRevealed={tokenVersion != 0}");
        Assert.Equal(actor.Id, user.GetProperty("id").GetInt32()); Assert.Equal(actor.FullName, user.GetProperty("fullName").GetString()); Assert.Equal(actor.Email, user.GetProperty("email").GetString()); Assert.Equal(PrivateCaseUserHash, (await db.Users.AsNoTracking().SingleAsync()).PasswordHash); await AssertBulkCasesUnchangedAsync(db, ids);
    }
}

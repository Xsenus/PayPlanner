using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContactDirectoryPaging_ReturnsOnlyRequestedExternalPageWithSqlLimit(bool postgres)
    {
        var recorder = new RecordingCommandInterceptor();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options)
            : await PassportBoundaryContextAsync(recorder);
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        var ids = await SeedDirectoryPagingContactsAsync(db, actor.Id, 5);
        recorder.Commands.Clear();
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        using var response = await http.GetAsync("/api/contacts?scope=external&page=2&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<ContactDirectoryItemDto>>();
        Assert.Equal(ids.Skip(2).Take(2), rows!.Select(x => x.SourceId));
        Assert.All(rows, row => Assert.Equal("external", row.SourceType));
        var sql = Assert.Single(recorder.Commands, command => command.Contains("FROM \"ExternalContacts\"", StringComparison.Ordinal));
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        using var denied = await http.GetAsync("/api/contacts?scope=external&page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ContactDirectoryPaging_SearchesBeforePagingAndRetainsEqualNames()
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        var ids = await SeedDirectoryPagingContactsAsync(db, actor.Id, 6);
        await db.ExternalContacts.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.FullName, "Same name"));
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        var first = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&search=same%20name&page=1&pageSize=3");
        var second = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&search=same%20name&page=2&pageSize=3");

        Assert.Equal(ids.Take(3), first!.Select(x => x.SourceId));
        Assert.Equal(ids.Skip(3), second!.Select(x => x.SourceId));
        Assert.Empty(first.Select(x => x.SourceId).Intersect(second.Select(x => x.SourceId)));
    }

    [Fact]
    public async Task ContactDirectoryPaging_PostgreSqlSearchesCyrillicAcrossPagesWithEqualNames()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        var ids = await SeedDirectoryPagingContactsAsync(db, actor.Id, 6);
        await db.ExternalContacts.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.FullName, "Одно имя"));
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        var first = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&search=" + Uri.EscapeDataString("одно имя") + "&page=1&pageSize=3");
        var second = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&search=" + Uri.EscapeDataString("одно имя") + "&page=2&pageSize=3");

        Assert.Equal(ids.Take(3), first!.Select(x => x.SourceId));
        Assert.Equal(ids.Skip(3), second!.Select(x => x.SourceId));
        Assert.All(first.Concat(second), row => Assert.Equal("Одно имя", row.FullName));
    }

    [Theory]
    [InlineData("scope=all&page=1&pageSize=2")]
    [InlineData("scope=clients&page=1")]
    [InlineData("scope=external&page=2147483647&pageSize=200")]
    [InlineData("scope=external&page=invalid&pageSize=2")]
    public async Task ContactDirectoryPaging_RejectsUnsupportedOrInvalidPage(string query)
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        using var response = await http.GetAsync("/api/contacts?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("legal-only")]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    public async Task ContactDirectoryPaging_RequiresCurrentClientReadAccess(string state)
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        await SeedDirectoryPagingContactsAsync(db, actor.Id, 3);
        if (state == "legal-only") await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        else if (state == "unapproved") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        else await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        using var response = await http.GetAsync("/api/contacts?scope=external&page=1&pageSize=2");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ContactDirectoryPaging_CapsPageSizeAndKeepsUnpagedCompatibility()
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        var ids = await SeedDirectoryPagingContactsAsync(db, actor.Id, 205);
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        var page = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&page=0&pageSize=9999");
        var unpaged = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external");

        Assert.Equal(ids.Take(200), page!.Select(x => x.SourceId));
        Assert.Equal(ids, unpaged!.Select(x => x.SourceId));
    }

    [Theory]
    [InlineData("pageSize=2", 2)]
    [InlineData("page=-10&pageSize=0", 1)]
    [InlineData("page=2", 5)]
    [InlineData("page=100&pageSize=2", 0)]
    public async Task ContactDirectoryPaging_DefaultsAndBoundsPreserveArrayContract(string query, int expectedCount)
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        await SeedDirectoryPagingContactsAsync(db, actor.Id, 55);
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        var rows = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&" + query);

        Assert.Equal(expectedCount, rows!.Count);
    }

    [Fact]
    public async Task ContactDirectoryPaging_AppliesVisibilityAndActiveSyncFiltersBeforeLimit()
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        var own = await SeedDirectoryPagingContactsAsync(db, actor.Id, 5);
        var other = await CreateUserAsync(db, "directory-other@example.local", "Другой владелец");
        await SeedDirectoryPagingContactsAsync(db, other.Id, 3);
        await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own));
        await db.ExternalContacts.Where(x => x.Id == own[0]).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await db.ExternalContacts.Where(x => x.Id == own[1]).ExecuteUpdateAsync(s => s.SetProperty(x => x.SyncToPhonebook, false));
        using var server = ClientContactServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        var rows = await http.GetFromJsonAsync<List<ContactDirectoryItemDto>>("/api/contacts?scope=external&syncOnly=true&page=1&pageSize=2");

        Assert.Equal(own.Skip(2).Take(2), rows!.Select(x => x.SourceId));
    }

    [Fact]
    public async Task ContactDirectoryPaging_RechecksViewPermissionAfterHttpFilters()
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        await SeedDirectoryPagingContactsAsync(db, actor.Id, 3);
        using var server = ClientContactServer(db, async () =>
            await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false)));
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        using var response = await http.GetAsync("/api/contacts?scope=external&page=1&pageSize=2");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("clients")]
    [InlineData("external")]
    [InlineData("people")]
    [InlineData("organizations")]
    public async Task ContactDirectoryPaging_UnpagedReadsAlsoRequireCurrentApproval(string scope)
    {
        await using var db = await PassportBoundaryContextAsync();
        var (actor, _, _, _) = await SeedClientContactBoundaryAsync(db);
        using var server = ClientContactServer(db, async () =>
            await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false)));
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());

        using var response = await http.GetAsync("/api/contacts?scope=" + scope);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    private static async Task<int[]> SeedDirectoryPagingContactsAsync(PaymentContext db, int actorId, int count)
    {
        var rows = Enumerable.Range(1, count).Select(index => new ExternalContact
        {
            FullName = $"Участник {index:000}",
            ResponsibleUserId = actorId,
            Phone = $"700000{index:00000}",
        }).ToArray();
        db.ExternalContacts.AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(x => x.Id).ToArray();
    }
}

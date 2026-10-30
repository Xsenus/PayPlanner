using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("admin", HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("editor", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("unassigned", HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("denied", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("unapproved", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("inactive", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("deleted-user", HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("ordinary", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData("deleted-company", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    public async Task CompanyBranches_HttpUsesFreshActorProfilePermissionAndOwnCompany(
        string role, HttpStatusCode readStatus, HttpStatusCode createStatus, HttpStatusCode updateStatus)
    {
        await using var db = await CreateContextAsync();
        var fixture = await CompanyDeleteFixture(db, true);
        var actor = role is "viewer" or "editor" or "unassigned" or "denied" ? fixture.Staff : fixture.Actor;
        var user = await db.Users.SingleAsync(row => row.Id == actor);
        if (role is "viewer" or "editor" or "unassigned" or "denied")
            db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = role != "denied", CanEdit = role == "editor" });
        if (role == "unassigned") db.UserCompanyAccesses.Remove(await db.UserCompanyAccesses.SingleAsync(row => row.UserId == actor && row.LegalEntityId == fixture.Company));
        if (role == "unapproved") user.IsApproved = false;
        if (role == "inactive") user.IsActive = false;
        if (role == "deleted-user") db.Users.Remove(user);
        if (role == "ordinary") (await db.SystemSettings.SingleAsync(row => row.Key == "legalEntities.mainCompanyIds")).Value = $"[{fixture.Other}]";
        if (role == "deleted-company") db.Entry(await db.LegalEntities.SingleAsync(row => row.Id == fixture.Company)).Property<bool>("IsDeleted").CurrentValue = true;
        (await db.CompanyBranches.SingleAsync()).Notes = "Private branch notes";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await CompanyDeleteSnapshot(db);
        var protectedBefore = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(db))!.AsObject(); protectedBefore.Remove("branches");
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("company-fixture").AddScheme<AuthenticationSchemeOptions, CompanyFixtureAuthentication>("company-fixture", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
                services.AddControllers().AddApplicationPart(typeof(CompanyBranchesController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        if (role != "anonymous") client.DefaultRequestHeaders.Add("X-Fixture-User", actor.ToString(CultureInfo.InvariantCulture));
        var url = $"/api/legal-entities/{fixture.Company}/branches";
        using var list = await client.GetAsync(url + "?includeArchived=true");
        var listed = readStatus == HttpStatusCode.OK ? await list.Content.ReadFromJsonAsync<List<PayPlanner.Api.Models.Responses.CompanyBranchDto>>() : null;
        using var effective = await client.GetAsync(url + $"/{fixture.Branch}/effective-requisites");
        using var create = await client.PostAsJsonAsync(url, new { name = "New branch" });
        db.ChangeTracker.Clear();
        using var update = await client.PutAsJsonAsync(url + $"/{fixture.Branch}", new { name = "Renamed branch", expectedVersion = listed?.Single().EditVersion });
        Assert.Equal(readStatus, list.StatusCode); Assert.Equal(readStatus, effective.StatusCode);
        Assert.Equal(createStatus, create.StatusCode); Assert.Equal(updateStatus, update.StatusCode);
        if (readStatus != HttpStatusCode.OK)
        {
            Assert.DoesNotContain("Private branch notes", await list.Content.ReadAsStringAsync());
            Assert.DoesNotContain("Historical branch", await effective.Content.ReadAsStringAsync());
        }
        if (createStatus != HttpStatusCode.Created) Assert.Equal(before, await CompanyDeleteSnapshot(db));
        var protectedAfter = JsonNode.Parse(await CompanyDeleteProtectedSnapshot(db))!.AsObject(); protectedAfter.Remove("branches");
        Assert.Equal(protectedBefore.ToJsonString(), protectedAfter.ToJsonString());
    }
}

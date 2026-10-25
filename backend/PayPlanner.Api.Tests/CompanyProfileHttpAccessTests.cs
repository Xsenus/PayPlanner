using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Fact]
    public async Task CompanyProfile_EditingRequisitesPreservesClientRelations()
    {
        await using var db = await CreateContextAsync();
        var id = await CreateUserAsync(db);
        var companyId = await CreateLegalEntityAsync(db, "Own company");
        var user = await db.Users.SingleAsync(item => item.Id == id);
        var linked = new Client { Name = "Private linked client", LegalEntityId = companyId };
        var other = new Client { Name = "Unrelated client" };
        db.Clients.AddRange(linked, other);
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = true });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = id, LegalEntityId = companyId });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = companyId.ToString() });
        await db.SaveChangesAsync();
        var result = await CreateController(db, id, isAdmin: false).Update(companyId,
            new PayPlanner.Api.Models.Requests.LegalEntityRequest { ShortName = "New requisites", ClientIds = [other.Id] }, CancellationToken.None);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        Assert.Equal(companyId, (await db.Clients.SingleAsync(item => item.Id == linked.Id)).LegalEntityId);
        Assert.Null((await db.Clients.SingleAsync(item => item.Id == other.Id)).LegalEntityId);
    }

    [Fact]
    public async Task CompanyProfile_DuplicateInnDoesNotDiscloseAnotherCompany()
    {
        await using var db = await CreateContextAsync();
        var id = await CreateUserAsync(db);
        var user = await db.Users.SingleAsync(item => item.Id == id);
        var companyId = await CreateLegalEntityAsync(db, "Own company");
        var foreignId = await CreateLegalEntityAsync(db, "Foreign secret company");
        (await db.LegalEntities.SingleAsync(item => item.Id == foreignId)).Inn = "1234567890";
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = true });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = id, LegalEntityId = companyId });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{companyId},{foreignId}]" });
        await db.SaveChangesAsync();
        var result = await CreateController(db, id, isAdmin: false).Update(companyId,
            new PayPlanner.Api.Models.Requests.LegalEntityRequest { ShortName = "Forbidden rename", Inn = "1234567890" }, CancellationToken.None);
        var conflict = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(result.Result);
        Assert.Equal("Юридическое лицо с таким ИНН уже существует.", conflict.Value);
        Assert.Equal("Own company", (await db.LegalEntities.SingleAsync(item => item.Id == companyId)).ShortName);
    }

    [Theory]
    [InlineData("none", HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    [InlineData("editor", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData("foreign", HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData("owner", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData("anonymous", HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    public async Task CompanyProfile_HttpEnforcesReadEditAndForeignCompanyBoundary(string role, HttpStatusCode profileStatus, HttpStatusCode detailStatus, HttpStatusCode updateStatus)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateUserAsync(db, role == "owner" ? "admin" : null);
        var companyId = await CreateLegalEntityAsync(db, "Hidden company");
        var company = await db.LegalEntities.SingleAsync(item => item.Id == companyId);
        company.SealDataUrl = "data:image/png;base64,QUFB";
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = companyId.ToString() });
        var user = await db.Users.SingleAsync(item => item.Id == id);
        if (role is "viewer" or "editor" or "foreign") db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = role != "viewer" });
        if (role is "viewer" or "editor") db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = id, LegalEntityId = companyId });
        await db.SaveChangesAsync();

        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddSingleton<ILegalEntityEnrichmentService>(new NoopLegalEntityEnrichmentService());
                services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
                services.AddAuthentication("company-fixture").AddScheme<AuthenticationSchemeOptions, CompanyFixtureAuthentication>("company-fixture", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
                services.AddControllers().AddApplicationPart(typeof(LegalEntitiesController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        if (role != "anonymous") client.DefaultRequestHeaders.Add("X-Fixture-User", id.ToString(CultureInfo.InvariantCulture));
        using var profile = await client.GetAsync("/api/legal-entities/company-profile");
        using var detail = await client.GetAsync($"/api/legal-entities/{companyId}");
        using var update = await client.PutAsJsonAsync($"/api/legal-entities/{companyId}", new { shortName = "Changed by granted editor" });
        Assert.Equal(profileStatus, profile.StatusCode); Assert.Equal(detailStatus, detail.StatusCode); Assert.Equal(updateStatus, update.StatusCode);
        if (profileStatus != HttpStatusCode.OK) Assert.DoesNotContain("QUFB", await profile.Content.ReadAsStringAsync());
        if (detailStatus != HttpStatusCode.OK) Assert.DoesNotContain("Hidden company", await detail.Content.ReadAsStringAsync());
        Assert.Equal(updateStatus == HttpStatusCode.OK ? "Changed by granted editor" : "Hidden company", company.ShortName);
        if (role != "anonymous")
        {
            using var options = await client.GetAsync("/api/own-companies/options");
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            Assert.DoesNotContain("QUFB", await options.Content.ReadAsStringAsync());
            if (role != "owner")
            {
                using var adminUpdate = await client.PutAsJsonAsync("/api/legal-entities/main-companies/settings", new { companyIds = Array.Empty<int>() });
                Assert.Equal(HttpStatusCode.Forbidden, adminUpdate.StatusCode);
            }
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task CompanyProfile_ReconciliationUsesOnlyAuthorizedDocumentParty(bool visibleClient, bool finance, bool expected)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateUserAsync(db);
        var user = await db.Users.SingleAsync(item => item.Id == id);
        var companyId = await CreateLegalEntityAsync(db, "Document issuer");
        var company = await db.LegalEntities.SingleAsync(item => item.Id == companyId);
        company.SealDataUrl = "data:image/png;base64,QUFB"; company.CardsJson = "private-cards";
        var client = new Client { Name = "Allowed client", ResponsibleUserId = visibleClient ? id : null };
        db.Clients.Add(client);
        db.RolePermissions.AddRange(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, ViewScope = DataVisibilityScope.Own }, new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Acts, CanView = finance });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyId", Value = companyId.ToString() });
        await db.SaveChangesAsync();
        var controller = new OwnCompaniesController(db) { ControllerContext = CreateController(db, id, isAdmin: false).ControllerContext };
        var response = await controller.GetReconciliationParty(client.Id, CancellationToken.None);
        Assert.Equal(expected, response.Result is Microsoft.AspNetCore.Mvc.OkObjectResult);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ForbidResult>((await CreateController(db, id, isAdmin: false).GetCompanyProfile(CancellationToken.None)).Result);
        if (expected)
        {
            var value = Assert.IsType<OwnCompanyReconciliationParty>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result).Value);
            Assert.Equal("Document issuer", value.ShortName); Assert.Equal(company.SealDataUrl, value.SealDataUrl);
            Assert.DoesNotContain("private-cards", System.Text.Json.JsonSerializer.Serialize(value));
        }
    }

    private sealed class CompanyFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "admin")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

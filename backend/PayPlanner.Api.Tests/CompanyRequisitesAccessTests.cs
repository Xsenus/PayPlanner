using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("owner", "own", HttpStatusCode.OK)]
    [InlineData("owner", "counterparty", HttpStatusCode.OK)]
    [InlineData("owner", "missing", HttpStatusCode.NotFound)]
    [InlineData("viewer", "own", HttpStatusCode.OK)]
    [InlineData("editor", "own", HttpStatusCode.OK)]
    [InlineData("viewer", "foreign", HttpStatusCode.NotFound)]
    [InlineData("viewer", "counterparty", HttpStatusCode.NotFound)]
    [InlineData("viewer", "missing", HttpStatusCode.NotFound)]
    [InlineData("no-view", "own", HttpStatusCode.NotFound)]
    [InlineData("no-assignment", "own", HttpStatusCode.NotFound)]
    [InlineData("finance-only", "own", HttpStatusCode.NotFound)]
    [InlineData("inactive", "own", HttpStatusCode.NotFound)]
    [InlineData("unapproved", "own", HttpStatusCode.NotFound)]
    [InlineData("anonymous", "own", HttpStatusCode.Unauthorized)]
    public async Task RequisitesAccess_HttpUsesCurrentProfilePermissionAndAssignment(string role, string target, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync();
        var userId = await CreateUserAsync(db, role == "owner" ? "admin" : null);
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        var own = await CreateLegalEntityAsync(db, "Own private company");
        var foreign = await CreateLegalEntityAsync(db, "Other private company");
        var counterparty = await CreateLegalEntityAsync(db, "Counterparty");
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{own},{foreign}]" });
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = role != "no-view" && role != "finance-only", CanEdit = role == "editor" });
        if (role != "no-assignment") db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = userId, LegalEntityId = own });
        if (role == "finance-only") db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Accounts, CanView = true, CanEdit = true });
        user.IsActive = role != "inactive";
        user.IsApproved = role != "unapproved";
        var company = await db.LegalEntities.SingleAsync(item => item.Id == own);
        company.Inn = "invalid";
        company.SealDataUrl = "data:image/png;base64,PRIVATE_IMAGE";
        company.DocumentDirectorJson = "private-signer";
        await db.SaveChangesAsync();
        var id = target switch { "foreign" => foreign, "counterparty" => counterparty, "missing" => int.MaxValue, _ => own };
        using var server = CreateRequisitesServer(db);
        using var client = server.CreateClient();
        if (role != "anonymous") client.DefaultRequestHeaders.Add("X-Fixture-User", userId.ToString(CultureInfo.InvariantCulture));

        using var response = await client.GetAsync($"/api/legal-entities/{id}/requisites-check");

        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE_IMAGE", body);
        Assert.DoesNotContain("private-signer", body);
        Assert.DoesNotContain("private company", body);
        if (expected == HttpStatusCode.OK)
        {
            var result = await response.Content.ReadFromJsonAsync<RequisitesCheckResponse>();
            Assert.NotNull(result);
            Assert.Equal(id, result.LegalEntityId);
            if (id == own) Assert.Contains(result.Issues, item => item.Field == "inn");
        }
        else Assert.DoesNotContain("issues", body);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal("invalid", company.Inn);
        Assert.Equal("data:image/png;base64,PRIVATE_IMAGE", company.SealDataUrl);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("assignment")]
    [InlineData("registry")]
    [InlineData("role")]
    public async Task RequisitesAccess_HttpRechecksRevocationWithAnOldAdminClaim(string change)
    {
        await using var db = await CreateContextAsync();
        var id = await CreateUserAsync(db);
        var user = await db.Users.SingleAsync(item => item.Id == id);
        var company = await CreateLegalEntityAsync(db, "Private own company");
        var permission = new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true };
        var assignment = new UserCompanyAccess { UserId = id, LegalEntityId = company };
        var registry = new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{company}]" };
        db.AddRange(permission, assignment, registry);
        await db.SaveChangesAsync();
        using var server = CreateRequisitesServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", id.ToString(CultureInfo.InvariantCulture));
        var path = $"/api/legal-entities/{company}/requisites-check";
        using var before = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        if (change == "permission") permission.CanView = false;
        if (change == "assignment") db.UserCompanyAccesses.Remove(assignment);
        if (change == "registry") registry.Value = "[]";
        if (change == "role")
        {
            var role = new Role { Name = "replacement-role", Description = "No company profile permission" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            user.RoleId = role.Id;
        }
        await db.SaveChangesAsync();

        using var after = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.DoesNotContain("issues", await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequisitesAccess_DoesNotReadImagesOrUnrelatedProfileColumns()
    {
        var capture = new RequisitesQueryCapture();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: capture);
        var id = await CreateUserAsync(db, "admin");
        var companyId = await CreateLegalEntityAsync(db, "Own company");
        capture.Queries.Clear();

        var response = await CreateController(db, id).CheckRequisites(companyId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var query = Assert.Single(capture.Queries.Where(sql => sql.Contains("FROM \"LegalEntities\"", StringComparison.Ordinal)));
        foreach (var field in new[] { "LogoDataUrl", "SealDataUrl", "DirectorSignatureDataUrl", "DocumentDirectorJson", "ChiefAccountantJson", "CardsJson", "CashRegistersJson" }) Assert.DoesNotContain(field, query);
        Assert.Contains("BankAccountsJson", query);
    }

    private static TestServer CreateRequisitesServer(PayPlanner.Api.Data.PaymentContext db) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(db);
            services.AddSingleton<ILegalEntityEnrichmentService>(new NoopLegalEntityEnrichmentService());
            services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
            services.AddAuthentication("company-fixture").AddScheme<AuthenticationSchemeOptions, CompanyFixtureAuthentication>("company-fixture", _ => { });
            services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
            services.AddControllers().AddApplicationPart(typeof(LegalEntitiesController).Assembly);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class RequisitesQueryCapture : DbCommandInterceptor
    {
        public List<string> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Queries.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

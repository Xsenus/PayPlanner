using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    private static TestServer OrganizationDirectoryServer(PaymentContext db) => new(new WebHostBuilder()
        .UseEnvironment("Testing")
        .UseSetting("Email:Enabled", "false")
        .UseSetting("BackgroundServices:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddSingleton<ILegalEntityEnrichmentService>(new NoopLegalEntityEnrichmentService());
            services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
            services.AddAuthentication("directory").AddScheme<AuthenticationSchemeOptions, ProfilePrintAuthentication>("directory", _ => { });
            services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
            services.AddControllers().AddApplicationPart(typeof(LegalEntitiesController).Assembly);
        })
        .Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    [Theory]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    [InlineData("no-view")]
    [InlineData("missing-user")]
    public async Task OrganizationDirectoryBaseline_PagedSourceRequiresCurrentApprovedView(string state)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, actor, false, false, false);
        await CreateLegalEntityAsync(db, "Visible organization", actor);
        if (state == "unapproved") await db.Users.Where(x => x.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        else if (state == "inactive") await db.Users.Where(x => x.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        else if (state == "no-view") await db.RolePermissions.Where(x => x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", (state == "missing-user" ? int.MaxValue : actor).ToString());

        using var response = await http.GetAsync("/api/legal-entities/paged?page=1&pageSize=50");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task OrganizationDirectoryBaseline_RecordsRichResponseCost()
    {
        var recorder = new OrganizationDirectoryQueries();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: recorder);
        var actor = await CreateUserAsync(db, "admin");
        db.LegalEntities.AddRange(Enumerable.Range(1, 50).Select(index => new LegalEntity
        {
            ShortName = $"Company {index:000}",
            FullName = $"Full company {index:000}",
            ResponsibleUserId = actor,
            Notes = new string('x', 4000),
        }));
        await db.SaveChangesAsync();
        recorder.Commands.Clear();
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());

        using var response = await http.GetAsync("/api/legal-entities/paged?page=1&pageSize=50");
        var body = await response.Content.ReadAsByteArrayAsync();
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, items.GetArrayLength());
        Assert.True(items[0].EnumerateObject().Count() > 6);
        Console.WriteLine("ORGANIZATION_RICH_BASELINE " + JsonSerializer.Serialize(new
        {
            Queries = recorder.Commands.Count,
            Bytes = body.Length,
            Properties = items[0].EnumerateObject().Select(x => x.Name).ToArray(),
        }));
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("inactive")]
    [InlineData("no-view")]
    [InlineData("missing-user")]
    [InlineData("missing-section")]
    public async Task OrganizationDirectory_CurrentAccessRefusesWithoutReturningRows(string state)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, actor, false, false, false);
        await CreateLegalEntityAsync(db, "Private company", actor);
        if (state == "unapproved") await db.Users.Where(x => x.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        else if (state == "inactive") await db.Users.Where(x => x.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        else if (state == "no-view") await db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false));
        else if (state == "missing-section") await db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Section, RolePermissionCatalog.Clients));
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", (state == "missing-user" ? int.MaxValue : actor).ToString());

        using var response = await http.GetAsync("/api/legal-entities/directory");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Private company", await response.Content.ReadAsStringAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("paged")]
    [InlineData("directory")]
    public async Task OrganizationDirectory_UnauthenticatedRequestIsRejected(string route)
    {
        await using var db = await CreateContextAsync();
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        using var response = await http.GetAsync($"/api/legal-entities/{route}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("own", 1)]
    [InlineData("department", 2)]
    [InlineData("all", 3)]
    [InlineData("legacy", 1)]
    [InlineData("admin", 3)]
    public async Task OrganizationDirectory_UsesCurrentVisibilityAndExcludesOwnCompanies(string scope, int expectedCount)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, scope == "admin" ? "admin" : null);
        var colleague = await CreateUserAsync(db);
        var outsider = await CreateUserAsync(db);
        await db.Users.Where(x => x.Id == actor || x.Id == colleague).ExecuteUpdateAsync(s => s.SetProperty(x => x.Department, "Directory team"));
        if (scope != "legacy")
        {
            await SetLegalEntityPermissionAsync(db, actor, false, false, false);
            await db.RolePermissions.ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, scope));
        }
        var owned = await CreateLegalEntityAsync(db, "A owned", actor);
        var team = await CreateLegalEntityAsync(db, "B colleague", colleague);
        var other = await CreateLegalEntityAsync(db, "C outsider", outsider);
        var ownCompany = await CreateLegalEntityAsync(db, "Z main company", actor);
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{ownCompany}]" });
        await db.SaveChangesAsync();
        var controller = CreateController(db, actor, isAdmin: true);

        var action = await controller.GetDirectory(null);
        var page = Assert.IsType<PagedResponse<LegalEntitiesController.DirectoryItem>>(Assert.IsType<OkObjectResult>(action.Result).Value);

        Assert.Equal(expectedCount, page.Total);
        Assert.Equal(new[] { owned, team, other }.Take(expectedCount), page.Items.Select(x => x.Id));
        Assert.DoesNotContain(page.Items, x => x.Id == ownCompany);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("email")]
    [InlineData("fullName")]
    [InlineData("inn")]
    [InlineData("kpp")]
    [InlineData("ogrn")]
    [InlineData("director")]
    [InlineData("address")]
    [InlineData("shortName")]
    public async Task OrganizationDirectory_SearchesEverySupportedField(string field)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        var id = await CreateLegalEntityAsync(db, "Plain company");
        var entity = await db.LegalEntities.FindAsync(id);
        var property = typeof(LegalEntity).GetProperties().Single(x => string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase));
        property.SetValue(entity, "CaseSensitive-Needle");
        await CreateLegalEntityAsync(db, "Unrelated company");
        await db.SaveChangesAsync();
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());

        using var response = await http.GetAsync("/api/legal-entities/directory?search=%20casesensitive-needle%20");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(id, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(1, 0, 1, 1, HttpStatusCode.OK)]
    [InlineData(0, 500, 1, 200, HttpStatusCode.OK)]
    [InlineData(3, 50, 3, 50, HttpStatusCode.OK)]
    [InlineData(int.MaxValue, 200, 0, 0, HttpStatusCode.BadRequest)]
    public async Task OrganizationDirectory_PagingBoundaries(int page, int size, int normalizedPage, int normalizedSize, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        await CreateLegalEntityAsync(db, "Company");
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());
        using var response = await http.GetAsync($"/api/legal-entities/directory?page={page}&pageSize={size}");
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) return;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(normalizedPage, json.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(normalizedSize, json.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(page > 1 ? 0 : 1, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrganizationDirectory_StablePagingAndRevocationOnExistingServer(bool postgres)
    {
        await using var template = new PostgreSqlModelTemplate();
        await using var database = postgres ? await template.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var actor = await CreateUserAsync(db);
        await SetLegalEntityPermissionAsync(db, actor, false, false, false);
        db.LegalEntities.AddRange(Enumerable.Range(1, 51).Select(i => new LegalEntity { ShortName = "Same name", Email = $"lookup-{i}@example.test", ResponsibleUserId = actor }));
        await db.SaveChangesAsync();
        var ids = await db.LegalEntities.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());
        using var first = await http.GetAsync("/api/legal-entities/directory");
        using var second = await http.GetAsync("/api/legal-entities/directory?page=2");
        using var json1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var json2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(ids.Take(50), json1.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.Equal(ids[50], Assert.Single(json2.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        using var search = await http.GetAsync("/api/legal-entities/directory?search=LOOKUP-51");
        using var found = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal(ids[50], Assert.Single(found.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        await db.Users.Where(x => x.Id == actor).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false));
        using var denied = await http.GetAsync("/api/legal-entities/directory?page=2");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task OrganizationDirectory_BoundedSqlProjectionAvoidsRichRows()
    {
        var recorder = new OrganizationDirectoryQueries();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: recorder);
        var actor = await CreateUserAsync(db, "admin");
        db.LegalEntities.AddRange(Enumerable.Range(1, 51).Select(i => new LegalEntity { ShortName = $"Company {i:000}", FullName = $"Full company {i:000}", Notes = new string('x', 4000), ResponsibleUserId = actor }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        recorder.Commands.Clear();
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());
        using var response = await http.GetAsync("/api/legal-entities/directory");
        var body = await response.Content.ReadAsByteArrayAsync();
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(51, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(50, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            Assert.Equal(new[] { "id", "shortName", "fullName", "inn", "phone", "email" }, item.EnumerateObject().Select(x => x.Name));
        Assert.Equal(4, recorder.Commands.Count);
        var pageSql = recorder.Commands.Last();
        Assert.Contains("LIMIT", pageSql);
        Assert.DoesNotContain("Notes", pageSql);
        Assert.DoesNotContain("Payments", pageSql);
        Assert.DoesNotContain("JOIN", pageSql);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.True(body.Length < 15000);
        Console.WriteLine("ORGANIZATION_DIRECTORY_COST " + JsonSerializer.Serialize(new { Queries = recorder.Commands.Count, Bytes = body.Length }));
    }

    [Theory]
    [InlineData("client")]
    [InlineData("supplier")]
    public async Task OrganizationDirectory_KeepsCounterpartyRoleFilter(string role)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        var statusId = await CreateClientStatusAsync(db, "Supplier", ClientStatusSystemKeys.Supplier);
        var company = await CreateLegalEntityAsync(db, "Supplier only", clientStatusId: statusId);
        using var server = OrganizationDirectoryServer(db);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Profile-Print-User", actor.ToString());
        using var response = await http.GetAsync($"/api/legal-entities/directory?counterpartyRole={role}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(role == "supplier" ? 1 : 0, json.RootElement.GetProperty("total").GetInt32());
        if (role == "supplier") Assert.Equal(company, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task OrganizationDirectory_CancelledReadDoesNotContinueOrMutate()
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        await CreateLegalEntityAsync(db, "Company");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var controller = CreateController(db, actor);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.GetDirectory(null, ct: cancellation.Token));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private sealed class OrganizationDirectoryQueries : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}

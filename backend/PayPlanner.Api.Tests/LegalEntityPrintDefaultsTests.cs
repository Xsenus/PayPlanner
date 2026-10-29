using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    private static CompanyDocumentImagePrintDefaultsRequest PrintDefaultsRequest(int mask) => new()
    {
        Invoice = new() { IncludeLogo = (mask & 1) != 0, IncludeSeal = (mask & 2) != 0, IncludeSignature = (mask & 4) != 0 },
        Act = new() { IncludeLogo = false, IncludeSeal = true, IncludeSignature = false },
        Contract = new() { IncludeLogo = true, IncludeSeal = false, IncludeSignature = true },
    };

    private static async Task<(int UserId, int CompanyId)> PrintDefaultsProfileFixture(PaymentContext db, bool canEdit = true, bool assigned = true)
    {
        var id = await CreateUserAsync(db);
        var companyId = await CreateLegalEntityAsync(db, "Own profile");
        var user = await db.Users.SingleAsync(row => row.Id == id);
        db.RolePermissions.Add(new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.CompanyProfile, CanView = true, CanEdit = canEdit });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{companyId}]" });
        db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        if (assigned) db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = id, LegalEntityId = companyId });
        await db.SaveChangesAsync();
        return (id, companyId);
    }

    private static TestServer PrintDefaultsServer(PaymentContext db) => new(new WebHostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(db);
        services.AddSingleton<ILegalEntityEnrichmentService>(new NoopLegalEntityEnrichmentService());
        services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
        services.AddAuthentication("profile-print").AddScheme<AuthenticationSchemeOptions, ProfilePrintAuthentication>("profile-print", _ => { });
        services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
        services.AddControllers().AddApplicationPart(typeof(LegalEntitiesController).Assembly);
    }).Configure(app =>
    {
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
    }));

    [Theory]
    [MemberData(nameof(CompanyDocumentImagePrintDefaultsTests.Masks), MemberType = typeof(CompanyDocumentImagePrintDefaultsTests))]
    public async Task PrintDefaults_HttpRoundTripsIndependentDocumentKinds(int mask)
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        using var server = PrintDefaultsServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Profile-Print-User", fixture.UserId.ToString());
        using var response = await client.PutAsJsonAsync($"/api/legal-entities/{fixture.CompanyId}", new LegalEntityRequest
        {
            ShortName = "Own profile",
            DocumentImagePrintDefaults = PrintDefaultsRequest(mask),
            DocumentImagePrintDefaultsChanged = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<LegalEntityDetailDto>();
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(mask), result!.DocumentImagePrintDefaults);
        using var read = await client.GetAsync($"/api/legal-entities/{fixture.CompanyId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(result.DocumentImagePrintDefaults, (await read.Content.ReadFromJsonAsync<LegalEntityDetailDto>())!.DocumentImagePrintDefaults);
        Assert.False(result.DocumentImagePrintDefaultsInvalid);
    }

    [Theory]
    [InlineData(false, true, 403)]
    [InlineData(true, false, 404)]
    public async Task PrintDefaults_ProfileEditAndAssignmentAreRequired(bool canEdit, bool assigned, int status)
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db, canEdit, assigned);
        var controller = CreateController(db, fixture.UserId, isAdmin: false);
        var response = await controller.Update(fixture.CompanyId, new LegalEntityRequest { ShortName = "Must not change", DocumentImagePrintDefaults = PrintDefaultsRequest(7), DocumentImagePrintDefaultsChanged = true }, default);
        if (status == 403) Assert.IsType<ForbidResult>(response.Result); else Assert.IsType<NotFoundResult>(response.Result);
        Assert.Equal("Own profile", (await db.LegalEntities.AsNoTracking().SingleAsync()).ShortName);
        Assert.Null((await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(fixture.CompanyId)).Value);
    }

    [Fact]
    public async Task PrintDefaults_HttpRejectsIncompleteFlagsAndRechecksRevocation()
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        using var server = PrintDefaultsServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Profile-Print-User", fixture.UserId.ToString());
        using var incomplete = await client.PutAsJsonAsync($"/api/legal-entities/{fixture.CompanyId}", new { shortName = "Must not change", documentImagePrintDefaultsChanged = true, documentImagePrintDefaults = new { invoice = new { includeLogo = false } } });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Equal("Own profile", (await db.LegalEntities.AsNoTracking().SingleAsync()).ShortName);
        var permission = await db.RolePermissions.SingleAsync(row => row.Section == RolePermissionCatalog.CompanyProfile);
        permission.CanEdit = false; await db.SaveChangesAsync();
        using var revoked = await client.PutAsJsonAsync($"/api/legal-entities/{fixture.CompanyId}", new { shortName = "Must not change", documentImagePrintDefaultsChanged = true, documentImagePrintDefaults = PrintDefaultsRequest(7) });
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Profile-Print-User");
        using var anonymous = await client.PutAsJsonAsync($"/api/legal-entities/{fixture.CompanyId}", new { shortName = "Must not change", documentImagePrintDefaultsChanged = true, documentImagePrintDefaults = PrintDefaultsRequest(7) });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null((await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(fixture.CompanyId)).Value);
    }

    [Fact]
    public async Task PrintDefaults_LegacyOmissionExplicitFalseAndResetPreserveRequisites()
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        var controller = CreateController(db, fixture.UserId, isAdmin: false);
        await controller.Update(fixture.CompanyId, new LegalEntityRequest { ShortName = "Own profile", DocumentImagePrintDefaults = PrintDefaultsRequest(3), DocumentImagePrintDefaultsChanged = true }, default);
        await controller.Update(fixture.CompanyId, new LegalEntityRequest { ShortName = "Edited normally" }, default);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(3), (await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(fixture.CompanyId)).Value);
        await controller.Update(fixture.CompanyId, new LegalEntityRequest { ShortName = "Edited normally", DocumentImagePrintDefaults = PrintDefaultsRequest(7), DocumentImagePrintDefaultsChanged = false }, default);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(3), (await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(fixture.CompanyId)).Value);
        await controller.Update(fixture.CompanyId, new LegalEntityRequest { ShortName = "Edited normally", DocumentImagePrintDefaultsChanged = true }, default);
        Assert.Null((await new CompanyDocumentImagePrintDefaultsService(db).ReadAsync(fixture.CompanyId)).Value);
        Assert.Equal("Edited normally", (await db.LegalEntities.AsNoTracking().SingleAsync()).ShortName);
    }

    [Fact]
    public async Task PrintDefaults_InvalidStoredValueIsReportedWithoutReturningRawDataAndCanBeRepaired()
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(fixture.CompanyId), Value = "broken private data" }); await db.SaveChangesAsync();
        var controller = CreateController(db, fixture.UserId, isAdmin: false);
        var result = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetById(fixture.CompanyId, default)).Result).Value);
        Assert.True(result.DocumentImagePrintDefaultsInvalid); Assert.Null(result.DocumentImagePrintDefaults);
        var saved = Assert.IsType<LegalEntityDetailDto>(Assert.IsType<OkObjectResult>((await controller.Update(fixture.CompanyId,
            new LegalEntityRequest { ShortName = "Own profile", DocumentImagePrintDefaults = PrintDefaultsRequest(0), DocumentImagePrintDefaultsChanged = true }, default)).Result).Value);
        Assert.False(saved.DocumentImagePrintDefaultsInvalid);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(0), saved.DocumentImagePrintDefaults);
    }

    [Fact]
    public async Task PrintDefaults_CreateRollsBackBothProfileAndSettingsWhenFinalSaveFails()
    {
        var failure = new PrintDefaultsSaveFailure();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db, user);
        failure.Remaining = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Create(new LegalEntityRequest { ShortName = "Must rollback", DocumentImagePrintDefaults = PrintDefaultsRequest(7), DocumentImagePrintDefaultsChanged = true }, default));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.SystemSettings.Where(row => row.Key.StartsWith(CompanyDocumentImagePrintDefaultsService.KeyPrefix)).ToListAsync());
    }

    [Fact]
    public async Task PrintDefaults_CreateDoesNotCommitCallerTransaction()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "admin");
        var controller = CreateController(db, user);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var response = await controller.Create(new LegalEntityRequest { ShortName = "Caller transaction", DocumentImagePrintDefaults = PrintDefaultsRequest(7), DocumentImagePrintDefaultsChanged = true }, default);
        Assert.IsType<CreatedResult>(response.Result);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        await transaction.RollbackAsync(); db.ChangeTracker.Clear();
        Assert.Empty(await db.LegalEntities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.SystemSettings.Where(row => row.Key.StartsWith(CompanyDocumentImagePrintDefaultsService.KeyPrefix)).ToListAsync());
    }

    [Fact]
    public async Task PrintDefaults_DeleteRemovesOnlyDeletedCompanySettingsAfterPermissionCheck()
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        var otherCompanyId = await CreateLegalEntityAsync(db, "Other profile");
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(fixture.CompanyId, CompanyDocumentImagePrintDefaultsTests.Value(3), true);
        await store.StageAsync(otherCompanyId, CompanyDocumentImagePrintDefaultsTests.Value(7), true);
        await db.SaveChangesAsync();

        var denied = await CreateController(db, fixture.UserId, isAdmin: false).Delete(fixture.CompanyId, default);
        Assert.IsType<ForbidResult>(denied);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(3), (await store.ReadAsync(fixture.CompanyId)).Value);
        var admin = await CreateUserAsync(db, "admin");
        var deleted = await CreateController(db, admin).Delete(fixture.CompanyId, default);
        Assert.IsType<NoContentResult>(deleted);
        Assert.False(await db.LegalEntities.AnyAsync(row => row.Id == fixture.CompanyId));
        Assert.False(await db.SystemSettings.AnyAsync(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(fixture.CompanyId)));
        Assert.True(await db.LegalEntities.AnyAsync(row => row.Id == otherCompanyId));
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(7), (await store.ReadAsync(otherCompanyId)).Value);
    }

    [Fact]
    public async Task PrintDefaults_HttpEchoWithoutChangeMarkerDoesNotOverwriteNewDefaults()
    {
        await using var db = await CreateContextAsync();
        var fixture = await PrintDefaultsProfileFixture(db);
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(fixture.CompanyId, CompanyDocumentImagePrintDefaultsTests.Value(5), true);
        await db.SaveChangesAsync();
        using var server = PrintDefaultsServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Profile-Print-User", fixture.UserId.ToString());
        using var response = await client.PutAsJsonAsync($"/api/legal-entities/{fixture.CompanyId}", new
        {
            shortName = "Changed by old client",
            documentImagePrintDefaults = PrintDefaultsRequest(0),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Changed by old client", (await db.LegalEntities.AsNoTracking().SingleAsync()).ShortName);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(5), (await store.ReadAsync(fixture.CompanyId)).Value);
    }

    private sealed class PrintDefaultsSaveFailure : SaveChangesInterceptor
    {
        public int Remaining { get; set; } = -1;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Remaining > 0 && --Remaining == 0) throw new InvalidOperationException("Synthetic final-save failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ProfilePrintAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Profile-Print-User"], out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], Scheme.Name)), Scheme.Name)));
        }
    }
}

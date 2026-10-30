using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class LegalEntitiesControllerTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("promote")]
    [InlineData("settings")]
    [InlineData("default")]
    public async Task CompanyApproval_RevokedAfterAuthenticationCannotMutateCompanyData(string operation)
        => await AssertCompanyApprovalHttp(operation, "after-auth");

    [Theory]
    [InlineData("create")]
    [InlineData("promote")]
    [InlineData("settings")]
    [InlineData("default")]
    public async Task CompanyApproval_RevokedBeforeAuthenticationIsRejected(string operation)
        => await AssertCompanyApprovalHttp(operation, "before-auth");

    [Theory]
    [InlineData("create")]
    [InlineData("promote")]
    [InlineData("settings")]
    [InlineData("default")]
    public async Task CompanyApproval_ApprovedAdministratorCanPerformOperation(string operation)
        => await AssertCompanyApprovalHttp(operation, "approved");

    [Theory]
    [InlineData("requisites", "after-auth")]
    [InlineData("requisites", "before-auth")]
    [InlineData("requisites", "approved")]
    [InlineData("delete-own", "after-auth")]
    [InlineData("delete-own", "before-auth")]
    [InlineData("delete-own", "approved")]
    public async Task CompanyApproval_OwnOperationsAlsoRequireApprovedActor(string operation, string stage)
        => await AssertCompanyApprovalHttp(operation, stage);

    private static async Task AssertCompanyApprovalHttp(string operation, string stage)
    {
        await using var db = await CreateContextAsync();
        var actor = await CreateUserAsync(db, "admin");
        var first = await CreateLegalEntityAsync(db, "Current own company");
        var second = await CreateLegalEntityAsync(db, "Candidate company");
        db.SystemSettings.AddRange(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = $"[{first}]" },
            new SystemSetting { Key = "legalEntities.defaultMainCompanyId", Value = first.ToString(CultureInfo.InvariantCulture) },
            new SystemSetting { Key = "legalEntities.mainCompanyId", Value = first.ToString(CultureInfo.InvariantCulture) },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = actor, LegalEntityId = first });
        var contact = new ExternalContact { ContactType = "Organization", FullName = "Contact company", Inn = "7700000111", ResponsibleUserId = actor };
        db.ExternalContacts.Add(contact); await db.SaveChangesAsync();
        var before = await CompanyApprovalMutationSnapshot(db);
        if (stage == "before-auth") await db.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsApproved, false));
        using var server = CompanyApprovalServer(db, stage == "after-auth" ? actor : null);
        using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Company-Approval-Actor", actor.ToString(CultureInfo.InvariantCulture));
        using var response = operation switch
        {
            "create" => await client.PostAsJsonAsync("/api/legal-entities", new LegalEntityRequest { ShortName = "Must require approval", Inn = "7700000112" }),
            "promote" => await client.PostAsJsonAsync($"/api/contacts/external/{contact.Id}/promote", new PromoteContactRequest { Target = "legalEntity", Role = "Client" }),
            "settings" => await client.PutAsJsonAsync("/api/legal-entities/main-companies/settings", new UpdateMainCompanySettingsRequest { CompanyIds = [second], DefaultCompanyId = second }),
            "default" => await client.PutAsync($"/api/legal-entities/company-profile/main/{second}", null),
            "requisites" => await client.GetAsync($"/api/legal-entities/{first}/requisites-check"),
            "delete-own" => await client.DeleteAsync($"/api/legal-entities/{first}"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        if (stage == "approved")
        {
            Assert.Equal(operation == "create" ? HttpStatusCode.Created : operation == "delete-own" ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode);
            if (operation == "requisites") Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
            else Assert.NotEqual(before, await CompanyApprovalMutationSnapshot(db));
        }
        else
        {
            Assert.Equal(before, await CompanyApprovalMutationSnapshot(db));
            Assert.Equal(stage == "before-auth" ? HttpStatusCode.Unauthorized : operation == "requisites" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
            if (operation == "requisites") Assert.DoesNotContain("legalEntityId", await response.Content.ReadAsStringAsync());
            Assert.False(await db.Users.AsNoTracking().Where(user => user.Id == actor).Select(user => user.IsApproved).SingleAsync());
        }
        Assert.Equal(stage != "before-auth", response.Headers.TryGetValues("X-Company-Approval-Authenticated", out var values) && values.Single() == "true");
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task<string> CompanyApprovalMutationSnapshot(PaymentContext db) => JsonSerializer.Serialize(new
    {
        companies = await db.LegalEntities.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ShortName, row.Inn }).ToArrayAsync(),
        settings = await db.SystemSettings.AsNoTracking().OrderBy(row => row.Key).Select(row => new { row.Key, row.Value }).ToArrayAsync(),
        assignments = await db.UserCompanyAccesses.AsNoTracking().OrderBy(row => row.UserId).ThenBy(row => row.LegalEntityId).Select(row => new { row.UserId, row.LegalEntityId }).ToArrayAsync(),
        contacts = await db.ExternalContacts.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.LinkedLegalEntityId, row.LegalEntityId, row.RolesJson }).ToArrayAsync(),
    });

    private static TestServer CompanyApprovalServer(PaymentContext db, int? revokeAfterAuthentication) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("Email:Enabled", "false").UseSetting("BackgroundServices:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging(); services.AddSingleton(db);
            services.AddSingleton<IDbContextFactory<PaymentContext>>(new CompanyApprovalContextFactory(db));
            services.AddTransient<CurrentUserClaimsTransformation>();
            services.AddSingleton<ILegalEntityEnrichmentService>(new NoopLegalEntityEnrichmentService());
            services.AddSingleton<IFormRequirementsService>(new NoopFormRequirementsService());
            services.AddAuthentication("company-approval").AddScheme<AuthenticationSchemeOptions, CompanyApprovalAuthentication>("company-approval", _ => { });
            services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
            services.AddControllers().AddApplicationPart(typeof(LegalEntitiesController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.Use(async (context, next) =>
            {
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    context.Response.Headers["X-Company-Approval-Authenticated"] = "true";
                    if (revokeAfterAuthentication.HasValue)
                        await db.Users.Where(user => user.Id == revokeAfterAuthentication.Value).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsApproved, false));
                }
                await next();
            });
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    private sealed class CompanyApprovalContextFactory(PaymentContext source) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => new(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(source.Database.GetDbConnection()).Options);
    }

    // Exercise the production claims transformer before the deliberate between-stage
    // revocation. This fixture does not claim to test JWT signature validation.
    private sealed class CompanyApprovalAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Company-Approval-Actor"], out var id)) return AuthenticateResult.NoResult();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "old-role"), AuthSessionClaims.CreateTokenVersionClaim(0)], Scheme.Name));
            var current = await Context.RequestServices.GetRequiredService<CurrentUserClaimsTransformation>().TransformAsync(principal);
            return current.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(current, Scheme.Name))
                : AuthenticateResult.Fail("Production account validation rejected the actor.");
        }
    }
}

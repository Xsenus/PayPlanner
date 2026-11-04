using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtSearchHttp_RevokedApprovalWhileProviderWaitsRejectsResultAndError(bool sourceError)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtSearchProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await db.Users.Where(row => row.Id == user.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        }, sourceError);
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("inactive", 401)]
    [InlineData("unapproved", 401)]
    [InlineData("session", 401)]
    [InlineData("missing-user", 401)]
    [InlineData("permission", 403)]
    [InlineData("view", 403)]
    [InlineData("role", 403)]
    [InlineData("hidden", 404)]
    [InlineData("owner", 404)]
    [InlineData("missing", 404)]
    public async Task CourtSearchHttp_CurrentAdmissionPreventsProviderCall(string mode, int expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
        var provider = new CourtSearchProvider(() => Task.CompletedTask);
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Court-Search-User", (mode == "missing-user" ? user.Id + 1000 : user.Id).ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{(mode == "missing" ? clientCase.Id + 1000 : clientCase.Id)}/court-sync/search", new { });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(0, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    public static IEnumerable<object[]> CourtSearchLateAccessCases()
    {
        foreach (var mode in new[] { "inactive", "session", "permission", "view", "role", "hidden", "owner", "deleted" })
            foreach (var error in new[] { false, true }) yield return [mode, error];
    }

    [Theory]
    [MemberData(nameof(CourtSearchLateAccessCases))]
    public async Task CourtSearchHttp_CurrentAccessAfterProviderWaitOverridesResultAndError(string mode, bool sourceError)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtSearchProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
        }, sourceError);
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        var expected = mode is "inactive" or "session" ? 401 : mode is "permission" or "view" or "role" ? 403 : 404;
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Theory]
    [InlineData("CourtUrl", false)]
    [InlineData("CourtName", false)]
    [InlineData("CourtCaseNumber", false)]
    [InlineData("CourtUrl", true)]
    [InlineData("CourtName", true)]
    [InlineData("CourtCaseNumber", true)]
    public async Task CourtSearchHttp_ChangedFallbackRejectsOnlySearchesThatUsedIt(string field, bool explicitSearch)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtSearchProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await db.ClientCases.Where(row => row.Id == clientCase.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<string>(row, field), "https://court.test/changed"));
        });
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        object body = explicitSearch ? new { sourceUrl = clientCase.CourtUrl, courtName = clientCase.CourtName, courtCaseNumber = clientCase.CourtCaseNumber } : new { };
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", body);
        Assert.Equal(explicitSearch ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        if (!explicitSearch) Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Fact]
    public async Task CourtSearchHttp_CurrentInstanceAndUnrelatedCaseEditArePreserved()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        db.ClientCaseCourtInstances.Add(new ClientCaseCourtInstance { ClientCaseId = clientCase.Id, Instance = "first", JudgeName = "Прежний судья" });
        await db.SaveChangesAsync();
        var provider = new CourtSearchProvider(async () =>
        {
            await db.ClientCaseCourtInstances.Where(row => row.ClientCaseId == clientCase.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Текущий судья"));
            await db.ClientCases.Where(row => row.Id == clientCase.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Изменено параллельно"));
        });
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = Assert.Single((await response.Content.ReadFromJsonAsync<CasesV2Controller.ClientCaseCourtSyncSearchResultDto[]>())!);
        Assert.Equal("Текущий судья", Assert.Single(result.Differences, row => row.Field == "judgeName").CurrentValue);
        Assert.True(result.RequiresReview);
        Assert.Equal("Изменено параллельно", (await db.ClientCases.AsNoTracking().SingleAsync()).Title);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Fact]
    public async Task CourtSearchHttp_ExplicitRetryUsesNewAdmissionAndNewController()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var calls = 0;
        var provider = new CourtSearchProvider(async () =>
        {
            if (++calls == 1) await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, "unapproved");
        });
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var refused = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, true));
        using var retry = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Fact]
    public async Task CourtSearch_CanceledProviderReplyCannotBecomeSuccessfulResponse()
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        using var cancellation = new CancellationTokenSource();
        var provider = new CourtSearchProvider(() => { cancellation.Cancel(); return Task.CompletedTask; });
        var controller = CreateController(db, user, courtSyncProviderRegistry: provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.SearchCourtSyncCases(clientCase.Id,
            new CasesV2Controller.ClientCaseCourtSyncSearchRequest(), cancellation.Token));
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtSearchHttp_ExistingPostgresRejectsLateApprovalAndSourceError(bool sourceError)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var provider = new CourtSearchProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await using var writer = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(lease.ConnectionString).Options);
            await ChangeCourtSearchAccessAsync(writer, user, clientCase.Id, "unapproved");
        }, sourceError);
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("private-source", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("case")]
    [InlineData("permission")]
    public async Task CourtSearchHttp_ExistingPostgresBusyOwnerRefusesThenAllowsRetry(string locked)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(lease.ConnectionString).Options);
        await using var competitor = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var calls = 0;
        var provider = new CourtSearchProvider(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            if (++calls > 1) return;
            await competitor.Database.BeginTransactionAsync();
            if (locked == "actor") await competitor.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
            else if (locked == "case") await competitor.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Занят"));
            else await competitor.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        });
        using var server = CourtSearchServer(db, user, provider);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var refused = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain("private-source", await refused.Content.ReadAsStringAsync());
        await competitor.Database.RollbackTransactionAsync();
        using var retry = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/search", new { });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, provider.Calls);
        await AssertCourtSearchDidNotWriteAsync(db);
    }

    private static async Task ChangeCourtSearchAccessAsync(PaymentContext db, User user, int id, string mode)
    {
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        else if (mode == "session") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1));
        else if (mode == "permission") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        else if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        else if (mode == "role")
        {
            var role = new Role { Name = "court-search-no-access-" + Guid.NewGuid().ToString("N") };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, role.Id));
        }
        else if (mode == "hidden") await db.ClientCases.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true));
        else if (mode == "owner")
        {
            await db.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
            await db.ClientCases.Where(row => row.Id == id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
        }
        else if (mode == "deleted") await db.ClientCases.Where(row => row.Id == id).ExecuteDeleteAsync();
    }

    private static async Task<(User User, ClientCase Case)> SeedCourtSearchAsync(PaymentContext db)
    {
        var user = await CreateUserAsync(db, "court-search@synthetic.local", "Синтетический юрист");
        var clientCase = new ClientCase
        {
            Title = "Синтетическое дело поиска",
            Client = new Client { Name = "Синтетический клиент поиска", ResponsibleUserId = user.Id, IsActive = true },
            ResponsibleUserId = user.Id,
            CourtName = "Исходный суд",
            CourtCaseNumber = "42/2026",
            CourtUrl = "https://court.test/search",
            JudgeName = "Исходный судья",
            CourtClaimAmount = 123.45m,
        };
        db.ClientCases.Add(clientCase);
        await db.SaveChangesAsync();
        return (user, clientCase);
    }

    private static async Task AssertCourtSearchDidNotWriteAsync(PaymentContext db)
    {
        var actual = await db.ClientCases.AsNoTracking().SingleOrDefaultAsync();
        if (actual is not null)
        {
            Assert.Empty(actual.EventHistory);
            Assert.Null(actual.CourtSyncLastAttemptAt);
            Assert.Null(actual.CourtSyncLastSuccessAt);
            Assert.Equal(123.45m, actual.CourtClaimAmount);
        }
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Empty(await db.TaskJournalItems.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static TestServer CourtSearchServer(PaymentContext db, User user, CourtSearchProvider provider)
        => new(new WebHostBuilder()
            .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IDbContextFactory<PaymentContext>>(new CourtSearchContextFactory(db));
                services.AddAuthentication("court-search-fixture")
                    .AddScheme<AuthenticationSchemeOptions, CourtSearchAuthentication>("court-search-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly);
                services.AddSingleton<IControllerActivator>(new CourtSearchControllerActivator(db, user, provider));
            }).Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

    private sealed class CourtSearchContextFactory(PaymentContext db) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PaymentContext>();
            if (db.Database.IsNpgsql()) return new PostgreSqlPaymentContext(
                new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
            return new PaymentContext(options.UseSqlite(db.Database.GetDbConnection()).Options);
        }
    }

    private sealed class CourtSearchControllerActivator(PaymentContext db, User user, CourtSearchProvider provider) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            Assert.Equal(typeof(CasesV2Controller), context.ActionDescriptor.ControllerTypeInfo.AsType());
            var controller = CreateController(db, user, courtSyncProviderRegistry: provider);
            controller.ControllerContext = context;
            return controller;
        }
        public void Release(ControllerContext context, object controller) { }
    }

    private sealed class CourtSearchAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, IDbContextFactory<PaymentContext> factory)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Court-Search-User"], out var id)) return AuthenticateResult.NoResult();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, "Юрист"),
                new Claim(ClaimTypes.Role, "admin"), AuthSessionClaims.CreateTokenVersionClaim(0),
            ], Scheme.Name));
            // Exercise the production JWT account-refresh hook without testing signature cryptography here.
            var refreshed = await new CurrentUserClaimsTransformation(factory).TransformAsync(principal);
            return refreshed.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(refreshed, Scheme.Name))
                : AuthenticateResult.Fail("Account is no longer available.");
        }
    }

    private sealed class CourtSearchProvider(Func<Task> whileWaiting, bool sourceError = false)
        : ICourtSyncProviderRegistry, ICourtSyncCaseSearchProvider
    {
        public int Calls { get; private set; }
        public CourtSyncCaseSearchRequest? LastRequest { get; private set; }
        public string Key => "test";
        public string Label => "Синтетический суд";
        public string SourceType => "test";
        public int SchemaVersion => 1;
        public CourtSyncProviderCapability Capabilities => CourtSyncProviderCapability.SearchCases;
        public IReadOnlyList<string> SupportedHosts { get; } = ["court.test"];
        public bool CanHandle(string? sourceUrl) => sourceUrl?.StartsWith("https://court.test/", StringComparison.Ordinal) == true;
        public IReadOnlyList<CourtSyncProviderDescriptor> GetProviders() => [];
        public ICourtSyncProvider? Resolve(string? sourceUrl) => CanHandle(sourceUrl) ? this : null;
        public async Task<IReadOnlyList<CourtSyncCaseSearchResult>> SearchCasesAsync(CourtSyncCaseSearchRequest request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            await whileWaiting();
            if (sourceError) throw new SudrfCourtSyncException("private-source-error");
            return [new(Key, SourceType, "https://court.test/private-source-case", "Найденный суд", "43/2026",
                "source-uid", "Найденный судья", "first", "private-source-result", "Синтетический результат", true)];
        }
    }
}

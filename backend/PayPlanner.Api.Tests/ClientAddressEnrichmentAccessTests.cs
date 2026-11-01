using System.Globalization;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientAddressEnrichmentAccessTests
{
    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task ReadOnlyRoleCannotCallThePaidEnrichmentService(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = await fixture.SendAsync(operation);
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden && fixture.Service.Calls == 0,
            $"Expected403 without enrichment; status={(int)response.StatusCode}, upstreamCalls={fixture.Service.Calls}");
    }

    public static IEnumerable<object[]> AllowedOperations()
    {
        foreach (var operation in new[] { "clean-address", "suggest-address", "clean/address", "clean/email", "clean/phone", "find-by-id", "suggest" })
        {
            yield return [operation, RolePermissionCatalog.Clients, true, false];
            yield return [operation, RolePermissionCatalog.Clients, false, true];
            yield return [operation, RolePermissionCatalog.LegalEntities, true, false];
            yield return [operation, RolePermissionCatalog.LegalEntities, false, true];
            yield return [operation, RolePermissionCatalog.CompanyProfile, false, true];
        }
    }

    [Theory]
    [MemberData(nameof(AllowedOperations))]
    public async Task IndependentWritableSectionsCanEnrichWithoutOtherModules(string operation, string section, bool create, bool edit)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(section, view: true, create: create, edit: edit);
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Service.Calls);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (operation is "clean-address" or "clean/address")
        {
            Assert.Equal("Уточнённый адрес", json.RootElement.GetProperty("result").GetString());
            Assert.Equal(operation == "clean-address" ? "Синтетический адрес" : "  Синтетический адрес  ", fixture.Service.LastCleanInput);
        }
        else if (operation == "suggest-address")
        {
            Assert.Equal("Подсказка адреса", json.RootElement[0].GetProperty("value").GetString());
            Assert.Equal("Синтетический адрес", fixture.Service.LastQuery);
            Assert.Equal(7, fixture.Service.LastLimit);
        }
        else if (operation == "clean/email") Assert.Equal("cleaned@local.invalid", json.RootElement.GetProperty("email").GetString());
        else if (operation == "clean/phone") Assert.Equal("+70000000000", json.RootElement.GetProperty("phone").GetString());
        else if (operation == "find-by-id") Assert.Equal("Синтетическая организация", json.RootElement.GetProperty("shortName").GetString());
        else Assert.Equal("Синтетическая организация", json.RootElement[0].GetProperty("shortName").GetString());
    }

    public static IEnumerable<object?[]> DeniedOperations()
    {
        foreach (var operation in new[] { "clean-address", "suggest-address", "clean/address", "clean/email", "clean/phone", "find-by-id", "suggest" })
        {
            yield return [operation, RolePermissionCatalog.LegalEntities, true, false, false];
            yield return [operation, RolePermissionCatalog.CompanyProfile, true, false, false];
            yield return [operation, RolePermissionCatalog.CompanyProfile, true, true, false];
            yield return [operation, RolePermissionCatalog.Clients, false, true, true];
            yield return [operation, RolePermissionCatalog.LegalEntities, false, true, true];
            yield return [operation, RolePermissionCatalog.Integrations, true, true, true];
            yield return [operation, null, false, false, false];
        }
    }

    [Theory]
    [MemberData(nameof(DeniedOperations))]
    public async Task ReadonlyMissingOrUnrelatedRightsNeverCallEnrichment(string operation, string? section, bool view, bool create, bool edit)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(section, view: view, create: create, edit: edit);
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, fixture.Service.Calls);
        Assert.DoesNotContain("Уточнённый адрес", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task TheSameSessionImmediatelyRespectsARevokedPermission(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        using var first = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients);
        using var second = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(1, fixture.Service.Calls);
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task TheCurrentDatabaseRoleWinsOverTheOldRoleAndAdminClaim(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        using var first = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await fixture.ChangeUserAsync((db, user) =>
        {
            var role = new Role { Name = "new-readonly-role" };
            user.Role = role;
            db.RolePermissions.Add(new RolePermission { Role = role, Section = RolePermissionCatalog.Clients, CanView = true });
            return Task.CompletedTask;
        });
        using var second = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(1, fixture.Service.Calls);
    }

    public static IEnumerable<object[]> InvalidAccounts()
    {
        foreach (var operation in new[] { "clean-address", "suggest-address", "clean/address", "clean/email", "clean/phone", "find-by-id", "suggest" })
            foreach (var state in new[] { "anonymous", "missing", "inactive", "unapproved", "session", "no-id" })
                yield return [operation, state];
    }

    [Theory]
    [MemberData(nameof(InvalidAccounts))]
    public async Task AuthenticationAndCurrentAccountMustBeValidBeforeEnrichment(string operation, string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        if (state is "anonymous" or "missing" or "no-id")
        {
            fixture.Client.DefaultRequestHeaders.Remove("X-User");
            if (state != "anonymous") fixture.Client.DefaultRequestHeaders.Add("X-User", state == "missing" ? "9999" : "no-id");
        }
        else await fixture.ChangeUserAsync((_, user) =>
        {
            if (state == "inactive") user.IsActive = false;
            if (state == "unapproved") user.IsApproved = false;
            if (state == "session") user.AuthTokenVersion++;
            return Task.CompletedTask;
        });
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(state == "no-id" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Service.Calls);
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task TheApprovedDatabaseAdminCanUseEnrichment(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ChangeUserAsync((_, user) => { user.Role!.Name = "AdMiN"; return Task.CompletedTask; });
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Service.Calls);
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task AnAssignedLegacyRoleKeepsExistingDefaults(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(null, keepMarker: false);
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fixture.Service.Calls);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("role")]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("session")]
    [InlineData("deleted")]
    public async Task FallbackRechecksAccessAfterTheFirstExternalCall(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        fixture.Service.ReturnNullClean = true;
        fixture.Service.BeforeCleanReturns = async _ =>
        {
            if (change == "permission") await fixture.SetAccessAsync(RolePermissionCatalog.Clients);
            else await fixture.ChangeUserAsync((db, user) =>
            {
                if (change == "inactive") user.IsActive = false;
                if (change == "unapproved") user.IsApproved = false;
                if (change == "session") user.AuthTokenVersion++;
                if (change == "deleted") db.Users.Remove(user);
                if (change == "role")
                {
                    var role = new Role { Name = "fallback-readonly" };
                    user.Role = role;
                    db.RolePermissions.Add(new RolePermission { Role = role, Section = RolePermissionCatalog.CompanyProfile, CanView = true });
                }
                return Task.CompletedTask;
            });
        };
        using var response = await fixture.SendAsync("clean-address");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, fixture.Service.CleanCalls);
        Assert.Equal(0, fixture.Service.SuggestCalls);
    }

    [Theory]
    [InlineData("Полный адрес")]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task AuthorizedFallbackPreservesTheContractAndOneCancellationToken(string fullLabel)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.CompanyProfile, edit: true);
        fixture.Service.ReturnNullClean = true;
        fixture.Service.FullLabel = fullLabel;
        fixture.Counter.Count = 0;
        using var response = await fixture.SendAsync("clean-address");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(string.IsNullOrWhiteSpace(fullLabel) ? "Подсказка адреса" : fullLabel, json.RootElement.GetProperty("result").GetString());
        Assert.Equal("Синтетический адрес", json.RootElement.GetProperty("source").GetString());
        Assert.Equal(1, fixture.Service.LastLimit);
        Assert.Equal(fixture.Service.LastCleanToken, fixture.Service.LastSuggestToken);
        Assert.Equal(3, fixture.Counter.Count);
    }

    [Fact]
    public async Task NoResultReturns404AfterOneAuthorizedFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.LegalEntities, create: true);
        fixture.Service.ReturnNullClean = true; fixture.Service.EmptySuggestions = true;
        using var response = await fixture.SendAsync("clean-address");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, fixture.Service.CleanCalls); Assert.Equal(1, fixture.Service.SuggestCalls);
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task EachAuthorizedRequestUsesOnePermissionQueryAfterAuthentication(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.LegalEntities, edit: true);
        fixture.Counter.Count = 0;
        using var response = await fixture.SendAsync(operation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fixture.Counter.Count);
    }

    [Theory]
    [InlineData("clean-address")]
    [InlineData("suggest-address")]
    [InlineData("clean/address")]
    [InlineData("clean/email")]
    [InlineData("clean/phone")]
    [InlineData("find-by-id")]
    [InlineData("suggest")]
    public async Task EmptyInputIsHandledOnlyAfterCheckingPermission(string operation)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var denied = await fixture.SendAsync(operation, "  ");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        using var allowed = await fixture.SendAsync(operation, "  ");
        Assert.Equal(operation == "suggest-address" ? HttpStatusCode.OK : HttpStatusCode.BadRequest, allowed.StatusCode);
        Assert.Equal(0, fixture.Service.Calls);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("session")]
    public async Task ControllerGuardDoesNotDependOnlyOnTheAuthenticationMiddleware(string state)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        await fixture.ChangeUserAsync((_, user) =>
        {
            if (state == "inactive") user.IsActive = false;
            if (state == "unapproved") user.IsApproved = false;
            if (state == "session") user.AuthTokenVersion++;
            return Task.CompletedTask;
        });
        await using var db = fixture.Factory.CreateDbContext();
        var controller = fixture.CreateController(db);
        Assert.IsType<ForbidResult>(await controller.CleanAddress(new ClientCleanAddressRequest("Адрес"), default));
        Assert.Equal(0, fixture.Service.Calls);
    }

    [Fact]
    public async Task CancellationDuringCleanNeverStartsTheFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Service.ReturnNullClean = true;
        fixture.Service.BeforeCleanReturns = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await using var db = fixture.Factory.CreateDbContext();
        var controller = fixture.CreateController(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CleanAddress(new ClientCleanAddressRequest("Адрес"), cancellation.Token));
        Assert.Equal(1, fixture.Service.CleanCalls); Assert.Equal(0, fixture.Service.SuggestCalls);
    }

    [Fact]
    public async Task AmbiguousSessionClaimsCannotBypassTheControllerGuard()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetAccessAsync(RolePermissionCatalog.Clients, edit: true);
        await using var db = fixture.Factory.CreateDbContext();
        var controller = fixture.CreateController(db);
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(AuthSessionClaims.CreateTokenVersionClaim(0));
        Assert.IsType<ForbidResult>(await controller.CleanAddress(new ClientCleanAddressRequest("Адрес"), default));
        Assert.Equal(0, fixture.Service.Calls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public TestServer Server { get; }
        public HttpClient Client { get; }
        public FakeEnrichment Service { get; }
        public IDbContextFactory<PaymentContext> Factory { get; }
        public QueryCounter Counter { get; }
        public int UserId { get; private set; }

        private Fixture(SqliteConnection connection, TestServer server, FakeEnrichment service, QueryCounter counter)
        {
            _connection = connection;
            Server = server;
            Client = server.CreateClient();
            Service = service;
            Counter = counter;
            Factory = server.Services.GetRequiredService<IDbContextFactory<PaymentContext>>();
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var counter = new QueryCounter();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(ClientAddressEnrichmentAccessTests).Assembly.FullName))
                .AddInterceptors(counter).Options;
            var service = new FakeEnrichment();
            var factory = new ContextFactory(options);
            var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
                .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IDbContextFactory<PaymentContext>>(factory);
                    services.AddScoped(_ => factory.CreateDbContext());
                    services.AddSingleton<ILegalEntityEnrichmentService>(service);
                    services.AddScoped<IFormRequirementsService, FormRequirementsService>();
                    services.AddAuthentication("address-fixture")
                        .AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("address-fixture", _ => { });
                    services.AddTransient<CurrentUserClaimsTransformation>();
                    services.AddAuthorization();
                    services.AddControllers().AddApplicationPart(typeof(ClientsController).Assembly);
                }).Configure(app =>
                {
                    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }));
            var fixture = new Fixture(connection, server, service, counter);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "address-viewer" };
            var user = new User { Role = role, Email = "address@local.invalid", FullName = "Address fixture", PasswordHash = "unused", IsActive = true, IsApproved = true };
            db.Users.Add(user);
            db.RolePermissions.Add(new RolePermission { Role = role, Section = RolePermissionCatalog.Clients, CanView = true });
            await db.SaveChangesAsync();
            fixture.UserId = user.Id;
            fixture.Client.DefaultRequestHeaders.Add("X-User", user.Id.ToString(CultureInfo.InvariantCulture));
            return fixture;
        }

        public Task<HttpResponseMessage> SendAsync(string operation, string input = "  Синтетический адрес  ")
            => Client.PostAsJsonAsync(operation is "clean-address" or "suggest-address" ? $"/api/clients/dadata/{operation}" : $"/api/legal-entities/{operation}",
                new { address = input, query = input, email = input, phone = input, limit = 7 });

        public async Task SetAccessAsync(string? section, bool view = true, bool create = false, bool edit = false, bool keepMarker = true)
        {
            await using var db = Factory.CreateDbContext();
            var roleId = await db.Users.Where(user => user.Id == UserId).Select(user => user.RoleId).SingleAsync();
            db.RolePermissions.RemoveRange(await db.RolePermissions.Where(permission => permission.RoleId == roleId).ToListAsync());
            if (keepMarker) db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = RolePermissionCatalog.Calendar });
            if (section is not null) db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = section, CanView = view, CanCreate = create, CanEdit = edit });
            await db.SaveChangesAsync();
        }

        public async Task ChangeUserAsync(Func<PaymentContext, User, Task> change)
        {
            await using var db = Factory.CreateDbContext();
            var user = await db.Users.Include(user => user.Role).SingleAsync(user => user.Id == UserId);
            await change(db, user); await db.SaveChangesAsync();
        }

        public ClientsController CreateController(PaymentContext db)
            => new(db, Service, new FormRequirementsService(db))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, UserId.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "admin"), AuthSessionClaims.CreateTokenVersionClaim(0)], "direct-fixture"))
                    }
                },
            };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); Server.Dispose(); await _connection.DisposeAsync();
        }
    }

    private sealed class ContextFactory(DbContextOptions<PaymentContext> options) : IDbContextFactory<PaymentContext>
    {
        public PaymentContext CreateDbContext() => new(options);
        public Task<PaymentContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Count++; return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, CurrentUserClaimsTransformation transformer)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var userId)) return AuthenticateResult.NoResult();
            var claims = new List<Claim> { new(ClaimTypes.Role, "admin"), AuthSessionClaims.CreateTokenVersionClaim(0) };
            if (userId != "no-id") claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            var principal = await transformer.TransformAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)));
            // Match Program.OnTokenValidated: an invalid current account fails authentication.
            if (principal.Identity?.IsAuthenticated != true) return AuthenticateResult.Fail("Current account is invalid.");
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
    }

    private sealed class FakeEnrichment : ILegalEntityEnrichmentService
    {
        public int CleanCalls { get; private set; }
        public int SuggestCalls { get; private set; }
        public int OtherCalls { get; private set; }
        public int Calls => CleanCalls + SuggestCalls + OtherCalls;
        public bool ReturnNullClean { get; set; }
        public bool EmptySuggestions { get; set; }
        public string FullLabel { get; set; } = "Полный адрес";
        public Func<CancellationToken, Task>? BeforeCleanReturns { get; set; }
        public CancellationToken LastCleanToken { get; private set; }
        public CancellationToken LastSuggestToken { get; private set; }
        public string? LastCleanInput { get; private set; }
        public string? LastQuery { get; private set; }
        public int LastLimit { get; private set; }
        public async Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); CleanCalls++; LastCleanInput = rawAddress; LastCleanToken = cancellationToken;
            if (BeforeCleanReturns is not null) await BeforeCleanReturns(cancellationToken);
            return ReturnNullClean ? null : JsonSerializer.Deserialize<CleanedAddress>("{\"result\":\"Уточнённый адрес\",\"cityWithType\":\"г Синтетический\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        public Task<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); SuggestCalls++; LastQuery = query; LastLimit = limit; LastSuggestToken = cancellationToken;
            var row = JsonSerializer.Deserialize<ILegalEntityEnrichmentService.DadataAddressSuggestion>(JsonSerializer.Serialize(new { value = "Подсказка адреса", unrestrictedValue = FullLabel }), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            return Task.FromResult<IReadOnlyList<ILegalEntityEnrichmentService.DadataAddressSuggestion>>(EmptySuggestions ? [] : [row]);
        }
        public Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); OtherCalls++; return Task.FromResult<CleanedEmail?>(new("cleaned@local.invalid", "PRIVATE", 0)); }
        public Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); OtherCalls++; return Task.FromResult<CleanedPhone?>(new("+70000000000", null, null, null, 0)); }
        public Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); OtherCalls++;
            return Task.FromResult(JsonSerializer.Deserialize<LegalEntityDetails>("{\"shortName\":\"Синтетическая организация\",\"phones\":[],\"emails\":[]}", new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        public Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); OtherCalls++;
            return Task.FromResult<IReadOnlyList<LegalEntitySuggestion>>([new("Синтетическая организация", null, "7700000000", null, null, null, null, null, null)]);
        }
    }
}

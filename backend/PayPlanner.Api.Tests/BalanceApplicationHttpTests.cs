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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyManaged_StaleAdminClaimCannotBypassCurrentEditPermission(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        await db.SaveChangesAsync();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString()), new Claim(ClaimTypes.Role, "admin"),
        ], "synthetic"))
        };
        var controllerContext = new ControllerContext { HttpContext = context };
        var request = Request(fixture); request.RequestId = null;
        var key = Guid.NewGuid();
        var denied = legalEntity
            ? await new LegalEntityBalancesController(db) { ControllerContext = controllerContext }.ApplyManaged(fixture.OwnerId, key, request, CancellationToken.None)
            : await new ClientBalancesController(db) { ControllerContext = controllerContext }.ApplyManaged(fixture.OwnerId, key, request, CancellationToken.None);
        Assert.IsType<ForbidResult>(denied);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireManaged_RouteKeyIsMandatoryAndRepeatedCommandReplays(bool legalEntity, bool includeBodyKey)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var key = Guid.NewGuid();
        using var server = CreateServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{key}/apply";
        object body = includeBodyKey ? new { paymentId = fixture.PaymentId, amount = 25m, requestId = key } : new { paymentId = fixture.PaymentId, amount = 25m };
        using var first = await client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.True(first.Headers.CacheControl?.NoStore);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(key, firstJson.GetProperty("requestId").GetGuid()); Assert.False(firstJson.GetProperty("isReplay").GetBoolean());
        using var repeated = await client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.True((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isReplay").GetBoolean());
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireManaged_ConflictingBodyKeyOrEmptyRouteKeyCannotBecomeLegacy(bool legalEntity, bool emptyRoute)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var key = emptyRoute ? Guid.Empty : Guid.NewGuid();
        using var server = CreateServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{key}/apply";
        using var response = await client.PostAsJsonAsync(route, new { paymentId = fixture.PaymentId, amount = 25m, requestId = emptyRoute ? (Guid?)null : Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireManaged_AnonymousOrCancelledAttemptCannotApply(bool legalEntity, bool cancelled)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var key = Guid.NewGuid();
        if (cancelled) BalanceApplicationCancellationTests.CancelResult(await BalanceApplicationCancellationTests.CancelAsync(db, fixture, key));
        using var server = CreateServer(db); using var client = server.CreateClient();
        if (cancelled) client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{key}/apply";
        using var response = await client.PostAsJsonAsync(route, new { paymentId = fixture.PaymentId, amount = 25m });
        Assert.Equal(cancelled ? HttpStatusCode.Conflict : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Equal(cancelled ? 1 : 0, await db.BalanceApplicationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireCancel_ClosesOnlyAnUncommittedAttemptAndNeverRefunds(bool legalEntity, bool committed)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        if (committed) Result(await ApplyAsync(db, fixture, request));
        using var server = CreateServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{request.RequestId}/cancel";
        using var response = await client.PostAsJsonAsync(route, new { paymentId = fixture.PaymentId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(committed ? "Committed" : "Cancelled", json.GetProperty("state").GetString());
        Assert.Equal(fixture.PaymentId, json.GetProperty("paymentId").GetInt32());
        Assert.Equal(request.RequestId, json.GetProperty("requestId").GetGuid());
        if (committed) Assert.Equal(25m, json.GetProperty("result").GetProperty("appliedAmount").GetDecimal());
        else Assert.Equal(JsonValueKind.Null, json.GetProperty("result").ValueKind);
        using var repeated = await client.PostAsJsonAsync(route, new { paymentId = fixture.PaymentId });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var original = await ApplyAsync(db, fixture, request);
        if (committed) Assert.True(Result(original).IsReplay); else Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(original);
        Assert.Equal(committed ? 25m : 0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(committed ? 2 : 1, await db.ClientBalanceTransactions.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireCancel_AnonymousOrMalformedBodyNeverCreatesAFence(bool legalEntity, bool anonymous)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        using var server = CreateServer(db); using var client = server.CreateClient();
        if (!anonymous) client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{Guid.NewGuid()}/cancel";
        using var response = await client.PostAsJsonAsync(route, new { paymentId = "invalid" });
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireInspect_ReturnsNoStoreAndDistinguishesCommittedFromUnconfirmed(bool legalEntity, bool committed)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); var request = Request(fixture);
        if (committed) Result(await ApplyAsync(db, fixture, request));
        using var server = CreateServer(db); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{request.RequestId}?paymentId={fixture.PaymentId}";
        using var response = await client.GetAsync(route);
        Assert.Equal(committed ? HttpStatusCode.OK : HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(request.RequestId, json.GetProperty("requestId").GetGuid());
        Assert.Equal(fixture.PaymentId, json.GetProperty("paymentId").GetInt32());
        Assert.Equal(committed ? "Committed" : "Unconfirmed", json.GetProperty("state").GetString());
        if (committed) Assert.True(json.GetProperty("result").GetProperty("isReplay").GetBoolean());
        else Assert.Equal(JsonValueKind.Null, json.GetProperty("result").ValueKind);
        Assert.False(json.TryGetProperty("commandSha256", out _));
        Assert.Equal(committed ? 25m : 0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WireInspect_AnonymousOrInvalidQueryDoesNotReadAReceipt(bool legalEntity, bool anonymous)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        using var server = CreateServer(db); using var client = server.CreateClient();
        if (!anonymous) client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = $"/api/{(legalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/applications/{Guid.NewGuid()}?paymentId=invalid";
        using var response = await client.GetAsync(route);
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Wire_OptionalIdentityKeepsLegacyAndReturnsOriginalReplay(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        using var server = CreateServer(db);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var route = Route(fixture);
        var key = Guid.NewGuid();
        object body = legacy ? new { paymentId = fixture.PaymentId, amount = 25m } : new { paymentId = fixture.PaymentId, amount = 25m, requestId = key };
        using var first = await client.PostAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(25m, firstJson.GetProperty("appliedAmount").GetDecimal());
        Assert.Equal(75m, firstJson.GetProperty("balance").GetDecimal());
        if (legacy)
        {
            Assert.False(firstJson.TryGetProperty("requestId", out _));
            Assert.False(firstJson.TryGetProperty("isReplay", out _));
            Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
        }
        else
        {
            Assert.Equal(key, firstJson.GetProperty("requestId").GetGuid());
            Assert.False(firstJson.GetProperty("isReplay").GetBoolean());
            using var repeated = await client.PostAsJsonAsync(route, body);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            var json = await repeated.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(key, json.GetProperty("requestId").GetGuid());
            Assert.True(json.GetProperty("isReplay").GetBoolean());
            Assert.Equal(25m, json.GetProperty("appliedAmount").GetDecimal());
            Assert.Equal(75m, json.GetProperty("balance").GetDecimal());
        }
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Wire_AnonymousOrMalformedIdentityNeverChangesMoney(bool legalEntity, bool anonymous)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        using var server = CreateServer(db);
        using var client = server.CreateClient();
        if (!anonymous) client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        using var response = await client.PostAsJsonAsync(Route(fixture), new { paymentId = fixture.PaymentId, amount = 25m, requestId = "malformed-guid" });
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    private static string Route(Fixture fixture) => $"/api/{(fixture.LegalEntity ? "legal-entities" : "clients")}/{fixture.OwnerId}/balance/apply";
    private static TestServer CreateServer(PayPlanner.Api.Data.PaymentContext db) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging(); services.AddSingleton(db);
            services.AddAuthentication("balance-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("balance-fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(ClientBalancesController).Assembly);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

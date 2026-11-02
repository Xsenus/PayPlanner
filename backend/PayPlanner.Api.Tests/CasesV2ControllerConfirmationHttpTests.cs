using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseConfirmationHttp_RejectsSourceChangedBeforeRequestWithoutMarkOrHistory(bool hearing)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        var body = ConfirmationBody(clientCase, hearing);
        if (hearing)
            await db.ClientCases.Where(row => row.Id == clientCase.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.NextHearingAt, clientCase.NextHearingAt!.Value.AddDays(1)));
        else
            await db.ClientCases.Where(row => row.Id == clientCase.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Strategy, "Новая позиция"));
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync(ConfirmationRoute(clientCase.Id, hearing), body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Null(actual.PositionReviewedAt);
        Assert.Null(actual.NextHearingConfirmedAt);
        Assert.Empty(actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    public static IEnumerable<object[]> InvalidConfirmationBodies()
    {
        foreach (var hearing in new[] { false, true })
            foreach (var legacy in new[] { false, true })
                foreach (var mode in new[] { "absent", "null-body", "missing", "null-source", "wrong-type" })
                    yield return [hearing, legacy, mode];
    }

    [Theory]
    [MemberData(nameof(InvalidConfirmationBodies))]
    public async Task CaseConfirmationHttp_MissingOrMalformedSourceFailsClosedOnBothRoutes(bool hearing, bool legacy, string mode)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        var property = hearing ? "expectedNextHearingAt" : "expectedStrategy";
        var json = mode switch
        {
            "absent" => string.Empty,
            "null-body" => "null",
            "missing" => "{}",
            "null-source" => $"{{\"{property}\":null}}",
            _ => $"{{\"{property}\":12}}",
        };
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(ConfirmationRoute(clientCase.Id, hearing, legacy), content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoConfirmationAsync(db);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseConfirmationHttp_ExactSourceReplaysSameMarkAndHistoryOnce(bool hearing, bool legacy)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        var route = ConfirmationRoute(clientCase.Id, hearing, legacy);
        using var first = await client.PostAsJsonAsync(route, ConfirmationBody(clientCase, hearing));
        using var repeated = await client.PostAsJsonAsync(route, ConfirmationBody(clientCase, hearing));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var firstJson = await first.Content.ReadFromJsonAsync<JsonElement>();
        var repeatedJson = await repeated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstJson.EnumerateObject().Select(property => property.Name).Order(), repeatedJson.EnumerateObject().Select(property => property.Name).Order());
        foreach (var property in firstJson.EnumerateObject())
        {
            var repeatedValue = repeatedJson.GetProperty(property.Name);
            if (property.Name.EndsWith("At", StringComparison.Ordinal))
                Assert.Equal(CalendarTimeZoneHelper.NormalizeUtcInstant(property.Value.GetDateTime()), CalendarTimeZoneHelper.NormalizeUtcInstant(repeatedValue.GetDateTime()));
            else
                Assert.Equal(property.Value.GetRawText(), repeatedValue.GetRawText());
        }
        Assert.False(firstJson.TryGetProperty("expectedStrategy", out _));
        Assert.False(firstJson.TryGetProperty("expectedNextHearingAt", out _));
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        var text = hearing ? "Дата следующего заседания проверена" : "Позиция по делу проверена";
        Assert.Equal(1, actual.EventHistory.Split(text, StringSplitOptions.None).Length - 1);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseConfirmationHttp_AlreadyReviewedSourceStillRejectsDifferentExpectation(bool hearing)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        var route = ConfirmationRoute(clientCase.Id, hearing);
        using var first = await client.PostAsJsonAsync(route, ConfirmationBody(clientCase, hearing));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = await db.ClientCases.AsNoTracking().SingleAsync();
        object different = hearing
            ? new { expectedNextHearingAt = clientCase.NextHearingAt!.Value.AddTicks(1) }
            : new { expectedStrategy = clientCase.Strategy + " " };
        using var response = await client.PostAsJsonAsync(route, different);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(before.PositionReviewedAt, after.PositionReviewedAt);
        Assert.Equal(before.NextHearingConfirmedAt, after.NextHearingConfirmedAt);
        Assert.Equal(before.EventHistory, after.EventHistory);
    }

    [Theory]
    [InlineData("2099-11-03T12:00:00", 0)]
    [InlineData("2099-11-03T12:00:00Z", 0)]
    [InlineData("2099-11-03T21:00:00+09:00", 0)]
    [InlineData("2099-11-03T12:00:00.1234567", 1234567)]
    [InlineData("2099-11-03T12:00:00.1234567Z", 1234567)]
    [InlineData("2099-11-03T21:00:00.1234567+09:00", 1234567)]
    public async Task CaseConfirmationHttp_DateUsesUtcAndPreservesSubMillisecondPrecision(string expected, long ticks)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        clientCase.NextHearingAt = clientCase.NextHearingAt!.Value.AddTicks(ticks);
        await db.SaveChangesAsync();
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync(ConfirmationRoute(clientCase.Id, true), new { expectedNextHearingAt = expected });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal(clientCase.NextHearingAt, actual.NextHearingConfirmedForAt);
    }

    [Theory]
    [InlineData("not-date")]
    [InlineData("2099-02-30T12:00:00Z")]
    [InlineData("")]
    [InlineData("12:00")]
    public async Task CaseConfirmationHttp_InvalidDateCannotConfirm(string expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync(ConfirmationRoute(clientCase.Id, true), new { expectedNextHearingAt = expected });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoConfirmationAsync(db);
    }

    public static IEnumerable<object[]> ConfirmationAccessFailures()
    {
        foreach (var hearing in new[] { false, true })
            foreach (var mode in new[] { "anonymous", "inactive", "unapproved", "permission", "hidden", "missing" })
                yield return [hearing, mode];
    }

    [Theory]
    [MemberData(nameof(ConfirmationAccessFailures))]
    public async Task CaseConfirmationHttp_CurrentAccessWinsOverAdminClaim(bool hearing, string mode)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedConfirmationCaseAsync(db);
        var expected = HttpStatusCode.Unauthorized;
        if (mode == "inactive")
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else if (mode == "unapproved")
            await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        else if (mode == "permission")
        {
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
            expected = HttpStatusCode.Forbidden;
        }
        else if (mode == "hidden")
        {
            await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
            await db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true));
            expected = HttpStatusCode.NotFound;
        }
        else if (mode == "missing") expected = HttpStatusCode.NotFound;
        using var server = ConfirmationServer(db, user);
        using var client = server.CreateClient();
        if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync(ConfirmationRoute(mode == "missing" ? clientCase.Id + 1000 : clientCase.Id, hearing), ConfirmationBody(clientCase, hearing));
        Assert.Equal(expected, response.StatusCode);
        await AssertNoConfirmationAsync(db);
    }

    private static async Task AssertNoConfirmationAsync(PaymentContext db)
    {
        var actual = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Null(actual.PositionReviewedAt);
        Assert.Null(actual.NextHearingConfirmedAt);
        Assert.Empty(actual.EventHistory);
        Assert.Equal(123.45m, actual.CourtClaimAmount);
        Assert.Empty(await db.Payments.ToArrayAsync());
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static object ConfirmationBody(ClientCase clientCase, bool hearing) => hearing
        ? new { expectedNextHearingAt = clientCase.NextHearingAt }
        : new { expectedStrategy = clientCase.Strategy };

    private static string ConfirmationRoute(int id, bool hearing, bool legacy = false)
        => $"/api/v2/cases/{id}/{(hearing ? "next-hearing-confirmation" : "position-review")}{(legacy ? string.Empty : "/checked")}";

    private static TestServer ConfirmationServer(PaymentContext db, User user) => new(new WebHostBuilder()
        .UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
        .ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddAuthentication("confirmation-fixture")
                .AddScheme<AuthenticationSchemeOptions, ConfirmationAuthentication>("confirmation-fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(CasesV2Controller).Assembly);
            services.AddSingleton<IControllerActivator>(new ConfirmationControllerActivator(db, user));
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    private sealed class ConfirmationControllerActivator(PaymentContext db, User user) : IControllerActivator
    {
        public object Create(ControllerContext context)
        {
            Assert.Equal(typeof(CasesV2Controller), context.ActionDescriptor.ControllerTypeInfo.AsType());
            var controller = CreateController(db, user);
            controller.ControllerContext = context;
            return controller;
        }

        public void Release(ControllerContext context, object controller) { }
    }

    private sealed class ConfirmationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Confirmation-User"], out var id))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, "Юрист"),
                new Claim(ClaimTypes.Role, "admin"),
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

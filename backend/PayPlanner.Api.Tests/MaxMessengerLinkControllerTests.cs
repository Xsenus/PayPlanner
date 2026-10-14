using System.Security.Claims;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MaxMessengerLinkControllerTests
{
    [Fact]
    public async Task HttpStatusRequiresAuthenticationAndDoesNotCacheOrExposeSecrets()
    {
        var service = new LinkService();
        using var server = Server(service);
        using var client = server.CreateClient();
        using var anonymous = await client.GetAsync("/api/max/link");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(service.LastUserId);
        client.DefaultRequestHeaders.Add("X-Fixture-User", "7");
        using var response = await client.GetAsync("/api/max/link?userId=999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(7, service.LastUserId);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unlinked", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(new[] { "channelReady", "expiresAt", "linkId", "status", "verifiedAt" }, body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task HttpCreateAndOptOutArePersonalAndAnonymousWritesAreRejected()
    {
        var service = new LinkService { Error = "Канал MAX выключен или настроен не полностью." };
        using var server = Server(service);
        using var client = server.CreateClient();
        using var anonymousCreate = await client.PostAsync("/api/max/link", null);
        using var anonymousDelete = await client.DeleteAsync("/api/max/link");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousDelete.StatusCode);
        Assert.Null(service.LastUserId);
        client.DefaultRequestHeaders.Add("X-Fixture-User", "8");
        using var unavailable = await client.PostAsync("/api/max/link?userId=999", null);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
        Assert.Equal(8, service.LastUserId);
        using var deleted = await client.DeleteAsync("/api/max/link?userId=999");
        using var repeated = await client.DeleteAsync("/api/max/link");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.True(deleted.Headers.CacheControl?.NoStore);
        Assert.Equal(8, service.LastUserId);
    }

    [Fact]
    public async Task HttpCreateReturnsAnUncachedPersonalLink()
    {
        var service = new LinkService();
        using var server = Server(service);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", "7");
        using var response = await client.PostAsync("/api/max/link?userId=999", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(7, service.LastUserId);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://max.ru/TestBot?start=fixture", body.RootElement.GetProperty("deepLinkUrl").GetString());
        Assert.EndsWith("Z", body.RootElement.GetProperty("expiresAt").GetString());
    }

    private static TestServer Server(LinkService service) => new(new WebHostBuilder()
        .ConfigureServices(services =>
        {
            services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthHandler>("fixture", _ => { });
            services.AddAuthorization();
            services.AddControllers().AddApplicationPart(typeof(MaxMessengerController).Assembly).AddControllersAsServices();
            services.AddScoped(_ => new MaxMessengerController(service, null!, null!, null!));
        })
        .Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));

    private sealed class FixtureAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var userId) || userId <= 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    [Fact]
    public async Task StatusUsesAuthenticatedEmployeeRatherThanQueryParameter()
    {
        var service = new LinkService();
        var controller = Create(service, "7");
        controller.Request.QueryString = new QueryString("?userId=999");
        var result = await controller.GetLink(CancellationToken.None);
        var status = Assert.IsType<MaxEmployeeLinkStatus>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(7, service.LastUserId);
        Assert.Equal("unlinked", status.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task LinkEndpointsRejectMissingOrInvalidIdentity(string? identity)
    {
        var service = new LinkService();
        var controller = Create(service, identity);
        Assert.IsType<UnauthorizedResult>((await controller.GetLink(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.CreateLink(CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>(await controller.OptOut(CancellationToken.None));
        Assert.Null(service.LastUserId);
    }

    [Fact]
    public async Task UnavailableChannelReturnsBusinessErrorInsteadOfServerError()
    {
        var service = new LinkService { Error = "Канал MAX выключен или настроен не полностью." };
        var result = await Create(service, "7").CreateLink(CancellationToken.None);
        Assert.Equal(service.Error, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task OptOutIsIdempotentWhenThereIsNoActiveLink()
    {
        var service = new LinkService();
        Assert.IsType<NoContentResult>(await Create(service, "7").OptOut(CancellationToken.None));
        Assert.Equal(7, service.LastUserId);
    }

    private static MaxMessengerController Create(LinkService service, string? identity)
    {
        var claims = identity is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.NameIdentifier, identity) };
        return new MaxMessengerController(service, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } }
        };
    }

    private sealed class LinkService : IMaxWebhookService
    {
        public int? LastUserId { get; private set; }
        public string? Error { get; init; }
        public Task<MaxEmployeeLinkStatus> GetEmployeeLinkStatusAsync(int userId, CancellationToken ct)
        {
            LastUserId = userId;
            return Task.FromResult(new MaxEmployeeLinkStatus("unlinked", true, null, null, null));
        }
        public Task<MaxLinkTokenResult> CreateEmployeeLinkAsync(int userId, CancellationToken ct)
        {
            LastUserId = userId;
            if (Error is not null) throw new MaxLinkUnavailableException(Error);
            return Task.FromResult(new MaxLinkTokenResult(Guid.NewGuid(), "fixture", "https://max.ru/TestBot?start=fixture", DateTime.UtcNow.AddMinutes(15)));
        }
        public Task<bool> OptOutEmployeeAsync(int userId, CancellationToken ct) { LastUserId = userId; return Task.FromResult(false); }
        public Task<MaxLinkTokenResult> CreatePortalLinkAsync(int portalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OptOutPortalAsync(int portalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<MaxWebhookResult> ProcessAsync(string rawJson, string? suppliedSecret, CancellationToken ct) => throw new NotSupportedException();
    }
}

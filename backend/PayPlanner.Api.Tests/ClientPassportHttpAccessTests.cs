using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ClientPassportHttpAccessTests
{
    [Fact]
    public async Task ClientDetails_RevokedViewReturnsForbiddenWithoutEndingSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "client-details-viewer" };
        var permission = new RolePermission { Role = role, Section = RolePermissionCatalog.Clients, CanView = true, ViewScope = DataVisibilityScope.Own };
        var user = new User { Role = role, Email = "details-viewer@example.test", FullName = "Viewer", PasswordHash = "x", IsActive = true, IsApproved = true };
        var other = new User { Role = role, Email = "details-other@example.test", FullName = "Other", PasswordHash = "x", IsActive = true, IsApproved = true };
        db.AddRange(permission, user, other);
        await db.SaveChangesAsync();
        var own = new Client { Name = "Own", ResponsibleUserId = user.Id };
        var hidden = new Client { Name = "Hidden", ResponsibleUserId = other.Id };
        db.Clients.AddRange(own, hidden);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("passport-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("passport-fixture", _ => { });
                services.AddAuthorization(); services.AddControllers().AddApplicationPart(typeof(ClientPassportController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v2/clients/{own.Id}")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v2/clients/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/clients/{hidden.Id}")).StatusCode);
        permission.CanView = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v2/clients/{own.Id}")).StatusCode);
    }

    [Fact]
    public async Task ExistingSession_RespectsClientScopeAndCurrentViewEditRights()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "passport-viewer", Description = "viewer" };
        var permission = new RolePermission { Role = role, Section = RolePermissionCatalog.Clients, CanView = true, ViewScope = DataVisibilityScope.Own };
        var user = new User { Role = role, Email = "passport-viewer@example.test", FullName = "Viewer", PasswordHash = "hash", IsActive = true, IsApproved = true };
        var other = new User { Role = role, Email = "passport-other@example.test", FullName = "Other", PasswordHash = "hash", IsActive = true, IsApproved = true };
        db.RolePermissions.Add(permission);
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        var own = new Client { Name = "Own", ResponsibleUserId = user.Id };
        var hidden = new Client { Name = "Hidden", ResponsibleUserId = other.Id };
        db.Clients.AddRange(own, hidden);
        await db.SaveChangesAsync();
        db.ClientPassports.AddRange(new ClientPassport { ClientId = own.Id, Number = "111111", IsPrimary = true }, new ClientPassport { ClientId = hidden.Id, Number = "222222", IsPrimary = true });
        await db.SaveChangesAsync();

        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("passport-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("passport-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(ClientPassportController).Assembly);
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/clients/{own.Id}/passport")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/clients/{hidden.Id}/passport")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/clients/{own.Id}/passport", new { number = "333333" })).StatusCode);
        permission.CanEdit = true;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/clients/{hidden.Id}/passport", new { number = "333333" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/clients/{own.Id}/passport", new { number = "444444" })).StatusCode);
        permission.CanView = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/clients/{own.Id}/passport")).StatusCode);
        Assert.Equal("222222", await db.ClientPassports.Where(item => item.ClientId == hidden.Id).Select(item => item.Number).SingleAsync());
    }

    private sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)), new Claim(ClaimTypes.Role, "admin")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

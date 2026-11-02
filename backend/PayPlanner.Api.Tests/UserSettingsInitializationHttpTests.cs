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
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserSettingsInitializationHttpTests
{
    [Fact]
    public async Task Initialize_RouteRequiresAuthenticationAndPreservesTheCurrentActorsExplicitChoice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        int actorId; int otherId;
        await using (var seed = new PaymentContext(options, useObjectAuditModel: false))
        {
            await seed.Database.EnsureCreatedAsync();
            var role = new Role { Name = "synthetic-member", Description = "Isolated HTTP test" };
            var actor = new User { Email = "http-settings-owner@example.test", FullName = "Synthetic owner", PasswordHash = "hash", IsActive = true, IsApproved = true, Role = role };
            var other = new User { Email = "http-settings-other@example.test", FullName = "Synthetic other", PasswordHash = "hash", IsActive = true, IsApproved = true, Role = role };
            seed.Users.AddRange(actor, other); await seed.SaveChangesAsync(); actorId = actor.Id; otherId = other.Id;
            seed.UserSettings.Add(new UserSetting { UserId = otherId, Key = "profile.timeZoneId", Value = "Asia/Novosibirsk" }); await seed.SaveChangesAsync();
        }

        // MVC/authentication and the real controller are exercised without Program,
        // seeders, hosted jobs, SMTP or an external listener/database server.
        using var server = new TestServer(new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddScoped(_ => new PaymentContext(options, useObjectAuditModel: false));
            services.AddAuthentication("SyntheticSettingActor")
                .AddScheme<AuthenticationSchemeOptions, SettingActorHandler>("SyntheticSettingActor", _ => { });
            services.AddAuthorization(auth => auth.AddPolicy("Admin", policy => policy.RequireRole("admin")));
            services.AddControllers().AddApplicationPart(typeof(UserSettingsController).Assembly);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers());
        }));
        using var client = server.CreateClient();
        const string path = "/api/user-settings/profile.timeZoneId";
        using (var guest = await client.PutAsJsonAsync(path + "/initialize", new { value = "UTC" })) Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);
        client.DefaultRequestHeaders.Add("X-Synthetic-User", actorId.ToString());
        using (var missing = await client.GetAsync(path)) Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        using (var initialized = await client.PutAsJsonAsync(path + "/initialize", new { value = "UTC", userId = otherId }))
        {
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Equal("UTC", (await initialized.Content.ReadFromJsonAsync<UserSettingDto>())!.Value);
        }
        using (var explicitChoice = await client.PutAsJsonAsync(path, new { value = "Europe/Samara" }))
        {
            Assert.Equal(HttpStatusCode.OK, explicitChoice.StatusCode);
            Assert.Equal(DateTimeKind.Utc, (await explicitChoice.Content.ReadFromJsonAsync<UserSettingDto>())!.UpdatedAt.Kind);
        }
        using (var lateDefault = await client.PutAsJsonAsync(path + "/initialize", new { value = "Europe/Moscow" }))
        {
            Assert.Equal(HttpStatusCode.OK, lateDefault.StatusCode);
            Assert.Equal("Europe/Samara", (await lateDefault.Content.ReadFromJsonAsync<UserSettingDto>())!.Value);
        }
        using (var otherUser = await client.PutAsJsonAsync($"/api/user-settings/users/{otherId}/profile.timeZoneId", new { value = "UTC" }))
            Assert.Equal(HttpStatusCode.Forbidden, otherUser.StatusCode);
        await using var verify = new PaymentContext(options, useObjectAuditModel: false);
        Assert.Equal(2, await verify.UserSettings.CountAsync());
        Assert.Equal("Europe/Samara", await verify.UserSettings.Where(row => row.UserId == actorId).Select(row => row.Value).SingleAsync());
        Assert.Equal("Asia/Novosibirsk", await verify.UserSettings.Where(row => row.UserId == otherId).Select(row => row.Value).SingleAsync());
        Assert.Empty(await verify.Payments.ToListAsync()); Assert.Empty(await verify.ClientCases.ToListAsync()); Assert.Empty(await verify.TaskJournalItems.ToListAsync());
    }

    private sealed class SettingActorHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public SettingActorHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Synthetic-User", out var header)) return Task.FromResult(AuthenticateResult.NoResult());
            if (!int.TryParse(header.ToString(), out var id)) return Task.FromResult(AuthenticateResult.Fail("Invalid synthetic actor."));
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, "member")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

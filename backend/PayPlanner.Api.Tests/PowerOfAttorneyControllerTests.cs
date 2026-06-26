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

public class PowerOfAttorneyControllerTests
{
    [Fact]
    public async Task Create_WithMinimalPayload_SucceedsThroughMvcPipeline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);

        using var httpClient = server.CreateClient();
        using var response = await httpClient.PostAsJsonAsync($"/api/clients/{clientId}/powers-of-attorney", new
        {
            number = "63 AA 9307025",
            issuedAt = "2026-04-10",
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<PowerOfAttorney>();
        Assert.NotNull(dto);
        Assert.Equal("63 AA 9307025", dto!.Number);
        Assert.Equal(new DateTime(2026, 4, 10), dto.IssuedAt.Date);
        Assert.True(dto.IsActive);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var stored = await db.PowersOfAttorney.AsNoTracking().SingleAsync(item => item.Id == dto.Id);

        Assert.Equal(clientId, stored.ClientId);
        Assert.Equal("63 AA 9307025", stored.Number);
        Assert.Equal(new DateTime(2026, 4, 10), stored.IssuedAt.Date);
    }

    [Fact]
    public async Task Update_WithMinimalPayload_SucceedsThroughMvcPipeline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);

        using var httpClient = server.CreateClient();
        using var response = await httpClient.PutAsJsonAsync($"/api/clients/{clientId}/powers-of-attorney/{poaId}", new
        {
            number = "77 BB 1234567",
            issuedAt = "2026-04-12",
            isActive = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<PowerOfAttorney>();
        Assert.NotNull(dto);
        Assert.Equal("77 BB 1234567", dto!.Number);
        Assert.Equal(new DateTime(2026, 4, 12), dto.IssuedAt.Date);
        Assert.False(dto.IsActive);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var stored = await db.PowersOfAttorney.AsNoTracking().SingleAsync(item => item.Id == poaId);

        Assert.Equal("77 BB 1234567", stored.Number);
        Assert.Equal(new DateTime(2026, 4, 12), stored.IssuedAt.Date);
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.UpdatedAt);
    }

    private static async Task<TestServer> CreateServerAsync(SqliteConnection connection)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddHttpContextAccessor();
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.AddAuthorization();
                services.AddDbContext<PaymentContext>(options => options.UseSqlite(connection));
                services.AddControllers()
                    .AddApplicationPart(typeof(PowerOfAttorneyController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });

        var server = new TestServer(builder);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedAuthenticatedUserAsync(db);

        return server;
    }

    private static async Task SeedAuthenticatedUserAsync(PaymentContext db)
    {
        var role = new Role { Name = "client-editor", Description = "Client editor" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            ViewScope = DataVisibilityScope.All,
        });
        db.Users.Add(new User
        {
            Id = TestAuthHandler.UserId,
            Email = "poa-test@example.test",
            PasswordHash = "hash",
            FullName = "Integration Test User",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CreateClientAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var client = new Client
        {
            Name = "Тестовый клиент",
            IsActive = true,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    private static async Task<int> CreatePowerOfAttorneyAsync(IServiceProvider services, int clientId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var poa = new PowerOfAttorney
        {
            ClientId = clientId,
            Number = "Исходная доверенность",
            IssuedAt = new DateTime(2026, 4, 1),
            IsActive = true,
        };

        db.PowersOfAttorney.Add(poa);
        await db.SaveChangesAsync();
        return poa.Id;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";
        public const int UserId = 999;

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
                new Claim(ClaimTypes.Name, "Integration Test User"),
            };

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class PowerOfAttorneyControllerTests
{
    public static IEnumerable<object[]> VisibilityOperations()
    {
        foreach (var operation in new[] { "GET", "POST", "PUT", "DELETE" })
        {
            yield return new object[] { operation, DataVisibilityScope.Own, 999, "Legal", true };
            yield return new object[] { operation, DataVisibilityScope.Own, 1000, "Legal", false };
            yield return new object[] { operation, DataVisibilityScope.Department, 1000, "Legal", true };
            yield return new object[] { operation, DataVisibilityScope.Department, 1000, "Other", false };
            yield return new object[] { operation, DataVisibilityScope.All, 1000, "Other", true };
            yield return new object[] { operation, DataVisibilityScope.Own, 0, "Other", false };
        }
    }

    [Theory]
    [MemberData(nameof(VisibilityOperations))]
    public async Task Operations_RespectTheCurrentClientVisibility(string operation, string viewScope, int ownerId, string otherDepartment, bool allowed)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        await ConfigureVisibilityAsync(server.Services, viewScope, otherDepartment);
        var clientId = await CreateClientAsync(server.Services, ownerId == 0 ? null : ownerId);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        using var httpClient = server.CreateClient();
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(allowed ? SuccessStatus(operation) : HttpStatusCode.NotFound, response.StatusCode);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var rows = await db.PowersOfAttorney.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.All(rows, item => Assert.Equal(clientId, item.ClientId));
        if (!allowed)
        {
            Assert.Equal("Исходная доверенность", Assert.Single(rows).Number);
            Assert.DoesNotContain("Исходная доверенность", await response.Content.ReadAsStringAsync());
        }
        else if (operation == "GET") Assert.Equal(poaId, Assert.Single((await response.Content.ReadFromJsonAsync<List<PowerOfAttorney>>())!).Id);
        else if (operation == "DELETE") Assert.Empty(rows);
        else
        {
            var saved = await response.Content.ReadFromJsonAsync<PowerOfAttorney>();
            Assert.NotNull(saved);
            Assert.Equal(clientId, saved.ClientId);
            Assert.Equal("Текущая запись", saved.Number);
            Assert.Equal(operation == "POST" ? 2 : 1, rows.Count);
            if (operation == "PUT") Assert.Equal(poaId, saved.Id);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Operations_DenyMissingClientViewEvenWhenMutationsAreAllowed(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        await ChangePermissionAsync(server.Services, permission => permission.CanView = false);
        using var httpClient = server.CreateClient();
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    [InlineData("GET", true)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    public async Task Operations_RejectInactiveOrUnapprovedCurrentUser(string operation, bool inactive)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var user = await db.Users.SingleAsync(item => item.Id == TestAuthHandler.UserId);
            if (inactive) user.IsActive = false;
            else user.IsApproved = false;
            await db.SaveChangesAsync();
        }
        using var httpClient = server.CreateClient();
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_RechecksItsSeparatePermissionAfterAnEarlierRead(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        using var httpClient = server.CreateClient();
        using var earlier = await httpClient.GetAsync($"/api/clients/{clientId}/powers-of-attorney");
        Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
        await ChangePermissionAsync(server.Services, permission =>
        {
            if (operation == "POST") permission.CanCreate = false;
            else if (operation == "PUT") permission.CanEdit = false;
            else permission.CanDelete = false;
        });
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_CannotUseAPowerFromAnotherVisibleClient(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var otherClientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, otherClientId);
        using var httpClient = server.CreateClient();
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, otherClientId);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Operations_MissingClientDoesNotReadOrMutateExistingPowers(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        using var httpClient = server.CreateClient();
        using var response = await SendAsync(httpClient, operation, 123456, poaId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_RechecksClientAssignmentAfterAnEarlierRead(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        await ConfigureVisibilityAsync(server.Services, DataVisibilityScope.Own, "Other");
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        using var httpClient = server.CreateClient();
        using var earlier = await httpClient.GetAsync($"/api/clients/{clientId}/powers-of-attorney");
        Assert.Equal(HttpStatusCode.OK, earlier.StatusCode);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            (await db.Clients.SingleAsync(item => item.Id == clientId)).ResponsibleUserId = 1000;
            await db.SaveChangesAsync();
        }
        using var response = await SendAsync(httpClient, operation, clientId, poaId);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

    [Fact]
    public async Task Get_ManyPowersUsesOneBoundedClientProjectionAfterAccessChecks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var counter = new ReaderCounter();
        using var server = await CreateServerAsync(connection, counter);
        var clientId = await CreateClientAsync(server.Services);
        for (var i = 0; i < 12; i++) await CreatePowerOfAttorneyAsync(server.Services, clientId);
        counter.Count = 0;
        using var httpClient = server.CreateClient();
        using var response = await httpClient.GetAsync($"/api/clients/{clientId}/powers-of-attorney");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(12, (await response.Content.ReadFromJsonAsync<List<PowerOfAttorney>>())!.Count);
        Assert.InRange(counter.Count, 1, 3);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_ConfirmsTheCurrentOperationInsideTheController(string operation)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var server = await CreateServerAsync(connection);
        var clientId = await CreateClientAsync(server.Services);
        var poaId = await CreatePowerOfAttorneyAsync(server.Services, clientId);
        await ChangePermissionAsync(server.Services, permission =>
        {
            if (operation == "POST") permission.CanCreate = false;
            else if (operation == "PUT") permission.CanEdit = false;
            else permission.CanDelete = false;
        });
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var controller = new PowerOfAttorneyController(db)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, TestAuthHandler.UserId.ToString()) }, "Test")),
                    },
                },
            };
            var payload = new UpsertPowerOfAttorneyRequest { Number = "Запрещённая запись", IssuedAt = new DateTime(2026, 4, 12) };
            var result = operation switch
            {
                "POST" => await controller.Create(clientId, payload, CancellationToken.None),
                "PUT" => await controller.Update(clientId, poaId, payload, CancellationToken.None),
                _ => await controller.Delete(clientId, poaId, CancellationToken.None),
            };
            Assert.IsType<ForbidResult>(result);
        }
        await AssertOriginalAsync(server.Services, poaId, clientId);
    }

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

    private static async Task<TestServer> CreateServerAsync(SqliteConnection connection, DbCommandInterceptor? interceptor = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddHttpContextAccessor();
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                services.AddAuthorization();
                services.AddDbContext<PaymentContext>(options =>
                {
                    options.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(PowerOfAttorneyControllerTests).Assembly.FullName));
                    if (interceptor is not null) options.AddInterceptors(interceptor);
                });
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

    private static async Task<int> CreateClientAsync(IServiceProvider services, int? responsibleUserId = TestAuthHandler.UserId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var client = new Client
        {
            Name = "Тестовый клиент",
            IsActive = true,
            ResponsibleUserId = responsibleUserId,
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

    private static async Task ConfigureVisibilityAsync(IServiceProvider services, string viewScope, string otherDepartment)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        (await db.RolePermissions.SingleAsync(item => item.Section == RolePermissionCatalog.Clients)).ViewScope = viewScope;
        var user = await db.Users.SingleAsync(item => item.Id == TestAuthHandler.UserId);
        user.Department = "Legal";
        db.Users.Add(new User
        {
            Id = 1000,
            Email = "other-poa@example.test",
            PasswordHash = "hash",
            FullName = "Other owner",
            RoleId = user.RoleId,
            Department = otherDepartment,
            IsActive = true,
            IsApproved = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task ChangePermissionAsync(IServiceProvider services, Action<RolePermission> change)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        change(await db.RolePermissions.SingleAsync(item => item.Section == RolePermissionCatalog.Clients));
        await db.SaveChangesAsync();
    }

    private static async Task AssertOriginalAsync(IServiceProvider services, int poaId, int clientId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var original = Assert.Single(await db.PowersOfAttorney.AsNoTracking().ToListAsync());
        Assert.Equal(poaId, original.Id);
        Assert.Equal(clientId, original.ClientId);
        Assert.Equal("Исходная доверенность", original.Number);
    }

    private static HttpStatusCode SuccessStatus(string operation) => operation switch
    {
        "POST" => HttpStatusCode.Created,
        "DELETE" => HttpStatusCode.NoContent,
        _ => HttpStatusCode.OK,
    };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string operation, int clientId, int poaId)
    {
        var route = $"/api/clients/{clientId}/powers-of-attorney";
        var payload = new { number = "Текущая запись", issuedAt = "2026-04-12", isActive = true };
        return operation switch
        {
            "GET" => client.GetAsync(route),
            "POST" => client.PostAsJsonAsync(route, payload),
            "PUT" => client.PutAsJsonAsync($"{route}/{poaId}", payload),
            "DELETE" => client.DeleteAsync($"{route}/{poaId}"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private sealed class ReaderCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
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

using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class AuthControllerSecurityTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_auth_test_";

    [Fact]
    public void PublicEndpointsAreRateLimitedAndRequestsHaveServerSideValidation()
    {
        var login = Assert.IsAssignableFrom<MethodInfo>(typeof(AuthController).GetMethod(nameof(AuthController.Login)));
        var register = Assert.IsAssignableFrom<MethodInfo>(typeof(AuthController).GetMethod(nameof(AuthController.Register)));
        var refresh = Assert.IsAssignableFrom<MethodInfo>(typeof(AuthController).GetMethod(nameof(AuthController.RefreshSession)));
        var logout = Assert.IsAssignableFrom<MethodInfo>(typeof(AuthController).GetMethod(nameof(AuthController.Logout)));
        var me = Assert.IsAssignableFrom<MethodInfo>(typeof(AuthController).GetMethod(nameof(AuthController.Me)));

        Assert.NotNull(login.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(AuthRateLimitPolicies.LoginPolicy, login.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.NotNull(register.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(AuthRateLimitPolicies.RegisterPolicy, register.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.NotNull(refresh.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(logout.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(me.GetCustomAttribute<AuthorizeAttribute>());

        AssertInvalid(new LoginRequest { Email = "не-email", Password = string.Empty });
        AssertInvalid(new RegisterRequest
        {
            Email = "не-email",
            Password = "коротко",
            FullName = "А",
        });
    }

    [Fact]
    public void ForwardedHeadersTrustOnlyOneLocalProxyHop()
    {
        var options = new ForwardedHeadersOptions();

        LocalReverseProxyForwarding.Configure(options);

        Assert.Equal(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Contains(System.Net.IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(System.Net.IPAddress.IPv6Loopback, options.KnownProxies);
    }

    [Fact]
    public async Task PublicRateLimitsRejectRequestsAfterConfiguredThreshold()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(options =>
                    {
                        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        AuthRateLimitPolicies.AddPolicies(options);
                    });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/login", () => Results.Ok())
                            .RequireRateLimiting(AuthRateLimitPolicies.LoginPolicy);
                        endpoints.MapGet("/register", () => Results.Ok())
                            .RequireRateLimiting(AuthRateLimitPolicies.RegisterPolicy);
                    });
                });
            })
            .StartAsync();
        var client = host.GetTestClient();

        for (var index = 0; index < 30; index++)
        {
            Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync("/login")).StatusCode);
        }
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (int)(await client.GetAsync("/login")).StatusCode);

        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync("/register")).StatusCode);
        }
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            (int)(await client.GetAsync("/register")).StatusCode);
    }

    [Theory]
    [InlineData(false, true, "UserInactive")]
    [InlineData(true, false, "PendingApproval")]
    public async Task WrongPasswordDoesNotRevealAccountState(
        bool isActive,
        bool isApproved,
        string expectedState)
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("state@example.com", "Верный пароль", isActive, isApproved);

        var wrongPasswordResult = await fixture.Service.LoginAsync(
            user.Email,
            "Неверный пароль",
            CancellationToken.None);
        Assert.Null(wrongPasswordResult);

        var stateError = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.LoginAsync(
            user.Email,
            "Верный пароль",
            CancellationToken.None));
        Assert.Equal(expectedState, stateError.Message);
    }

    [Fact]
    public async Task UnknownEmailStillPerformsPasswordHashVerification()
    {
        var passwordHasher = new CountingPasswordHasher();
        await using var fixture = await Fixture.CreateAsync(passwordHasher: passwordHasher);

        var response = await fixture.Service.LoginAsync(
            "missing@example.com",
            "Неверный пароль",
            CancellationToken.None);

        Assert.Null(response);
        Assert.Equal(1, passwordHasher.VerifyCalls);
    }

    [Fact]
    public async Task CanceledLoginStopsBeforeDatabaseCommand()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Recorder.Commands.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.LoginAsync(
            "user@example.com",
            "Надёжный пароль",
            cancellation.Token));

        Assert.Empty(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task SuccessfulLoginUsesBoundedSqlAndUpdatesActivity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("login@example.com", "Надёжный пароль", true, true);
        fixture.Recorder.Commands.Clear();

        var response = await fixture.Service.LoginAsync(
            "  LOGIN@EXAMPLE.COM ",
            "Надёжный пароль",
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(user.Id, response!.User.Id);
        Assert.False(string.IsNullOrWhiteSpace(response.Token));
        Assert.Equal(2, fixture.Recorder.Commands.Count);
        Assert.Contains(fixture.Recorder.Commands, sql => sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Recorder.Commands, sql => sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.NotNull(stored.LastLoginAt);
        Assert.Equal("login", stored.LastActivityAction);
        Assert.Equal("auth", stored.LastActivitySection);
    }

    [Fact]
    public async Task RefreshRejectsRevokedTokenVersionBeforeIssuingNewJwt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("refresh@example.com", "Надёжный пароль", true, true);
        user.AuthTokenVersion = 2;
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();

        var response = await fixture.Service.RefreshSessionAsync(
            user.Id,
            tokenVersion: 1,
            CancellationToken.None);

        Assert.Null(response);
        Assert.Single(fixture.Recorder.Commands);
        Assert.Contains("AuthTokenVersion", fixture.Recorder.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutAtomicallyRevokesAllJwtAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("logout@example.com", "Надёжный пароль", true, true);
        fixture.Recorder.Commands.Clear();

        Assert.True(await fixture.Service.RevokeSessionsAsync(user.Id, 0, CancellationToken.None));
        Assert.False(await fixture.Service.RevokeSessionsAsync(user.Id, 0, CancellationToken.None));

        Assert.Equal(2, fixture.Recorder.Commands.Count);
        Assert.All(
            fixture.Recorder.Commands,
            sql => Assert.Contains("UPDATE", sql, StringComparison.OrdinalIgnoreCase));
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.Equal(1, stored.AuthTokenVersion);
        Assert.NotNull(stored.LastLogoutAt);
        Assert.Equal("logout", stored.LastActivityAction);
        Assert.Equal("auth", stored.LastActivitySection);
    }

    [Fact]
    public async Task LogoutControllerReturnsNoContentAndRejectsRepeatedVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("controller-logout@example.com", "Надёжный пароль", true, true);
        var controller = fixture.CreateController(user.Id, tokenVersion: 0);

        Assert.IsType<NoContentResult>(await controller.Logout(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.Logout(CancellationToken.None));
    }

    [Fact]
    public async Task DisabledRegistrationIsRejectedBeforeDatabaseQuery()
    {
        await using var fixture = await Fixture.CreateAsync(registrationEnabled: false);
        fixture.Recorder.Commands.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(
            new RegisterRequest
            {
                Email = "new@example.com",
                Password = "Надёжный пароль",
                FullName = "Новый пользователь",
            },
            CancellationToken.None));

        Assert.Equal("RegistrationDisabled", error.Message);
        Assert.Empty(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task ControllerReturnsStableRussianContractWhenRegistrationIsDisabled()
    {
        await using var fixture = await Fixture.CreateAsync(registrationEnabled: false);
        var controller = fixture.CreateController();

        var result = Assert.IsType<ObjectResult>(await controller.Register(
            new RegisterRequest
            {
                Email = "new@example.com",
                Password = "Надёжный пароль",
                FullName = "Новый пользователь",
            },
            CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("RegistrationDisabled", problem.Title);
        Assert.Equal("Самостоятельная регистрация сейчас отключена администратором.", problem.Detail);
    }

    [Fact]
    public async Task RegistrationNormalizesFieldsAndCreatesPendingUserWithBoundedSql()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Recorder.Commands.Clear();

        var response = await fixture.Service.RegisterAsync(
            new RegisterRequest
            {
                Email = "  NEW@EXAMPLE.COM ",
                Password = "Надёжный пароль",
                FullName = "  Новый пользователь  ",
            },
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("new@example.com", response!.Email);
        Assert.Equal("Новый пользователь", response.FullName);
        Assert.True(response.IsActive);
        Assert.False(response.IsApproved);
        Assert.Equal(4, fixture.Recorder.Commands.Count);
        Assert.Equal(1, fixture.Recorder.Commands.Count(sql => sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PostgreSqlRegistrationAndLoginWorkOnFreshDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgreSqlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                $"{PostgreSqlEnvironmentVariable} must be configured in CI.");
            return;
        }

        var databaseName = PostgreSqlDatabasePrefix + Guid.NewGuid().ToString("N");
        Assert.StartsWith(PostgreSqlDatabasePrefix, databaseName, StringComparison.Ordinal);
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        try
        {
            await using (var create = adminConnection.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();
            db.Roles.Add(new Role { Name = "user", Description = "Пользователь" });
            await db.SaveChangesAsync();
            var hasher = new PasswordHasher<User>();
            var service = new AuthService(
                db,
                CreateConfiguration(registrationEnabled: true),
                new NoopEmailService(),
                new FakeWebHostEnvironment(),
                hasher);

            var registered = await service.RegisterAsync(
                new RegisterRequest
                {
                    Email = "PG-USER@EXAMPLE.COM",
                    Password = "Надёжный пароль",
                    FullName = "Пользователь PostgreSQL",
                },
                CancellationToken.None);
            Assert.NotNull(registered);
            var user = await db.Users.SingleAsync(item => item.Id == registered!.Id);
            user.IsApproved = true;
            await db.SaveChangesAsync();

            var login = await service.LoginAsync(
                "pg-user@example.com",
                "Надёжный пароль",
                CancellationToken.None);
            Assert.NotNull(login);
            Assert.Equal(user.Id, login!.User.Id);
            Assert.Equal(
                "0",
                new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                    .ReadJwtToken(login.Token)
                    .Claims
                    .Single(claim => claim.Type == AuthSessionClaims.TokenVersionClaimType)
                    .Value);
            Assert.True(await service.RevokeSessionsAsync(user.Id, 0, CancellationToken.None));
            Assert.Null(await service.RefreshSessionAsync(user.Id, 0, CancellationToken.None));
            db.ChangeTracker.Clear();
            var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
            Assert.NotNull(stored.LastLoginAt);
            Assert.NotNull(stored.LastLogoutAt);
            Assert.Equal(1, stored.AuthTokenVersion);
        }
        finally
        {
            await using var terminate = adminConnection.CreateCommand();
            terminate.CommandText =
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @databaseName AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = adminConnection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ControllerReturnsSameUnauthorizedContractForUnknownUserAndWrongPassword()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("known@example.com", "Верный пароль", true, true);
        var controller = fixture.CreateController();

        var wrongPassword = Assert.IsType<ObjectResult>(await controller.Login(
            new LoginRequest { Email = "known@example.com", Password = "Неверный пароль" },
            CancellationToken.None));
        var unknownUser = Assert.IsType<ObjectResult>(await controller.Login(
            new LoginRequest { Email = "unknown@example.com", Password = "Неверный пароль" },
            CancellationToken.None));

        Assert.Equal(StatusCodes.Status401Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, unknownUser.StatusCode);
        Assert.Equal(
            Assert.IsType<ProblemDetails>(wrongPassword.Value).Title,
            Assert.IsType<ProblemDetails>(unknownUser.Value).Title);
    }

    private static void AssertInvalid(object model)
    {
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, true));
        Assert.NotEmpty(results);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _mvcServices;
        private readonly IPasswordHasher<User> _passwordHasher;

        private Fixture(
            SqliteConnection connection,
            PaymentContext db,
            AuthService service,
            RecordingCommandInterceptor recorder,
            ServiceProvider mvcServices,
            IPasswordHasher<User> passwordHasher)
        {
            _connection = connection;
            Db = db;
            Service = service;
            Recorder = recorder;
            _mvcServices = mvcServices;
            _passwordHasher = passwordHasher;
        }

        public PaymentContext Db { get; }
        public AuthService Service { get; }
        public RecordingCommandInterceptor Recorder { get; }

        public static async Task<Fixture> CreateAsync(
            bool registrationEnabled = true,
            IPasswordHasher<User>? passwordHasher = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var recorder = new RecordingCommandInterceptor();
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .AddInterceptors(recorder)
                .Options;
            var db = new PaymentContext(options, useObjectAuditModel: false);
            await db.Database.EnsureCreatedAsync();
            var role = new Role { Name = "user", Description = "Пользователь" };
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var configuration = CreateConfiguration(registrationEnabled);
            passwordHasher ??= new PasswordHasher<User>();
            var service = new AuthService(
                db,
                configuration,
                new NoopEmailService(),
                new FakeWebHostEnvironment(),
                passwordHasher);
            var mvcServices = new ServiceCollection()
                .AddLogging()
                .AddControllers()
                .Services
                .BuildServiceProvider();
            recorder.Commands.Clear();
            return new Fixture(connection, db, service, recorder, mvcServices, passwordHasher);
        }

        public async Task<User> AddUserAsync(
            string email,
            string password,
            bool isActive,
            bool isApproved)
        {
            var roleId = await Db.Roles.Select(item => item.Id).SingleAsync();
            var user = new User
            {
                Email = email,
                FullName = "Тестовый пользователь",
                RoleId = roleId,
                IsActive = isActive,
                IsApproved = isApproved,
            };
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            Db.Users.Add(user);
            await Db.SaveChangesAsync();
            return user;
        }

        public AuthController CreateController(int? userId = null, int tokenVersion = 0)
        {
            var httpContext = new DefaultHttpContext
            {
                RequestServices = _mvcServices,
            };
            if (userId.HasValue)
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                    AuthSessionClaims.CreateTokenVersionClaim(tokenVersion),
                ],
                authenticationType: "test"));
            }
            return new AuthController(Service)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            await _mvcServices.DisposeAsync();
        }
    }

    private static IConfiguration CreateConfiguration(bool registrationEnabled)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-auth-secret-with-at-least-thirty-two-characters",
                ["Jwt:Issuer"] = "PayPlanner.Tests",
                ["Jwt:Audience"] = "PayPlanner.Tests",
                ["Features:RegistrationEnabled"] = registrationEnabled.ToString(),
            })
            .Build();

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingPasswordHasher : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _inner = new();

        public int VerifyCalls { get; private set; }

        public string HashPassword(User user, string password)
            => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            User user,
            string hashedPassword,
            string providedPassword)
        {
            VerifyCalls++;
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

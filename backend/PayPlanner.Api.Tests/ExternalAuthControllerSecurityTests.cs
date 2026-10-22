using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ExternalAuthControllerSecurityTests
{
    private const string PostgreSqlEnvironmentVariable = "PAYPLANNER_TEST_POSTGRES";
    private const string PostgreSqlDatabasePrefix = "payplanner_external_auth_test_";

    [Fact]
    public void AnonymousEndpointsHaveDedicatedRateLimitsAndBoundedContracts()
    {
        AssertPolicy(nameof(ExternalAuthController.GetProviders), AuthRateLimitPolicies.ExternalSettingsPolicy);
        AssertPolicy(nameof(ExternalAuthController.StartExternal), AuthRateLimitPolicies.ExternalStartPolicy);
        AssertPolicy(nameof(ExternalAuthController.GetExternalStatus), AuthRateLimitPolicies.ExternalStatusPolicy);
        AssertPolicy(nameof(ExternalAuthController.ExternalCallback), AuthRateLimitPolicies.ExternalCallbackPolicy);
        AssertPolicy(nameof(ExternalAuthController.CompleteTelegramWidget), AuthRateLimitPolicies.ExternalStartPolicy);
        AssertPolicy(nameof(ExternalAuthController.StartTelegramBot), AuthRateLimitPolicies.ExternalStartPolicy);
        AssertPolicy(nameof(ExternalAuthController.GetTelegramBotStatus), AuthRateLimitPolicies.ExternalStatusPolicy);
        var webhook = AssertPolicy(nameof(ExternalAuthController.TelegramWebhook), AuthRateLimitPolicies.TelegramWebhookPolicy);
        var requestLimit = Assert.Single(
            webhook.CustomAttributes,
            attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(64L * 1024, requestLimit.ConstructorArguments[0].Value);

        AssertInvalid(new ExternalAuthStartRequest { Provider = string.Empty });
        AssertInvalid(new ExternalAuthStartRequest { Provider = "google", Intent = "delete" });
        AssertInvalid(new TelegramBotAuthStartRequest { ReturnUrl = new string('x', 1001) });
        AssertInvalid(new TelegramWidgetLoginRequest
        {
            Id = "not-number",
            AuthDate = "not-date",
            Hash = "short",
        });
    }

    [Fact]
    public async Task ExternalStartAndPollingRateLimitsRejectExcessRequests()
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
                        endpoints.MapGet("/start", () => Results.Ok())
                            .RequireRateLimiting(AuthRateLimitPolicies.ExternalStartPolicy);
                        endpoints.MapGet("/status", () => Results.Ok())
                            .RequireRateLimiting(AuthRateLimitPolicies.ExternalStatusPolicy);
                    });
                });
            })
            .StartAsync();
        var client = host.GetTestClient();

        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync("/start")).StatusCode);
        }
        Assert.Equal(StatusCodes.Status429TooManyRequests, (int)(await client.GetAsync("/start")).StatusCode);

        for (var index = 0; index < 120; index++)
        {
            Assert.Equal(StatusCodes.Status200OK, (int)(await client.GetAsync("/status")).StatusCode);
        }
        Assert.Equal(StatusCodes.Status429TooManyRequests, (int)(await client.GetAsync("/status")).StatusCode);
    }

    [Fact]
    public async Task ControllerDoesNotExposeInternalExternalAuthErrors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var controller = fixture.CreateController();

        var unsupportedProvider = Assert.IsType<ObjectResult>(await controller.StartExternal(
            new ExternalAuthStartRequest { Provider = "internal-provider-name" },
            CancellationToken.None));
        var startProblem = Assert.IsType<ProblemDetails>(unsupportedProvider.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, unsupportedProvider.StatusCode);
        Assert.Equal("ExternalAuthStartFailed", startProblem.Title);
        Assert.DoesNotContain("Unsupported external auth provider", startProblem.Detail, StringComparison.Ordinal);

        var missingState = Assert.IsType<ObjectResult>(await controller.GetExternalStatus("missing-state", CancellationToken.None));
        var stateProblem = Assert.IsType<ProblemDetails>(missingState.Value);
        Assert.Equal(StatusCodes.Status404NotFound, missingState.StatusCode);
        Assert.Equal("Запрос авторизации не найден или уже устарел.", stateProblem.Detail);

        using var update = System.Text.Json.JsonDocument.Parse("""{"update_id":1}""");
        var invalidWebhook = Assert.IsType<ObjectResult>(await controller.TelegramWebhook(update, CancellationToken.None));
        var webhookProblem = Assert.IsType<ProblemDetails>(invalidWebhook.Value);
        Assert.Equal(StatusCodes.Status401Unauthorized, invalidWebhook.StatusCode);
        Assert.Equal("Запрос Telegram не подтверждён.", webhookProblem.Detail);
    }

    [Fact]
    public async Task PublicProviderSettingsUseTwoBoundedSqlQueries()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.SystemSettings.AddRange(
            Setting("auth.google.enabled", "true"),
            Setting("auth.google.clientId", "google-client"),
            Setting("auth.google.clientSecret", "google-secret"),
            Setting("auth.telegram.enabled", "true"),
            Setting("auth.telegram.widgetEnabled", "true"),
            Setting("auth.telegram.botToken", "telegram-token"),
            Setting("auth.telegram.botUsername", "payplanner_bot"));
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("crm.elitpravo.com");

        var result = await fixture.ExternalAuth.GetPublicSettingsAsync(context.Request, CancellationToken.None);

        Assert.True(result.GoogleEnabled);
        Assert.True(result.TelegramEnabled);
        Assert.True(result.TelegramWidgetEnabled);
        Assert.Equal("payplanner_bot", result.TelegramBotUsername);
        Assert.Equal(2, fixture.Recorder.Commands.Count);
        Assert.Single(fixture.Recorder.Commands, sql => sql.Contains("SystemSettings", StringComparison.Ordinal));
        Assert.Single(fixture.Recorder.Commands, sql => sql.Contains("NotificationSenders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedOAuthStatusReadsStateOnceAndCanOnlyBeConsumedOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddApprovedUserAsync("external@example.com");
        fixture.Db.ExternalAuthRequests.Add(new ExternalAuthRequest
        {
            Provider = "google",
            State = "completed-state",
            ReturnUrl = "/calendar",
            Intent = "signin",
            Status = "completed",
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            CompletedAt = DateTime.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Recorder.Commands.Clear();

        var first = await fixture.ExternalAuth.GetExternalStatusForClientAsync("completed-state", CancellationToken.None);

        Assert.True(first.Completed);
        Assert.False(string.IsNullOrWhiteSpace(first.Token));
        Assert.Equal(
            1,
            fixture.Recorder.Commands.Count(sql =>
                sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("ExternalAuthRequests", StringComparison.Ordinal)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("consumed", (await fixture.Db.ExternalAuthRequests.SingleAsync()).Status);

        var second = await fixture.ExternalAuth.GetExternalStatusForClientAsync("completed-state", CancellationToken.None);
        Assert.Equal("consumed", second.Status);
        Assert.False(second.Completed);
        Assert.Null(second.Token);
    }

    [Fact]
    public async Task ParallelOAuthPollingReturnsTokenOnlyOnce()
    {
        var databaseName = $"external-auth-race-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).Options;

        await using (var seed = new PaymentContext(options, useObjectAuditModel: false))
        {
            await seed.Database.EnsureCreatedAsync();
            var role = new Role { Name = "user", Description = "Пользователь" };
            var user = new User
            {
                Email = "parallel@example.com",
                FullName = "Параллельный пользователь",
                PasswordHash = "external-auth-only",
                Role = role,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            seed.ExternalAuthRequests.Add(new ExternalAuthRequest
            {
                Provider = "google",
                State = "parallel-state",
                ReturnUrl = "/",
                Intent = "signin",
                Status = "completed",
                User = user,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                CompletedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var firstDb = new PaymentContext(options, useObjectAuditModel: false);
        await using var secondDb = new PaymentContext(options, useObjectAuditModel: false);
        var configuration = CreateConfiguration();
        var firstService = CreateExternalAuthService(firstDb, configuration);
        var secondService = CreateExternalAuthService(secondDb, configuration);

        var results = await Task.WhenAll(
            firstService.GetExternalStatusForClientAsync("parallel-state", CancellationToken.None),
            secondService.GetExternalStatusForClientAsync("parallel-state", CancellationToken.None));

        Assert.Single(results, result => result.Completed && !string.IsNullOrWhiteSpace(result.Token));
        Assert.Single(results, result => result.Status == "consumed" && !result.Completed && result.Token is null);
    }

    [Fact]
    public async Task CanceledProviderSettingsRequestStopsBeforeSql()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Recorder.Commands.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ExternalAuth.GetPublicSettingsAsync(new DefaultHttpContext().Request, cancellation.Token));

        Assert.Empty(fixture.Recorder.Commands);
    }

    [Fact]
    public async Task PostgreSqlExternalAuthWorksOnFreshDatabase()
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

            var recorder = new RecordingCommandInterceptor();
            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(recorder)
                .Options;
            await using var db = new PostgreSqlPaymentContext(options);
            await db.Database.MigrateAsync();
            var role = new Role { Name = "user", Description = "Пользователь" };
            var user = new User
            {
                Email = "postgres-external@example.com",
                FullName = "Внешний пользователь PostgreSQL",
                PasswordHash = "external-auth-only",
                Role = role,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.ExternalAuthRequests.Add(new ExternalAuthRequest
            {
                Provider = "yandex",
                State = "postgres-completed-state",
                ReturnUrl = "/calendar",
                Intent = "signin",
                Status = "completed",
                User = user,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                CompletedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            recorder.Commands.Clear();
            var service = CreateExternalAuthService(db, CreateConfiguration());

            var result = await service.GetExternalStatusForClientAsync("postgres-completed-state", CancellationToken.None);

            Assert.True(result.Completed);
            Assert.False(string.IsNullOrWhiteSpace(result.Token));
            Assert.Equal(
                1,
                recorder.Commands.Count(sql =>
                    sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("ExternalAuthRequests", StringComparison.Ordinal)));
            db.ChangeTracker.Clear();
            Assert.Equal("consumed", (await db.ExternalAuthRequests.SingleAsync()).Status);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(adminConnection, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static MethodInfo AssertPolicy(string methodName, string expectedPolicy)
    {
        var method = Assert.IsAssignableFrom<MethodInfo>(typeof(ExternalAuthController).GetMethod(methodName));
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(expectedPolicy, method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        return method;
    }

    private static void AssertInvalid(object model)
    {
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true));
        Assert.NotEmpty(results);
    }

    private static SystemSetting Setting(string key, string value)
        => new() { Key = key, Value = value, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "external-auth-test-secret-with-at-least-thirty-two-characters",
                ["Jwt:Issuer"] = "PayPlanner.Tests",
                ["Jwt:Audience"] = "PayPlanner.Tests",
            })
            .Build();

    private static ExternalAuthService CreateExternalAuthService(
        PaymentContext db,
        IConfiguration configuration)
    {
        var passwordHasher = new PasswordHasher<User>();
        var authService = new AuthService(
            db,
            configuration,
            new NoopEmailService(),
            new FakeWebHostEnvironment(),
            passwordHasher);
        return new ExternalAuthService(
            db,
            configuration,
            new StaticHttpClientFactory(),
            passwordHasher,
            authService,
            NullLogger<ExternalAuthService>.Instance);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _mvcServices;

        private Fixture(
            SqliteConnection connection,
            PaymentContext db,
            ExternalAuthService externalAuth,
            RecordingCommandInterceptor recorder,
            ServiceProvider mvcServices)
        {
            _connection = connection;
            Db = db;
            ExternalAuth = externalAuth;
            Recorder = recorder;
            _mvcServices = mvcServices;
        }

        public PaymentContext Db { get; }
        public ExternalAuthService ExternalAuth { get; }
        public RecordingCommandInterceptor Recorder { get; }

        public static async Task<Fixture> CreateAsync()
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
            db.Roles.Add(new Role { Name = "user", Description = "Пользователь" });
            await db.SaveChangesAsync();

            var externalAuth = CreateExternalAuthService(db, CreateConfiguration());
            var mvcServices = new ServiceCollection()
                .AddLogging()
                .AddControllers()
                .Services
                .BuildServiceProvider();
            recorder.Commands.Clear();
            return new Fixture(connection, db, externalAuth, recorder, mvcServices);
        }

        public async Task<User> AddApprovedUserAsync(string email)
        {
            var roleId = await Db.Roles.Select(item => item.Id).SingleAsync();
            var user = new User
            {
                Email = email,
                FullName = "Внешний пользователь",
                PasswordHash = "external-auth-only",
                RoleId = roleId,
                IsActive = true,
                IsApproved = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            Db.Users.Add(user);
            await Db.SaveChangesAsync();
            return user;
        }

        public ExternalAuthController CreateController()
        {
            var httpContext = new DefaultHttpContext { RequestServices = _mvcServices };
            return new ExternalAuthController(ExternalAuth)
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

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client = new(new HttpClientHandler());

        public HttpClient CreateClient(string name) => _client;
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

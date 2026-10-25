using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    private sealed class AccountSessionsFixture(BankImportProvenanceModelTests.ModelDatabase database,
        AccountScopeSeed seed, string connection, bool postgres, string? sqliteFile) : IAsyncDisposable
    {
        public AccountScopeSeed Seed { get; } = seed;

        public PaymentContext Open(params IInterceptor[] interceptors)
        {
            if (!postgres) return new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);
            var context = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(connection).AddInterceptors(interceptors).Options);
            try
            {
                context.Database.OpenConnection();
                return context;
            }
            catch (Exception error)
            {
                try { context.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }

        public static async Task<AccountSessionsFixture> CreateAsync(bool postgres, PostgreSqlModelTemplate modelTemplate, bool admin = true)
        {
            var database = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(postgres, modelTemplate);
            string? file = null;
            try
            {
                var seed = await SeedAccountScopeAsync(database.Db, admin);
                var mail = await database.Db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "notifications.email.enabled");
                if (mail is null) database.Db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else mail.Value = "false";
                await database.Db.SaveChangesAsync();
                var connection = database.Db.Database.GetConnectionString()!;
                if (!postgres)
                {
                    file = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp-account-boundary-" + Guid.NewGuid().ToString("N") + ".sqlite"));
                    connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 15 }.ToString();
                    await using var target = new SqliteConnection(connection);
                    await target.OpenAsync();
                    ((SqliteConnection)database.Db.Database.GetDbConnection()).BackupDatabase(target);
                }
                return new(database, seed, connection, postgres, file);
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                try { await database.DisposeAsync(); }
                catch (Exception cleanup) { errors.Add(cleanup); }
                try { DeleteOwnedFile(file); }
                catch (Exception cleanup) { errors.Add(cleanup); }
                if (errors.Count > 1) throw new AggregateException(errors);
                throw;
            }
        }

        public TestServer HttpServer() => new(new WebHostBuilder()
            .UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false")
            .UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped(_ => Open());
                services.AddAuthentication("account-boundary-fixture")
                    .AddScheme<AuthenticationSchemeOptions, AccountBoundaryAuthentication>("account-boundary-fixture", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
                services.AddControllers().AddApplicationPart(typeof(MoneyAccountsController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));

        public HttpClient Client(TestServer server, bool claim = true, bool authenticated = true)
        {
            var client = server.CreateClient();
            if (authenticated) client.DefaultRequestHeaders.Add("X-Fixture-User", Seed.Actor.Id.ToString(CultureInfo.InvariantCulture));
            if (claim) client.DefaultRequestHeaders.Add("X-Fixture-Admin", "true");
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            try { await database.DisposeAsync(); }
            catch (Exception error)
            {
                try { DeleteOwnedFile(sqliteFile); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
            DeleteOwnedFile(sqliteFile);
        }

        private static void DeleteOwnedFile(string? file)
        {
            if (file is null) return;
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(file));
            Assert.Matches("^pp-account-boundary-[a-f0-9]{32}\\.sqlite$", Path.GetFileName(file));
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(file + suffix);
        }
    }

    private sealed class AccountBoundaryAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var userId) || userId <= 0)
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)) };
            if (Request.Headers["X-Fixture-Admin"] == "true") claims.Add(new(ClaimTypes.Role, "admin"));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private static StringContent AccountJson(object payload) => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    [Fact]
    public async Task MoneyModelFixture_PostgreSqlRetainsSessionUntilDisposeAndRemovesOwnedDatabase()
    {
        DbConnection connection;
        string connectionString;
        string databaseName;
        await using (var fixture = await CreateModelDatabaseAsync(true))
        {
            connection = fixture.Db.Database.GetDbConnection();
            connectionString = fixture.Db.Database.GetConnectionString()!;
            databaseName = connection.Database;
            Assert.Equal(ConnectionState.Open, connection.State);
            var pid = await MoneyFixtureBackendPidAsync(fixture.Db);
            Assert.True(await fixture.Db.SystemSettings.AnyAsync(setting => setting.Key == "notifications.email.enabled" && setting.Value == "false"));
            Assert.Equal(pid, await MoneyFixtureBackendPidAsync(fixture.Db));
            Assert.Equal(ConnectionState.Open, connection.State);
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
        await AssertMoneyFixtureDatabaseAbsentAsync(connectionString, databaseName);
    }

    [Fact]
    public async Task AccountSessionsFixture_PostgreSqlKeepsIndependentScopedSessionsAndRemovesOwnedDatabase()
    {
        string connectionString;
        string databaseName;
        await using (var fixture = await CreateAccountSessionsFixtureAsync(true))
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => fixture.Open());
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            DbConnection firstConnection;
            await using (var firstScope = provider.CreateAsyncScope())
            {
                var first = firstScope.ServiceProvider.GetRequiredService<PaymentContext>();
                firstConnection = first.Database.GetDbConnection();
                connectionString = first.Database.GetConnectionString()!;
                databaseName = firstConnection.Database;
                Assert.Equal(ConnectionState.Open, firstConnection.State);
                var firstPid = await MoneyFixtureBackendPidAsync(first);
                DbConnection secondConnection;
                await using (var secondScope = provider.CreateAsyncScope())
                {
                    var second = secondScope.ServiceProvider.GetRequiredService<PaymentContext>();
                    secondConnection = second.Database.GetDbConnection();
                    Assert.NotSame(firstConnection, secondConnection);
                    Assert.Equal(ConnectionState.Open, secondConnection.State);
                    var secondPid = await MoneyFixtureBackendPidAsync(second);
                    Assert.NotEqual(firstPid, secondPid);
                    Assert.True(await first.Users.AnyAsync());
                    Assert.True(await second.Users.AnyAsync());
                    Assert.Equal(firstPid, await MoneyFixtureBackendPidAsync(first));
                    Assert.Equal(secondPid, await MoneyFixtureBackendPidAsync(second));
                }
                Assert.Equal(ConnectionState.Closed, secondConnection.State);
                Assert.Equal(ConnectionState.Open, firstConnection.State);
                Assert.Equal(firstPid, await MoneyFixtureBackendPidAsync(first));
            }
            Assert.Equal(ConnectionState.Closed, firstConnection.State);
        }
        await AssertMoneyFixtureDatabaseAbsentAsync(connectionString, databaseName);
    }

    [Fact]
    public async Task AccountSessionsFixture_PostgreSqlOpenFailureClosesConnectionAndPreservesCause()
    {
        string connectionString;
        string databaseName;
        await using (var fixture = await CreateAccountSessionsFixtureAsync(true))
        {
            var fault = new AccountConnectionOpenedFailure();
            Assert.Same(fault.Error, Assert.Throws<InvalidOperationException>(() => fixture.Open(fault)));
            Assert.NotNull(fault.Connection);
            Assert.Equal(ConnectionState.Closed, fault.Connection.State);
            await using var recovered = fixture.Open();
            connectionString = recovered.Database.GetConnectionString()!;
            databaseName = recovered.Database.GetDbConnection().Database;
            Assert.True(await recovered.Users.AnyAsync());
        }
        await AssertMoneyFixtureDatabaseAbsentAsync(connectionString, databaseName);
    }

    private static Task<int> MoneyFixtureBackendPidAsync(PaymentContext context)
        => context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static async Task AssertMoneyFixtureDatabaseAbsentAsync(string connectionString, string databaseName)
    {
        Assert.StartsWith("pp_bank_model_", databaseName);
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        Assert.False(settings.Pooling);
        settings.Database = "postgres";
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using var absent = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin);
        absent.Parameters.AddWithValue("name", databaseName);
        Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync())!);
    }

    private sealed class AccountConnectionOpenedFailure : DbConnectionInterceptor
    {
        public InvalidOperationException Error { get; } = new("Owned account connection-open failure.");
        public DbConnection? Connection { get; private set; }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Connection = connection;
            throw Error;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountHttp_ListExposesOnlyAssignedCoherentAccountAndDerivedCompany(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        using var response = await client.GetAsync("/api/money-accounts?activeOnly=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var account = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(fixture.Seed.Own.Id, account.GetProperty("id").GetInt32());
        Assert.Equal(fixture.Seed.Company.ShortName, account.GetProperty("companyName").GetString());
        Assert.False(account.TryGetProperty("company", out _));
        Assert.False(account.TryGetProperty("bankConnectionAccount", out _));
        Assert.Empty(account.GetProperty("moneyOperations").EnumerateArray());
    }

    [Theory]
    [InlineData(false, "foreign")]
    [InlineData(false, "unknown")]
    [InlineData(false, "incoherent")]
    [InlineData(true, "foreign")]
    [InlineData(true, "unknown")]
    [InlineData(true, "incoherent")]
    public async Task AccountHttp_DirectIdDoesNotDiscloseHiddenAccount(bool postgres, string kind)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        var id = kind == "foreign" ? fixture.Seed.Foreign.Id : kind == "unknown" ? fixture.Seed.Unknown.Id : fixture.Seed.Incoherent.Id;
        using var response = await client.GetAsync("/api/money-accounts/" + id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(fixture.Seed.Foreign.Name, body, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Seed.Foreign.BankAccountNumber!, body, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> AccountHttpRevokedCases =>
        from postgres in new[] { false, true }
        from endpoint in new[] { "list", "detail" }
        from reason in new[] { "inactive", "unapproved", "permission", "company" }
        select new object[] { postgres, endpoint, reason };

    [Theory]
    [MemberData(nameof(AccountHttpRevokedCases))]
    public async Task AccountHttp_ReadRechecksDatabaseAfterSessionIssued(bool postgres, string endpoint, string reason)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        await using var db = fixture.Open();
        if (reason == "inactive") await db.Users.Where(item => item.Id == fixture.Seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        if (reason == "unapproved") await db.Users.Where(item => item.Id == fixture.Seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        if (reason == "permission") await db.RolePermissions.Where(item => item.RoleId == fixture.Seed.Actor.RoleId).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CanView, false));
        if (reason == "company") await db.UserCompanyAccesses.Where(item => item.UserId == fixture.Seed.Actor.Id).ExecuteDeleteAsync();
        var before = await CaptureAccountScopeAsync(db);
        using var response = await client.GetAsync(endpoint == "list" ? "/api/money-accounts" : "/api/money-accounts/" + fixture.Seed.Own.Id);
        Assert.Equal(reason == "company" ? endpoint == "list" ? HttpStatusCode.OK : HttpStatusCode.NotFound : HttpStatusCode.Forbidden, response.StatusCode);
        if (reason == "company" && endpoint == "list") Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    public static IEnumerable<object[]> AccountHttpWriteDeniedCases =>
        from postgres in new[] { false, true }
        from verb in new[] { "POST", "PUT", "DELETE" }
        from reason in new[] { "claim-missing", "stale-admin", "unapproved" }
        select new object[] { postgres, verb, reason };

    [Theory]
    [MemberData(nameof(AccountHttpWriteDeniedCases))]
    public async Task AccountHttp_WritesRequirePolicyAndFreshDatabaseAdmin(bool postgres, string verb, string reason)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: reason != "stale-admin");
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server, claim: reason != "claim-missing");
        await using var db = fixture.Open();
        if (reason == "unapproved") await db.Users.Where(item => item.Id == fixture.Seed.Actor.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsApproved, false));
        var before = await CaptureAccountScopeAsync(db);
        using var request = new HttpRequestMessage(new HttpMethod(verb), "/api/money-accounts" + (verb == "POST" ? "" : "/" + fixture.Seed.Own.Id));
        if (verb != "DELETE") request.Content = AccountJson(NewScopedAccount(fixture.Seed));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false, "GET")]
    [InlineData(false, "POST")]
    [InlineData(false, "PUT")]
    [InlineData(false, "DELETE")]
    [InlineData(true, "GET")]
    [InlineData(true, "POST")]
    [InlineData(true, "PUT")]
    [InlineData(true, "DELETE")]
    public async Task AccountHttp_AnonymousCannotReachReadOrWrite(bool postgres, string verb)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server, authenticated: false);
        await using var db = fixture.Open();
        var before = await CaptureAccountScopeAsync(db);
        using var request = new HttpRequestMessage(new HttpMethod(verb), "/api/money-accounts" + (verb is "GET" or "POST" ? "" : "/" + fixture.Seed.Own.Id));
        if (verb is "POST" or "PUT") request.Content = AccountJson(NewScopedAccount(fixture.Seed));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    [Theory]
    [InlineData(false, "POST")]
    [InlineData(false, "PUT")]
    [InlineData(true, "POST")]
    [InlineData(true, "PUT")]
    public async Task AccountHttp_JsonCannotWriteRootIdentityOrNavigationGraph(bool postgres, string verb)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        var seed = fixture.Seed;
        using var request = new HttpRequestMessage(new HttpMethod(verb), "/api/money-accounts" + (verb == "POST" ? "" : "/" + seed.Own.Id))
        {
            Content = AccountJson(new
            {
                id = seed.Foreign.Id,
                name = "Безопасная запись",
                companyId = seed.Company.Id,
                companyName = "Подложное имя",
                source = (int)MoneyOperationSource.Bank,
                currency = "RUB",
                bankConnectionAccountId = seed.AvailableBank.Id,
                bankAccountNumber = seed.AvailableBank.AccountNumber,
                bankBik = seed.AvailableBank.Bik,
                isActive = true,
                isDefault = true,
                createdAt = "2035-01-01T00:00:00Z",
                company = new { id = seed.Other.Id, shortName = "Не сохранять компанию" },
                bankConnectionAccount = new { accountNumber = "Не сохранять банковский счёт" },
                moneyOperations = new[] { new { amount = 987m, date = "2026-10-01T00:00:00Z", counterpartyName = "Не создавать операцию" } },
            }),
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(verb == "POST" ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetInt32();
        Assert.NotEqual(seed.Foreign.Id, id);
        if (verb == "PUT") Assert.Equal(seed.Own.Id, id);
        Assert.Equal(seed.Company.ShortName, body.RootElement.GetProperty("companyName").GetString());
        Assert.True(body.RootElement.GetProperty("createdAt").GetDateTime() < new DateTime(2035, 1, 1));
        if (verb == "POST") Assert.EndsWith("/api/money-accounts/" + id, response.Headers.Location!.ToString(), StringComparison.Ordinal);
        await using var db = fixture.Open();
        Assert.Empty(await db.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        Assert.Equal(3, await db.LegalEntities.CountAsync());
        Assert.Equal(4, await db.BankConnectionAccounts.CountAsync());
        Assert.Equal(seed.Foreign.Name, (await db.MoneyAccounts.SingleAsync(item => item.Id == seed.Foreign.Id)).Name);
    }

    private sealed class AccountCompanyLockGate(bool hold) : DbCommandInterceptor
    {
        private int fired;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static bool CompanyUpdate(DbCommand command) => command.CommandText.Contains("UPDATE \"LegalEntities\"", StringComparison.Ordinal);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!hold && CompanyUpdate(command) && Interlocked.Exchange(ref fired, 1) == 0) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        {
            if (hold && result == 1 && CompanyUpdate(command) && Interlocked.Exchange(ref fired, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            return result;
        }
    }

    private sealed class AccountSqliteTransactionAttempt(TaskCompletionSource entered) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            // SQLite acquires its write lease at BEGIN IMMEDIATE, before the company UPDATE.
            if (connection is SqliteConnection) entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<(IActionResult? First, IActionResult? Second)> AccountConcurrentPairAsync(AccountSessionsFixture fixture,
        Func<PaymentContext, Task<IActionResult?>> first, Func<PaymentContext, Task<IActionResult?>> second)
    {
        var held = new AccountCompanyLockGate(true);
        var waiting = new AccountCompanyLockGate(false);
        await using var firstDb = fixture.Open(held);
        await using var secondDb = fixture.Open(waiting, new AccountSqliteTransactionAttempt(waiting.Entered));
        Assert.NotSame(firstDb.Database.GetDbConnection(), secondDb.Database.GetDbConnection());
        var firstTask = Task.Run(() => first(firstDb));
        Task<IActionResult?>? secondTask = null;
        try
        {
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            secondTask = Task.Run(() => second(secondDb));
            await waiting.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(secondTask.IsCompleted, "Second session must wait while the first owns the company write lock.");
        }
        finally
        {
            held.Release.TrySetResult();
            await Task.WhenAll(secondTask is null ? [firstTask] : new[] { firstTask, secondTask }).WaitAsync(TimeSpan.FromSeconds(45));
        }
        return (await firstTask, await secondTask!);
    }

    [Theory]
    [InlineData(false, "create-default")]
    [InlineData(false, "update-default")]
    [InlineData(false, "duplicate-name")]
    [InlineData(false, "archive-create")]
    [InlineData(false, "delete-update")]
    [InlineData(true, "create-default")]
    [InlineData(true, "update-default")]
    [InlineData(true, "duplicate-name")]
    [InlineData(true, "archive-create")]
    [InlineData(true, "delete-update")]
    public async Task AccountConcurrency_IndependentSessionsPreserveDefaultsAndHistory(bool postgres, string scenario)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        var seed = fixture.Seed;
        MoneyAccount Cash(string name) => new() { Name = name, CompanyId = seed.Company.Id, Source = MoneyOperationSource.Cash, IsActive = true, IsDefault = true };
        var firstModel = Cash("Первая касса");
        var secondModel = Cash("Вторая касса");
        int firstId = 0, secondId = 0;
        await using (var setup = fixture.Open())
        {
            if (scenario == "update-default")
            {
                secondModel.IsDefault = false;
                setup.MoneyAccounts.AddRange(firstModel, secondModel);
                await setup.SaveChangesAsync();
                firstId = firstModel.Id;
                secondId = secondModel.Id;
                secondModel.IsDefault = true;
            }
            if (scenario == "archive-create") await AddAccountHistoryAsync(setup, seed, "active");
        }
        if (scenario == "duplicate-name") { firstModel.Name = "НОВАЯ КАССА"; secondModel.Name = "новая касса"; }
        if (scenario is "archive-create" or "delete-update") secondModel = NewScopedAccount(seed);
        if (scenario == "delete-update")
        {
            secondModel.Name = seed.Own.Name;
            secondModel.BankConnectionAccountId = seed.Own.BankConnectionAccountId;
            secondModel.BankAccountNumber = seed.Own.BankAccountNumber;
            secondModel.BankBik = seed.Own.BankBik;
        }
        var pair = await AccountConcurrentPairAsync(fixture,
            async db => scenario is "archive-create" or "delete-update"
                ? await CreateMoneyAccountsController(db, seed.Actor.Id).Delete(seed.Own.Id, default)
                : scenario == "update-default"
                    ? (await CreateMoneyAccountsController(db, seed.Actor.Id).Update(firstId, firstModel, default)).Result
                    : (await CreateMoneyAccountsController(db, seed.Actor.Id).Create(firstModel, default)).Result,
            async db => scenario == "delete-update" || scenario == "update-default"
                ? (await CreateMoneyAccountsController(db, seed.Actor.Id).Update(scenario == "delete-update" ? seed.Own.Id : secondId, secondModel, default)).Result
                : (await CreateMoneyAccountsController(db, seed.Actor.Id).Create(secondModel, default)).Result);
        if (scenario is "archive-create" or "delete-update") Assert.IsType<NoContentResult>(pair.First);
        else if (scenario == "update-default") Assert.IsType<OkObjectResult>(pair.First);
        else Assert.IsType<CreatedAtActionResult>(pair.First);
        if (scenario is "duplicate-name" or "delete-update") Assert.IsType<ConflictObjectResult>(pair.Second);
        else if (scenario == "update-default") Assert.IsType<OkObjectResult>(pair.Second);
        else Assert.IsType<CreatedAtActionResult>(pair.Second);
        await using var verify = fixture.Open();
        Assert.True((await verify.MoneyAccounts.SingleAsync(item => item.Id == seed.Foreign.Id)).IsDefault);
        var source = scenario is "archive-create" or "delete-update" ? MoneyOperationSource.Bank : MoneyOperationSource.Cash;
        var active = await verify.MoneyAccounts.Where(item => item.CompanyId == seed.Company.Id && item.Source == source && item.IsActive).ToArrayAsync();
        if (scenario == "delete-update") Assert.DoesNotContain(active, item => item.IsDefault);
        else Assert.Single(active, item => item.IsDefault);
        if (scenario == "duplicate-name") Assert.Single(active);
        if (scenario == "archive-create")
        {
            Assert.False((await verify.MoneyAccounts.SingleAsync(item => item.Id == seed.Own.Id)).IsActive);
            Assert.Equal(seed.Own.Id, (await verify.MoneyOperations.SingleAsync()).MoneyAccountId);
        }
        if (scenario == "delete-update") Assert.False(await verify.MoneyAccounts.AnyAsync(item => item.Id == seed.Own.Id));
        if (scenario == "update-default") Assert.True((await verify.MoneyAccounts.SingleAsync(item => item.Id == secondId)).IsDefault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountConcurrency_FallbackNeverPromotesIncoherentBankOrigin(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        var valid = new MoneyAccount { Name = "Явный резерв", CompanyId = fixture.Seed.Company.Id, Source = MoneyOperationSource.Bank, IsActive = true };
        db.MoneyAccounts.Add(valid);
        await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await CreateMoneyAccountsController(db, fixture.Seed.Actor.Id).Delete(fixture.Seed.Own.Id, default));
        await using var verify = fixture.Open();
        Assert.True((await verify.MoneyAccounts.SingleAsync(item => item.Id == valid.Id)).IsDefault);
        Assert.False((await verify.MoneyAccounts.SingleAsync(item => item.Id == fixture.Seed.Incoherent.Id)).IsDefault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountConcurrency_FirstEligibleAccountBecomesDefaultDespiteHistoricalIncoherence(bool postgres)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        await db.MoneyAccounts.Where(item => item.Id == fixture.Seed.Own.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false).SetProperty(item => item.IsDefault, false));
        var model = NewScopedAccount(fixture.Seed);
        model.IsDefault = false;
        var saved = Assert.IsType<MoneyAccount>(Assert.IsType<CreatedAtActionResult>((await CreateMoneyAccountsController(db, fixture.Seed.Actor.Id).Create(model, default)).Result).Value);
        Assert.True(saved.IsDefault);
        await using var verify = fixture.Open();
        Assert.True((await verify.MoneyAccounts.SingleAsync(item => item.Id == saved.Id)).IsDefault);
        Assert.False((await verify.MoneyAccounts.SingleAsync(item => item.Id == fixture.Seed.Incoherent.Id)).IsDefault);
    }

    public static IEnumerable<object[]> AccountUnavailableBankCases =>
        from postgres in new[] { false, true }
        from state in new[] { "Hidden", BankAccountStatuses.Closed, BankAccountStatuses.Limited, BankAccountStatuses.Preliminary,
            BankAccountStatuses.Pending, BankAccountStatuses.Unavailable, BankAccountStatuses.NotFoundInBank }
        select new object[] { postgres, state };

    [Theory]
    [MemberData(nameof(AccountUnavailableBankCases))]
    public async Task AccountConcurrency_FallbackRejectsUnavailableLinkedBank(bool postgres, string state)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var bank = await db.BankConnectionAccounts.SingleAsync(item => item.Id == seed.AvailableBank.Id);
        bank.IsHidden = state == "Hidden";
        if (state != "Hidden") bank.Status = state;
        var invalid = NewScopedAccount(seed);
        invalid.Name = "А недоступный резерв";
        invalid.IsDefault = false;
        var valid = new MoneyAccount { Name = "Я действующий резерв", CompanyId = seed.Company.Id, Source = MoneyOperationSource.Bank, IsActive = true };
        db.MoneyAccounts.AddRange(invalid, valid);
        await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await CreateMoneyAccountsController(db, seed.Actor.Id).Delete(seed.Own.Id, default));
        await using var verify = fixture.Open();
        Assert.True((await verify.MoneyAccounts.SingleAsync(item => item.Id == valid.Id)).IsDefault);
        Assert.False((await verify.MoneyAccounts.SingleAsync(item => item.Id == invalid.Id)).IsDefault);
        Assert.False((await verify.MoneyAccounts.SingleAsync(item => item.Id == seed.Incoherent.Id)).IsDefault);
    }

    [Theory]
    [MemberData(nameof(AccountUnavailableBankCases))]
    public async Task AccountHttp_CreateRejectsUnavailableLinkedBank(bool postgres, string state)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        await db.BankConnectionAccounts.Where(item => item.Id == seed.AvailableBank.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.IsHidden, state == "Hidden").SetProperty(item => item.Status, state == "Hidden" ? BankAccountStatuses.Open : state));
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        var before = await CaptureAccountScopeAsync(db);
        using var response = await client.PostAsync("/api/money-accounts", AccountJson(NewScopedAccount(seed)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }

    public static IEnumerable<object[]> AccountActiveChoiceCases =>
        AccountUnavailableBankCases.Concat(from postgres in new[] { false, true }
                                           from state in new[] { "CompanyDraft", "CompanyArchived", "CompanyDeleted" }
                                           select new object[] { postgres, state });

    [Theory]
    [MemberData(nameof(AccountActiveChoiceCases))]
    public async Task AccountHttp_ActiveChoicesExcludeUnknownOrUnavailableOrigin(bool postgres, string state)
    {
        await using var fixture = await CreateAccountSessionsFixtureAsync(postgres, admin: false);
        await using var db = fixture.Open();
        var seed = fixture.Seed;
        var candidate = NewScopedAccount(seed);
        candidate.IsDefault = false;
        db.MoneyAccounts.Add(candidate);
        if (state.StartsWith("Company", StringComparison.Ordinal))
        {
            var company = await db.LegalEntities.SingleAsync(item => item.Id == seed.Company.Id);
            if (state == "CompanyDeleted") db.LegalEntities.Remove(company);
            else company.ProfileStatus = state == "CompanyDraft" ? CompanyProfileStatuses.Draft : CompanyProfileStatuses.Archived;
        }
        else
        {
            var bank = await db.BankConnectionAccounts.SingleAsync(item => item.Id == seed.AvailableBank.Id);
            bank.IsHidden = state == "Hidden";
            if (state != "Hidden") bank.Status = state;
        }
        await db.SaveChangesAsync();
        using var server = fixture.HttpServer();
        using var client = fixture.Client(server);
        var before = await CaptureAccountScopeAsync(db);
        using var response = await client.GetAsync("/api/money-accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToArray();
        Assert.DoesNotContain(candidate.Id, ids);
        Assert.DoesNotContain(seed.Unknown.Id, ids);
        Assert.DoesNotContain(seed.Incoherent.Id, ids);
        Assert.DoesNotContain(seed.Foreign.Id, ids);
        if (state.StartsWith("Company", StringComparison.Ordinal)) Assert.Empty(ids);
        else Assert.Equal(seed.Own.Id, Assert.Single(ids));
        Assert.Equal(before, await CaptureAccountScopeAsync(db));
    }
}

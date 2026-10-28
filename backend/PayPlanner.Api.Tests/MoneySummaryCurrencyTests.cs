using System.Security.Claims;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using Xunit;

namespace PayPlanner.Api.Tests;

public class MoneySummaryCurrencyTests
{
    private sealed class PauseFirstReconciliation : SaveChangesInterceptor
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Reached.TrySetResult(true);
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class SignalSecondRequest : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult(true);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Reconciliation_SelectedAccountConcurrentRunsDoNotDuplicateOrTouchAnotherAccount()
    {
        var connectionString = $"Data Source=balance-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=10";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).Options;
        int adminId, accountId, otherId;
        var day = new DateTime(2026, 10, 20);
        await using (var setup = new PaymentContext(baseOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            var admin = new User
            {
                Email = "concurrent@fixture.local",
                FullName = "Admin",
                PasswordHash = "hash",
                Role = new Role { Name = "admin", Description = "Synthetic" },
                IsActive = true,
                IsApproved = true
            };
            var company = new LegalEntity { ShortName = "Synthetic" };
            setup.Users.Add(admin);
            setup.LegalEntities.Add(company);
            await setup.SaveChangesAsync();
            setup.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
            var own = new MoneyAccount { Name = "Selected", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = "RUB", IsActive = true };
            var other = new MoneyAccount { Name = "Other", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = "RUB", IsActive = true };
            setup.MoneyOperations.AddRange(new MoneyOperation
            {
                MoneyAccount = own,
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = day,
                Amount = 10m
            },
                new MoneyOperation
                {
                    MoneyAccount = other,
                    Source = MoneyOperationSource.Bank,
                    Direction = MoneyOperationDirection.Income,
                    AccountingType = MoneyOperationAccountingType.Fact,
                    Currency = "RUB",
                    Date = day,
                    Amount = 20m
                });
            await setup.SaveChangesAsync();
            adminId = admin.Id; accountId = own.Id; otherId = other.Id;
        }
        var pause = new PauseFirstReconciliation();
        var secondSignal = new SignalSecondRequest();
        var firstOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).AddInterceptors(pause).Options;
        var secondOptions = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connectionString).AddInterceptors(secondSignal).Options;
        await using var firstDb = new PaymentContext(firstOptions);
        await using var secondDb = new PaymentContext(secondOptions);
        MoneyOperationsController Controller(PaymentContext db)
        {
            var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, adminId.ToString())], "Test"))
                    }
                }
            };
            controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
            return controller;
        }
        var first = Task.Run(() => Controller(firstDb).RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null, moneyAccountId: accountId));
        Task<ActionResult<MoneyBalanceReconciliationRunResultDto>> second;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Task.Run(() => Controller(secondDb).RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null, moneyAccountId: accountId));
            await secondSignal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { pause.Continue.TrySetResult(true); }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        var runs = results.Select(item => Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(item.Result).Value)).ToArray();
        Assert.Equal(1, runs.Sum(item => item.Created));
        Assert.Equal(1, runs.Sum(item => item.Updated));
        await using var verify = new PaymentContext(baseOptions);
        var saved = Assert.Single(await verify.MoneyBalanceReconciliationResults.AsNoTracking().ToListAsync());
        Assert.Equal(accountId, saved.MoneyAccountId);
        Assert.Equal(10m, saved.ClosingBalance);
        Assert.Equal(2, await verify.MoneyOperations.CountAsync());
        Assert.Equal(20m, (await verify.MoneyOperations.SingleAsync(item => item.MoneyAccountId == otherId)).Amount);
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["X-Fixture-User"].ToString();
            if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    [Fact]
    public async Task Reconciliation_RealHttpBindsAccountIdAndEnforcesPermissionsAndLegacyContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var admin = new User
        {
            Email = "http-admin@fixture.local",
            FullName = "Admin",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var role = new Role { Name = "http-employee", Description = "Synthetic" };
        var employee = new User
        {
            Email = "http-employee@fixture.local",
            FullName = "Employee",
            PasswordHash = "hash",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true
        };
        var first = new LegalEntity { ShortName = "First" };
        var second = new LegalEntity { ShortName = "Second" };
        db.Users.AddRange(admin, employee);
        db.LegalEntities.AddRange(first, second);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { first.Id, second.Id }) });
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = employee.Id, LegalEntityId = first.Id });
        var a = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = first.Id, Currency = "RUB", IsActive = true };
        var b = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = second.Id, Currency = "RUB", IsActive = true };
        var day = new DateTime(2026, 10, 20);
        db.MoneyOperations.AddRange(new MoneyOperation
        {
            MoneyAccount = a,
            AccountName = "Old",
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Currency = "RUB",
            Date = day,
            Amount = 10m
        },
            new MoneyOperation
            {
                MoneyAccount = b,
                AccountName = "Old",
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = day,
                Amount = 20m
            });
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly);
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        var path = $"/api/money-operations/balance-reconciliation/run?from=2026-10-20&to=2026-10-20&source=Bank&moneyAccountId={a.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(path, null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-User", admin.Id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/money-operations/daily-balances?from=2026-10-20&to=2026-10-20&source=Bank")).StatusCode);
        Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        client.DefaultRequestHeaders.Add("X-PayPlanner-Currency-Contract", "1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/money-operations/daily-balances?from=2026-10-20&to=2026-10-20&source=Bank")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/money-operations/balance-reconciliation/run?from=2026-10-20&to=2026-10-20&source=Bank&moneyAccountId=invalid", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/money-operations/balance-reconciliation/run?from=2026-10-20&to=2026-10-20&source=Cash&moneyAccountId={a.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/money-operations/balance-reconciliation/run?from=2026-10-20&to=2026-10-20&source=Bank&accountName=Old", null)).StatusCode);
        Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        using var response = await client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<MoneyBalanceReconciliationRunResultDto>();
        Assert.NotNull(payload);
        var result = Assert.Single(payload.Items);
        Assert.Equal(a.Id, result.MoneyAccountId);
        Assert.Equal(10m, result.Income);
        Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        client.DefaultRequestHeaders.Remove("X-PayPlanner-Currency-Contract");
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/money-operations/balance-reconciliation?from=2026-10-20&to=2026-10-20&source=Bank")).StatusCode);
        client.DefaultRequestHeaders.Add("X-PayPlanner-Currency-Contract", "1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/money-operations/balance-reconciliation?from=2026-10-20&to=2026-10-20&source=Bank")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-User");
        client.DefaultRequestHeaders.Add("X-Fixture-User", employee.Id.ToString());
        var foreignPath = $"/api/money-operations/balance-reconciliation/run?from=2026-10-20&to=2026-10-20&source=Bank&moneyAccountId={b.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(foreignPath, null)).StatusCode);
        var permission = await db.RolePermissions.SingleAsync(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Payments);
        permission.CanEdit = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(path, null)).StatusCode);
        Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
    }

    [Fact]
    public async Task Reconciliation_UsesAccountIdAndRejectsForeignOrAmbiguousSelection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var admin = new User
        {
            Email = "identity-admin@fixture.local",
            FullName = "Admin",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var role = new Role { Name = "identity-employee", Description = "Synthetic" };
        var employee = new User
        {
            Email = "identity-employee@fixture.local",
            FullName = "Employee",
            PasswordHash = "hash",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true
        };
        var first = new LegalEntity { ShortName = "First" };
        var second = new LegalEntity { ShortName = "Second" };
        db.Users.AddRange(admin, employee);
        db.LegalEntities.AddRange(first, second);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { first.Id, second.Id }) });
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, CanEdit = true, ViewScope = DataVisibilityScope.All });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = employee.Id, LegalEntityId = first.Id });
        var a = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = first.Id, Currency = "RUB", IsActive = true };
        var b = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = second.Id, Currency = "RUB", IsActive = true };
        var day = new DateTime(2026, 10, 20);
        db.MoneyOperations.AddRange(new MoneyOperation
        {
            MoneyAccount = a,
            AccountName = "Old",
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Currency = "RUB",
            Date = day,
            Amount = 10m
        },
            new MoneyOperation
            {
                MoneyAccount = b,
                AccountName = "Old",
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = day,
                Amount = 20m
            });
        await db.SaveChangesAsync();
        MoneyOperationsController Controller(User user)
        {
            var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                    }
                }
            };
            controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
            return controller;
        }
        var adminController = Controller(admin);
        Assert.IsType<BadRequestObjectResult>((await adminController.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, "Old")).Result);
        Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        var selected = await adminController.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, "outdated label", moneyAccountId: a.Id);
        var result = Assert.IsType<MoneyBalanceReconciliationRunResultDto>(Assert.IsType<OkObjectResult>(selected.Result).Value);
        var row = Assert.Single(result.Items);
        Assert.Equal(a.Id, row.MoneyAccountId);
        Assert.Equal(10m, row.Income);
        Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        var employeeController = Controller(employee);
        Assert.IsType<ForbidResult>((await employeeController.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null, moneyAccountId: b.Id)).Result);
        Assert.IsType<ForbidResult>((await adminController.RunBalanceReconciliation(day, day, MoneyOperationSource.Cash, null, moneyAccountId: a.Id)).Result);
        Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
    }

    [Theory]
    [InlineData("rub", true)]
    [InlineData("foreign", true)]
    [InlineData("unknown", true)]
    [InlineData("unattributed", true)]
    [InlineData("duplicate-name", true)]
    public async Task LegacySavedReconciliation_RequiresModernContractForAnyRows(string scenario, bool blocked)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "saved@fixture.local",
            FullName = "Synthetic",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var first = new LegalEntity { ShortName = "First" };
        var second = new LegalEntity { ShortName = "Second" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(first, second);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { first.Id, second.Id }) });
        var account = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = first.Id, Currency = "RUB", IsActive = true };
        var another = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = second.Id, Currency = "RUB", IsActive = true };
        db.MoneyAccounts.AddRange(account, another);
        await db.SaveChangesAsync();
        var day = new DateTime(2026, 10, 20);
        db.MoneyBalanceReconciliationResults.Add(new MoneyBalanceReconciliationResult
        {
            Date = day,
            Source = MoneyOperationSource.Bank,
            AccountName = "Same",
            CompanyId = first.Id,
            MoneyAccountId = account.Id,
            Currency = scenario == "foreign" ? "USD" : scenario == "unknown" ? null : "RUB",
            ScopeKey = "first",
            Income = 10m,
            ClosingBalance = 10m
        });
        if (scenario == "duplicate-name")
            db.MoneyBalanceReconciliationResults.Add(new MoneyBalanceReconciliationResult
            {
                Date = day,
                Source = MoneyOperationSource.Bank,
                AccountName = "Same",
                CompanyId = second.Id,
                MoneyAccountId = another.Id,
                Currency = "RUB",
                ScopeKey = "second",
                Income = 20m,
                ClosingBalance = 20m
            });
        if (scenario == "unattributed")
            db.MoneyBalanceReconciliationResults.Add(new MoneyBalanceReconciliationResult
            {
                Date = day,
                Source = MoneyOperationSource.Bank,
                AccountName = "Legacy",
                Currency = "RUB",
                Income = 20m,
                ClosingBalance = 20m
            });
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };
        var old = await controller.GetBalanceReconciliationResults(day, day, MoneyOperationSource.Bank, null);
        if (blocked) Assert.IsType<ConflictObjectResult>(old.Result);
        else Assert.IsType<OkObjectResult>(old.Result);
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Contains("X-PayPlanner-Currency-Contract", controller.Response.Headers.Vary.ToString());
        Assert.IsType<OkObjectResult>((await controller.GetBalanceReconciliationResults(day.AddDays(1), day.AddDays(1), MoneyOperationSource.Bank, null)).Result);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "2";
        Assert.IsType<ConflictObjectResult>((await controller.GetBalanceReconciliationResults(day, day, MoneyOperationSource.Bank, null)).Result);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var current = await controller.GetBalanceReconciliationResults(day, day, MoneyOperationSource.Bank, null);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<MoneyBalanceReconciliationResultDto>>(Assert.IsType<OkObjectResult>(current.Result).Value));
    }

    [Theory]
    [InlineData("rub", false)]
    [InlineData("foreign", true)]
    [InlineData("unknown", true)]
    [InlineData("unattributed", true)]
    [InlineData("duplicate-name", true)]
    public async Task LegacyDailyBalances_RequireUnambiguousRubleAccounts(string scenario, bool blocked)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "daily@fixture.local",
            FullName = "Synthetic",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var first = new LegalEntity { ShortName = "First" };
        var second = new LegalEntity { ShortName = "Second" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(first, second);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { first.Id, second.Id }) });
        var account = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = first.Id, Currency = "RUB", IsActive = true };
        db.MoneyAccounts.Add(account);
        var day = new DateTime(2026, 10, 20);
        db.MoneyOperations.Add(new MoneyOperation
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Currency = scenario == "foreign" ? "USD" : scenario == "unknown" ? null : "RUB",
            Date = day,
            Amount = 10m
        });
        if (scenario == "duplicate-name")
            db.MoneyOperations.Add(new MoneyOperation
            {
                MoneyAccount = new MoneyAccount { Name = "Same", Source = MoneyOperationSource.Bank, CompanyId = second.Id, Currency = "RUB", IsActive = true },
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = day,
                Amount = 20m
            });
        if (scenario == "unattributed")
            db.MoneyOperations.Add(new MoneyOperation
            {
                AccountName = "Legacy",
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = day,
                Amount = 20m
            });
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };
        var old = await controller.GetDailyBalances(day, day, MoneyOperationSource.Bank, null);
        if (blocked) Assert.IsType<ConflictObjectResult>(old.Result);
        else Assert.IsType<OkObjectResult>(old.Result);
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Contains("X-PayPlanner-Currency-Contract", controller.Response.Headers.Vary.ToString());
        if (blocked)
        {
            controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "2";
            Assert.IsType<ConflictObjectResult>((await controller.GetDailyBalances(day, day, MoneyOperationSource.Bank, null)).Result);
            Assert.IsType<ConflictObjectResult>((await controller.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null)).Result);
            Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        }
        controller.Request.Headers.Remove("X-PayPlanner-Currency-Contract");
        Assert.IsType<ConflictObjectResult>((await controller.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null)).Result);
        Assert.Empty(await db.MoneyBalanceReconciliationResults.ToListAsync());
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var current = await controller.GetDailyBalances(day, day, MoneyOperationSource.Bank, null);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationDailyBalanceDto>>(Assert.IsType<OkObjectResult>(current.Result).Value));
        if (scenario == "rub")
        {
            Assert.IsType<OkObjectResult>((await controller.RunBalanceReconciliation(day, day, MoneyOperationSource.Bank, null)).Result);
            Assert.Single(await db.MoneyBalanceReconciliationResults.ToListAsync());
        }
    }

    [Theory]
    [InlineData("single", "single", "USD", 125)]
    [InlineData("mixed", "mixed", null, null)]
    [InlineData("unknown", "unknown", null, null)]
    [InlineData("mixed-opening", "mixed", null, null)]
    [InlineData("empty", "empty", null, 0)]
    public async Task Summary_OnlyReturnsComparableAmountsFromOneCurrency(string scenario, string expectedStatus, string? expectedCurrency, int? expectedIncome)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "admin", Description = "Synthetic" };
        var user = new User { Email = "summary@fixture.local", FullName = "Synthetic", PasswordHash = "hash", Role = role, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        var company = new LegalEntity { ShortName = "Synthetic company" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var account = new MoneyAccount { Name = "Synthetic", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = "USD", IsActive = true };
        db.MoneyAccounts.Add(account);
        MoneyOperation Movement(string? currency, DateTime date, decimal amount) => new()
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            Currency = currency,
            Date = date,
            Amount = amount,
        };
        if (scenario != "empty") db.MoneyOperations.Add(Movement(scenario == "unknown" ? null : "USD", new DateTime(2026, 10, 20), 125m));
        if (scenario == "mixed") db.MoneyOperations.Add(Movement("RUB", new DateTime(2026, 10, 21), 30m));
        if (scenario == "mixed-opening") db.MoneyOperations.Add(Movement("RUB", new DateTime(2026, 9, 20), 30m));
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "admin")], "Test"))
                }
            },
        };
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var response = await controller.GetPaged(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), null, null, null, ct: CancellationToken.None);
        var summary = Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(response.Result).Value).Summary;
        Assert.Equal(expectedStatus, summary.CurrencyStatus);
        Assert.Equal(expectedCurrency, summary.Currency);
        Assert.Equal(expectedIncome, summary.Income);
        if (expectedIncome is null)
        {
            Assert.Null(summary.StartBalance);
            Assert.Null(summary.Expense);
            Assert.Null(summary.EndBalance);
            Assert.All(summary.BySource, source => Assert.Null(source.EndBalance));
            var json = System.Text.Json.JsonSerializer.SerializeToElement(summary, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("Income").ValueKind);
        }
    }

    [Theory]
    [InlineData("rub", false)]
    [InlineData("foreign", true)]
    [InlineData("unknown", true)]
    [InlineData("foreign-plan-other-page", true)]
    [InlineData("foreign-opening", true)]
    public async Task LegacyJournal_CannotReadUnconfirmedCurrencyAsRubles(string scenario, bool blocked)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "legacy@fixture.local",
            FullName = "Synthetic",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var company = new LegalEntity { ShortName = "Synthetic company" };
        db.Users.Add(user);
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company.Id }) });
        var account = new MoneyAccount { Name = "Synthetic", Source = MoneyOperationSource.Bank, CompanyId = company.Id, Currency = "RUB", IsActive = true };
        db.MoneyAccounts.Add(account);
        for (var index = 0; index < 5; index++)
            db.MoneyOperations.Add(new MoneyOperation
            {
                MoneyAccount = account,
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Currency = "RUB",
                Date = new DateTime(2026, 10, 20),
                Amount = 10m
            });
        if (scenario != "rub")
            db.MoneyOperations.Add(new MoneyOperation
            {
                MoneyAccount = account,
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = scenario == "foreign-plan-other-page" ? MoneyOperationAccountingType.Plan : MoneyOperationAccountingType.Fact,
                Currency = scenario == "unknown" ? null : "USD",
                Date = scenario == "foreign-opening" ? new DateTime(2026, 9, 20)
                    : scenario == "foreign-plan-other-page" ? new DateTime(2026, 10, 2) : new DateTime(2026, 10, 21),
                Amount = 20m
            });
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };
        async Task<ActionResult<MoneyOperationsPageResponse>> Read() => await controller.GetPaged(new DateTime(2026, 10, 1),
            new DateTime(2026, 10, 31), null, null, null, page: 1, pageSize: 5, ct: CancellationToken.None);

        var legacy = await Read();
        if (blocked) Assert.IsType<ConflictObjectResult>(legacy.Result);
        else Assert.IsType<OkObjectResult>(legacy.Result);
        Assert.Contains("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Contains("X-PayPlanner-Currency-Contract", controller.Response.Headers.Vary.ToString());
        if (blocked)
        {
            controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "2";
            Assert.IsType<ConflictObjectResult>((await Read()).Result);
        }
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        Assert.IsType<OkObjectResult>((await Read()).Result);
    }

    [Fact]
    public async Task LegacyJournal_ForeignCompanyCurrencyDoesNotBlockOrLeak()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Name = "synthetic-employee", Description = "Synthetic" };
        var user = new User
        {
            Email = "legacy-employee@fixture.local",
            FullName = "Synthetic",
            PasswordHash = "hash",
            Role = role,
            IsEmployee = true,
            IsActive = true,
            IsApproved = true
        };
        var own = new LegalEntity { ShortName = "Своя" };
        var foreign = new LegalEntity { ShortName = "Другая" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(own, foreign);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = own.Id });
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { own.Id, foreign.Id }) });
        db.MoneyOperations.AddRange(
            new MoneyOperation
            {
                MoneyAccount = new MoneyAccount { Name = "Своя", Source = MoneyOperationSource.Bank, CompanyId = own.Id },
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Date = new DateTime(2026, 10, 20),
                Amount = 10m,
                Currency = "RUB"
            },
            new MoneyOperation
            {
                MoneyAccount = new MoneyAccount { Name = "Чужая", Source = MoneyOperationSource.Bank, CompanyId = foreign.Id },
                Source = MoneyOperationSource.Bank,
                Direction = MoneyOperationDirection.Income,
                AccountingType = MoneyOperationAccountingType.Fact,
                Date = new DateTime(2026, 10, 20),
                Amount = 20m,
                Currency = "USD"
            });
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };

        var result = await controller.GetPaged(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), null, null, null, ct: CancellationToken.None);

        var page = Assert.IsType<MoneyOperationsPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, page.Total);
        Assert.Equal(10m, page.Summary.Income);
        var accountsResult = await controller.GetAccountSummaries(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), MoneyOperationSource.Bank);
        var accounts = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationAccountSummaryDto>>(Assert.IsType<OkObjectResult>(accountsResult.Result).Value);
        var account = Assert.Single(accounts);
        Assert.Equal(own.Id, account.CompanyId);
        Assert.Equal(10m, account.Income);
        var dailyResult = await controller.GetDailyBalances(new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), MoneyOperationSource.Bank, null);
        var daily = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationDailyBalanceDto>>(Assert.IsType<OkObjectResult>(dailyResult.Result).Value));
        Assert.Equal(own.Id, daily.CompanyId);
        Assert.Equal(10m, daily.Income);
        var accountsByCompany = await db.MoneyAccounts.ToDictionaryAsync(item => item.CompanyId!.Value);
        db.MoneyBalanceReconciliationResults.AddRange(
            new MoneyBalanceReconciliationResult
            {
                Date = new DateTime(2026, 10, 20),
                Source = MoneyOperationSource.Bank,
                AccountName = "Своя",
                CompanyId = own.Id,
                MoneyAccountId = accountsByCompany[own.Id].Id,
                Currency = "RUB",
                ScopeKey = "own",
                Income = 10m
            },
            new MoneyBalanceReconciliationResult
            {
                Date = new DateTime(2026, 10, 20),
                Source = MoneyOperationSource.Bank,
                AccountName = "Чужая",
                CompanyId = foreign.Id,
                MoneyAccountId = accountsByCompany[foreign.Id].Id,
                Currency = "USD",
                ScopeKey = "foreign",
                Income = 20m
            });
        await db.SaveChangesAsync();
        var savedResult = await controller.GetBalanceReconciliationResults(new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), MoneyOperationSource.Bank, null);
        Assert.IsType<ConflictObjectResult>(savedResult.Result);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        savedResult = await controller.GetBalanceReconciliationResults(new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), MoneyOperationSource.Bank, null);
        var saved = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MoneyBalanceReconciliationResultDto>>(Assert.IsType<OkObjectResult>(savedResult.Result).Value));
        Assert.Equal(own.Id, saved.CompanyId);
        controller.Request.Headers.Remove("X-PayPlanner-Currency-Contract");
        Assert.IsType<ForbidResult>((await controller.RunBalanceReconciliation(new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), MoneyOperationSource.Bank, null)).Result);
    }

    [Fact]
    public async Task AccountSummaries_SeparateCompaniesAccountsCurrenciesAndUnattributedHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User
        {
            Email = "summary-identities@fixture.local",
            FullName = "Synthetic",
            PasswordHash = "hash",
            Role = new Role { Name = "admin", Description = "Synthetic" },
            IsActive = true,
            IsApproved = true
        };
        var company1 = new LegalEntity { ShortName = "Первая" };
        var company2 = new LegalEntity { ShortName = "Вторая" };
        db.Users.Add(user);
        db.LegalEntities.AddRange(company1, company2);
        await db.SaveChangesAsync();
        db.SystemSettings.Add(new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = System.Text.Json.JsonSerializer.Serialize(new[] { company1.Id, company2.Id }) });
        var first = new MoneyAccount { Name = "Одинаковый", Source = MoneyOperationSource.Bank, CompanyId = company1.Id, Currency = "RUB" };
        var second = new MoneyAccount { Name = "Одинаковый", Source = MoneyOperationSource.Bank, CompanyId = company2.Id, Currency = "RUB" };
        db.MoneyAccounts.AddRange(first, second);
        MoneyOperation Movement(MoneyAccount? account, string? currency, DateTime date, decimal amount) => new()
        {
            MoneyAccount = account,
            Source = MoneyOperationSource.Bank,
            Direction = MoneyOperationDirection.Income,
            AccountingType = MoneyOperationAccountingType.Fact,
            AccountName = account == first ? "Старое имя" : "Одинаковый",
            Currency = currency,
            Date = date,
            Amount = amount,
        };
        db.MoneyOperations.AddRange(
            Movement(first, "RUB", new DateTime(2026, 9, 20), 100m),
            Movement(first, "RUB", new DateTime(2026, 10, 20), 50m),
            Movement(first, "USD", new DateTime(2026, 10, 21), 25m),
            Movement(first, null, new DateTime(2026, 10, 22), 7m),
            Movement(second, "RUB", new DateTime(2026, 10, 20), 999m),
            Movement(null, "RUB", new DateTime(2026, 10, 23), 333m));
        await db.SaveChangesAsync();
        var controller = new MoneyOperationsController(db, NullLogger<MoneyOperationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                }
            },
        };
        var from = new DateTime(2026, 10, 1);
        var to = new DateTime(2026, 10, 31);
        Assert.IsType<OkObjectResult>((await controller.GetAccountSummaries(null, new DateTime(2026, 9, 30), MoneyOperationSource.Bank)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.GetAccountSummaries(from, new DateTime(2026, 10, 20), MoneyOperationSource.Bank)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.GetAccountSummaries(from, to, MoneyOperationSource.Bank)).Result);
        controller.Request.Headers["X-PayPlanner-Currency-Contract"] = "1";
        var response = await controller.GetAccountSummaries(from, to, MoneyOperationSource.Bank);
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationAccountSummaryDto>>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(5, summaries.Count);
        var rubles = Assert.Single(summaries, row => row.MoneyAccountId == first.Id && row.Currency == "RUB");
        Assert.Equal("Одинаковый", rubles.AccountName);
        Assert.Equal(100m, rubles.StartBalance);
        Assert.Equal(50m, rubles.Income);
        Assert.Equal(150m, rubles.EndBalance);
        Assert.Equal(25m, Assert.Single(summaries, row => row.MoneyAccountId == first.Id && row.Currency == "USD").EndBalance);
        Assert.Equal(999m, Assert.Single(summaries, row => row.MoneyAccountId == second.Id).EndBalance);
        Assert.Null(Assert.Single(summaries, row => row.MoneyAccountId == first.Id && row.Currency is null).EndBalance);
        var legacy = Assert.Single(summaries, row => row.MoneyAccountId is null);
        Assert.Equal("unattributed", legacy.CurrencyStatus);
        Assert.Null(legacy.EndBalance);
        var unbounded = await controller.GetAccountSummaries(null, to, MoneyOperationSource.Bank);
        var allRows = Assert.IsAssignableFrom<IReadOnlyList<MoneyOperationAccountSummaryDto>>(Assert.IsType<OkObjectResult>(unbounded.Result).Value);
        Assert.Equal(150m, Assert.Single(allRows, row => row.MoneyAccountId == first.Id && row.Currency == "RUB").Income);
    }
}

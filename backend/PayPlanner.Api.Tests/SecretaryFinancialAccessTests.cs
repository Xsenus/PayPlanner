using System.Security.Claims;
using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SecretaryFinancialAccessTests
{
    [Fact]
    public async Task Http_SeparatesInvoicePermissionFromPaymentsAndRolesAndAppliesRevocation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var accountPermission = new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = true, ViewScope = DataVisibilityScope.All };
        var role = new Role
        {
            Name = "Секретарь",
            Permissions =
        {
            accountPermission,
            new RolePermission { Section = RolePermissionCatalog.Payments, CanView = false },
            new RolePermission { Section = RolePermissionCatalog.Roles, CanView = false },
        }
        };
        var user = new User { Email = "http-secretary@example.test", FullName = "Секретарь", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddScoped<IFormRequirementsService, FormRequirementsService>();
                services.AddScoped<RoleManagementAccessService>();
                services.AddAuthentication("secretary-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("secretary-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var http = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/accounts")).StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/roles")).StatusCode);
        accountPermission.CanView = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/accounts")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_DeniesOtherFinancialListsEvenWhenInvoicesAreAllowed(bool allowInvoices)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role
        {
            Name = "Секретарь",
            Permissions =
            {
                new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = allowInvoices, ViewScope = DataVisibilityScope.All },
                new RolePermission { Section = RolePermissionCatalog.Payments, CanView = false },
                new RolePermission { Section = RolePermissionCatalog.Acts, CanView = false },
                new RolePermission { Section = RolePermissionCatalog.Contracts, CanView = false },
            },
        };
        var user = new User { Email = "financial-lists-secretary@example.test", FullName = "Секретарь", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddScoped<IFormRequirementsService, FormRequirementsService>();
                services.AddAuthentication("secretary-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("secretary-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString());
        Assert.Equal(allowInvoices ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await http.GetAsync("/api/accounts")).StatusCode);
        foreach (var path in new[] { "/api/acts", "/api/contracts", "/api/money-accounts", "/api/money-operations", "/api/money-operations/paged" })
        {
            var response = await http.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path}: expected 403, got {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Http_MoneyListsReflectGrantAndRevocationWithoutExpiringSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var permission = new RolePermission { Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All };
        var role = new Role { Name = "Бухгалтер", Permissions = { permission } };
        var user = new User { Email = "money-access@example.test", FullName = "Бухгалтер", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddAuthentication("secretary-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("secretary-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var http = server.CreateClient();
        string[] paths = ["/api/money-operations", "/api/money-operations/paged"];
        foreach (var path in paths) Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString());
        foreach (var path in paths) Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path)).StatusCode);
        permission.CanView = false;
        await db.SaveChangesAsync();
        foreach (var path in paths) Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(path)).StatusCode);
        permission.CanView = true;
        await db.SaveChangesAsync();
        foreach (var path in paths) Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("/api/money-operations/export.csv", true, 403)]
    [InlineData("/api/money-operations/export.xls", true, 403)]
    [InlineData("/api/money-operations/print", true, 403)]
    [InlineData("/api/money-operations/export.pdf", true, 403)]
    [InlineData("/api/money-operations/accounts", false, 200)]
    [InlineData("/api/money-operations/account-summaries", false, 200)]
    [InlineData("/api/money-operations/daily-balances", false, 200)]
    [InlineData("/api/money-operations/123456", false, 404)]
    public async Task Http_MoneyActionsRespectViewExportAndRevocation(string path, bool export, int statusWithView)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var permission = new RolePermission { Section = RolePermissionCatalog.Payments, CanView = false, CanExport = false, ViewScope = DataVisibilityScope.All };
        var user = new User { Email = "money-actions@example.test", FullName = "Сотрудник", PasswordHash = "x", Role = new Role { Name = "Сотрудник", Permissions = { permission } }, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddSingleton<PayPlanner.Api.Services.Invoices.IInvoiceDocumentPdfService, FakePdfRenderer>();
                services.AddAuthentication("secretary-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("secretary-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var http = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(path)).StatusCode);
        permission.CanView = true;
        await db.SaveChangesAsync();
        Assert.Equal(statusWithView, (int)(await http.GetAsync(path)).StatusCode);
        if (export)
        {
            permission.CanExport = true;
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path)).StatusCode);
        }
        permission.CanView = false;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(path)).StatusCode);
    }

    private sealed class FakePdfRenderer : PayPlanner.Api.Services.Invoices.IInvoiceDocumentPdfService
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken ct = default) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes("%PDF-test"));
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectControllers_KeepInvoiceAccessSeparateFromPaymentsAndRoleManagement(bool allowAccounts)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role
        {
            Name = "Секретарь",
            Permissions =
        {
            new RolePermission { Section = RolePermissionCatalog.Accounts, CanView = allowAccounts, ViewScope = DataVisibilityScope.All },
            new RolePermission { Section = RolePermissionCatalog.Payments, CanView = false },
            new RolePermission { Section = RolePermissionCatalog.Roles, CanView = false },
        }
        };
        var user = new User { Email = "secretary@example.test", FullName = "Секретарь", PasswordHash = "x", Role = role, IsActive = true, IsApproved = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "test"));
        ControllerContext context = new() { HttpContext = new DefaultHttpContext { User = principal } };
        var accounts = new AccountsController(db) { ControllerContext = context };
        var payments = new PaymentsController(db, null!) { ControllerContext = context };
        var roles = new RolesController(db, new RoleManagementAccessService(db)) { ControllerContext = context };

        var accountsResult = await accounts.Get(null, null, null, null, ct: default);
        if (allowAccounts) Assert.IsType<OkObjectResult>(accountsResult);
        else Assert.IsType<ForbidResult>(accountsResult);
        Assert.IsType<ForbidResult>(await payments.Get(null, null, null, null, ct: default));
        Assert.IsType<ForbidResult>(await roles.GetRoles(default));

        role.Permissions.Single(permission => permission.Section == RolePermissionCatalog.Accounts).CanView = false;
        await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await accounts.Get(null, null, null, null, ct: default));
    }
}

using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class MoneyOperationsControllerTests
{
    [Fact]
    public async Task ManagementSections_HttpExistingSessionLosesAccessWhenRolePermissionsAreRevoked()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "management-group-http@example.test", "Секретарь");
        var permissions = new[] { RolePermissionCatalog.Roles, RolePermissionCatalog.Dictionaries }
            .Select(section => new RolePermission { RoleId = user.RoleId, Section = section, CanView = true, ViewScope = DataVisibilityScope.All }).ToArray();
        db.RolePermissions.AddRange(permissions);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db); services.AddScoped<RoleManagementAccessService>();
                services.AddAuthentication("bank-import-fixture").AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(RolesController).Assembly);
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        var paths = new[] { "/api/roles", "/api/dictionaries/deal-types" };
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        foreach (var permission in permissions) permission.CanView = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task DocumentSections_HttpExistingSessionLosesAccessWhenRolePermissionsAreRevoked()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "documents-group-http@example.test", "Секретарь");
        var sections = new[] { RolePermissionCatalog.Accounts, RolePermissionCatalog.Acts, RolePermissionCatalog.Contracts };
        var permissions = sections.Select(section => new RolePermission
        {
            RoleId = user.RoleId,
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.All,
        }).ToArray();
        db.RolePermissions.AddRange(permissions);
        await db.SaveChangesAsync();
        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddSingleton<IEmailService, GroupFixtureEmailService>();
                services.AddSingleton<IFormRequirementsService, GroupFixtureFormRequirements>();
                services.AddAuthentication("bank-import-fixture").AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(InvoicesController).Assembly);
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        var paths = new[] { "/api/invoices", "/api/acts", "/api/contracts" };
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        foreach (var permission in permissions) permission.CanView = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    private sealed class GroupFixtureEmailService : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException("Email must not be sent");
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => throw new InvalidOperationException("Email must not be sent");
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => throw new InvalidOperationException("Email must not be sent");
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => throw new InvalidOperationException("Email must not be sent");
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => throw new InvalidOperationException("Email must not be sent");
    }

    private sealed class GroupFixtureFormRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }

    [Fact]
    public async Task MoneyJournal_HttpExistingSessionLosesAccessWhenRolePermissionIsRevoked()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "group-revoke-http@example.test", "Секретарь");
        var permission = new RolePermission { RoleId = user.RoleId, Section = RolePermissionCatalog.Payments, CanView = true, ViewScope = DataVisibilityScope.All };
        db.RolePermissions.Add(permission);
        await db.SaveChangesAsync();

        using var server = new TestServer(new WebHostBuilder().UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db);
                services.AddAuthentication("bank-import-fixture").AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly);
            })
            .Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        using var before = await client.GetAsync("/api/money-operations/accounts");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        permission.CanView = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var after = await client.GetAsync("/api/money-operations/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("missing-company", HttpStatusCode.BadRequest)]
    [InlineData("missing-account", HttpStatusCode.BadRequest)]
    [InlineData("foreign-company", HttpStatusCode.Forbidden)]
    [InlineData("not-own-company", HttpStatusCode.BadRequest)]
    [InlineData("unknown-account", HttpStatusCode.NotFound)]
    public async Task BankImportAccess_HttpUploadRejectsUntrustedContextBeforeCreatingMoney(
        string scenario,
        HttpStatusCode expectedStatus)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "bank-import-http@example.test", "Импорт своей компании");
        user.IsEmployee = true;
        db.RolePermissions.Add(new RolePermission
        {
            RoleId = user.RoleId,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            ViewScope = DataVisibilityScope.All,
        });
        var ownCompany = new LegalEntity { ShortName = "Своя синтетическая компания" };
        var foreignCompany = new LegalEntity { ShortName = "Чужая синтетическая компания" };
        var counterparty = new LegalEntity { ShortName = "Сторонний синтетический контрагент" };
        db.LegalEntities.AddRange(ownCompany, foreignCompany, counterparty);
        var account = new MoneyAccount
        {
            Name = "Синтетический банк",
            Source = MoneyOperationSource.Bank,
            Currency = "RUB",
        };
        db.MoneyAccounts.Add(account);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(
            new SystemSetting
            {
                Key = "legalEntities.mainCompanyIds",
                Value = JsonSerializer.Serialize(new[] { ownCompany.Id, foreignCompany.Id }),
            },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        db.UserCompanyAccesses.Add(new UserCompanyAccess { UserId = user.Id, LegalEntityId = ownCompany.Id });
        await db.SaveChangesAsync();
        var before = await CaptureBankImportStateAsync(db);

        using var server = new TestServer(new WebHostBuilder()
            .UseEnvironment("Testing")
            .UseSetting("BackgroundServices:Enabled", "false")
            .UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddAuthentication("bank-import-fixture")
                    .AddScheme<AuthenticationSchemeOptions, BankImportFixtureAuthentication>("bank-import-fixture", _ => { });
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(MoneyOperationsController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            }));
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", user.Id.ToString(CultureInfo.InvariantCulture));
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(
            "Дата;Сумма;Контрагент;Назначение;Номер\n" +
            "01.10.2026;-123;Синтетический поставщик;Синтетический расход;101\n")), "file", "synthetic-statement.csv");
        if (scenario != "missing-company")
        {
            var companyId = scenario switch
            {
                "foreign-company" => foreignCompany.Id,
                "not-own-company" => counterparty.Id,
                _ => ownCompany.Id,
            };
            form.Add(new StringContent(companyId.ToString(CultureInfo.InvariantCulture)), "companyId");
        }
        if (scenario != "missing-account")
        {
            var accountId = scenario == "unknown-account" ? account.Id + 10_000 : account.Id;
            form.Add(new StringContent(accountId.ToString(CultureInfo.InvariantCulture)), "moneyAccountId");
        }

        using var response = await client.PostAsync("/api/money-operations/bank-statement/import", form);

        Assert.Equal(before, await CaptureBankImportStateAsync(db));
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(foreignCompany.ShortName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(counterparty.ShortName, body, StringComparison.Ordinal);
    }

    private sealed class BankImportFixtureAuthentication(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!int.TryParse(Request.Headers["X-Fixture-User"], out var userId) || userId <= 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            // A stale admin claim must not bypass the role and company grants in DB.
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Role, "admin"),
            }, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAdminAccessTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    private static readonly string[] Actions = ["list", "accounts", "statements", "statement", "settings", "operations", "logs"];
    public static IEnumerable<object[]> Refusals()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in postgres ? new[] { "unapproved", "stale-role" } : new[] { "unapproved", "stale-role", "inactive", "unknown", "no-id", "empty", "anonymous", "no-admin-claim" })
                foreach (var action in Actions) yield return [postgres, mode, action];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public Task CurrentApprovalAndDatabaseRoleAreRequiredBeforeEveryBankAction(bool postgres, string mode, string action)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db); var user = await db.Users.SingleAsync();
            if (mode == "unapproved") user.IsApproved = false;
            if (mode == "inactive") user.IsActive = false;
            if (mode == "stale-role") (await db.Roles.SingleAsync()).Name = "ordinary";
            if (mode == "empty") db.Users.Remove(user);
            await db.SaveChangesAsync();
            var sync = SyncProbe.Create(); var probe = (SyncProbe)(object)sync;
            using var server = Server(db, sync); using var client = server.CreateClient();
            SetIdentity(client, mode == "no-id" ? null : mode == "unknown" ? int.MaxValue : fixture.User, mode != "no-admin-claim", mode != "anonymous");
            var before = await SnapshotAsync(db);
            using var response = await SendAsync(client, fixture, action);
            Assert.Equal(mode == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("Confidential fixture", await response.Content.ReadAsStringAsync());
            Assert.Empty(probe.Calls); Assert.Equal(before, await SnapshotAsync(db));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ApprovedAdministratorCanReadAndSyncAndRevocationAppliesToSameSession(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var fixture = await SeedAsync(db); var sync = SyncProbe.Create(); var probe = (SyncProbe)(object)sync;
            using var server = Server(db, sync); using var client = server.CreateClient(); SetIdentity(client, fixture.User);
            foreach (var action in Actions)
            {
                using var allowed = await SendAsync(client, fixture, action); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
                if (action == "list") Assert.Contains("Confidential fixture", await allowed.Content.ReadAsStringAsync());
            }
            Assert.Equal(4, probe.Calls.Count); var before = await SnapshotAsync(db);
            (await db.Users.SingleAsync()).IsApproved = false; await db.SaveChangesAsync();
            using var refused = await SendAsync(client, fixture, "accounts"); Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(4, probe.Calls.Count); Assert.Equal(before, await SnapshotAsync(db));
            (await db.Users.SingleAsync()).IsApproved = true; await db.SaveChangesAsync();
            using var restored = await SendAsync(client, fixture, "list"); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        });

    private sealed record Fixture(int User, int Connection, int Account);
    private static async Task<Fixture> SeedAsync(PaymentContext db)
    {
        var user = new User { Email = "bank-admin@test.local", FullName = "Fixture administrator", PasswordHash = "unused", IsActive = true, IsApproved = true, Role = new Role { Name = "admin" } };
        var company = new LegalEntity { ShortName = "Confidential fixture company", ProfileStatus = CompanyProfileStatuses.Active };
        db.Users.Add(user); db.LegalEntities.Add(company); await db.SaveChangesAsync();
        var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Sber, Status = BankConnectionStatuses.Active, LastError = "Confidential fixture connection", AccessTokenProtected = "synthetic-not-a-token" };
        db.BankConnections.Add(connection); await db.SaveChangesAsync();
        var account = new BankConnectionAccount { BankConnectionId = connection.Id, ExternalAccountId = "synthetic", AccountNumber = "40702810200000000001", Currency = "RUB" };
        db.BankConnectionAccounts.Add(account); await db.SaveChangesAsync();
        db.BankStatementOperations.Add(new() { BankConnectionAccountId = account.Id, ExternalOperationId = "history", Amount = 123m, OperationDate = DateTime.UtcNow.Date, Purpose = "Confidential fixture operation" });
        db.BankSyncLogs.Add(new() { BankConnectionId = connection.Id, Kind = "accounts", Result = "ok", Error = "Confidential fixture log" });
        await db.SaveChangesAsync(); return new(user.Id, connection.Id, account.Id);
    }

    private static async Task<string> SnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            connections = await db.BankConnections.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.LegalEntityId, x.Status, x.AccessTokenProtected, x.LastAccountsSyncAt }).ToArrayAsync(),
            accounts = await db.BankConnectionAccounts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            operations = await db.BankStatementOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.ExternalOperationId, x.Amount, x.OperationDate, x.BankConnectionAccountId }).ToArrayAsync(),
            logs = await db.BankSyncLogs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            money = await db.MoneyOperations.CountAsync(),
        });

    private static void SetIdentity(HttpClient client, int? userId, bool admin = true, bool authenticated = true)
    {
        if (!authenticated) return;
        client.DefaultRequestHeaders.Add("X-Fixture-Identity", userId?.ToString() ?? "no-id");
        if (admin) client.DefaultRequestHeaders.Add("X-Fixture-Admin", "true");
    }
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Fixture fixture, string action)
    {
        var period = new { from = "2026-10-01", to = "2026-10-02", requestId = Guid.NewGuid() };
        return action switch
        {
            "list" => client.GetAsync("/api/bank-connections"),
            "accounts" => client.PostAsync($"/api/bank-connections/{fixture.Connection}/sync-accounts", null),
            "statements" => client.PostAsJsonAsync($"/api/bank-connections/{fixture.Connection}/sync-statements", period),
            "statement" => client.PostAsJsonAsync($"/api/bank-connections/accounts/{fixture.Account}/sync-statement", period),
            "settings" => client.PatchAsJsonAsync($"/api/bank-connections/accounts/{fixture.Account}", new { isHidden = true }),
            "operations" => client.GetAsync($"/api/bank-connections/accounts/{fixture.Account}/operations"),
            "logs" => client.GetAsync($"/api/bank-connections/{fixture.Connection}/logs"),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
    private static TestServer Server(PaymentContext db, IBankSyncService sync)
        => new(new WebHostBuilder().UseEnvironment("Testing").UseSetting("BackgroundServices:Enabled", "false").UseSetting("Email:Enabled", "false")
            .ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton(db); services.AddSingleton(sync);
                services.AddAuthentication("bank-admin-fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("bank-admin-fixture", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Admin", policy => policy.RequireRole("admin")));
                services.AddControllers().AddApplicationPart(typeof(BankConnectionsController).Assembly).AddControllersAsServices();
            }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); }));

    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres)
        {
            await using var database = await template.CreateDatabaseAsync();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db);
        }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Fixture-Identity")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim>();
            if (int.TryParse(Request.Headers["X-Fixture-Identity"], out var id)) claims.Add(new(ClaimTypes.NameIdentifier, id.ToString()));
            if (Request.Headers.ContainsKey("X-Fixture-Admin")) claims.Add(new(ClaimTypes.Role, "admin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
    public class SyncProbe : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public static IBankSyncService Create() => DispatchProxy.Create<IBankSyncService, SyncProbe>();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            return method.Name switch
            {
                nameof(IBankSyncService.SyncAccountsAsync) => Task.FromResult(new BankAccountsSyncResult(0, 0, 0, null)),
                nameof(IBankSyncService.SyncStatementAsync) => Task.FromResult(new BankStatementSyncResult(0, 0, null, null)),
                nameof(IBankSyncService.SyncAllStatementsForPeriodAsync) => Task.FromResult<IReadOnlyDictionary<int, BankStatementSyncResult>>(new Dictionary<int, BankStatementSyncResult>()),
                nameof(IBankSyncService.UpdateAccountSettingsAsync) => Task.FromResult(new AccountSettingsResult(true, null)),
                _ => throw new InvalidOperationException("Unexpected bank method"),
            };
        }
    }
}

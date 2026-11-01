using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Core = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAccountBindingHttpTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HttpBindReadReplayAndRevokeNeverCallBankOrChangeFinancialHistory(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient(); Identity(client, f.Actor);
            var initial = await ReportAsync(client, f); Assert.True(initial.CanBind); Assert.Empty(initial.ActiveBindings);
            using var created = await BindAsync(client, f, initial.Version); Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.True(created.Headers.CacheControl!.NoStore);
            var receipt = (await created.Content.ReadFromJsonAsync<CompanyBankBindingReceipt>())!; Assert.False(receipt.IsReplay);
            var current = await ReportAsync(client, f); Assert.NotEqual(initial.Version, current.Version);
            var binding = Assert.Single(current.ActiveBindings); Assert.Equal(receipt.BindingId, binding.Id);
            Assert.Equal("local-id", binding.ProfileAccountId); Assert.Equal(f.Bank, binding.BankAccountId);
            Assert.Equal(Core.Number, binding.AccountNumber); Assert.Equal(Core.Bik, binding.Bik); Assert.Equal("RUB", binding.Currency);
            var payload = JsonSerializer.Serialize(current); Assert.DoesNotContain("synthetic-not-a-token", payload); Assert.DoesNotContain("Spoofed", payload);
            using var replay = await BindAsync(client, f, initial.Version); Assert.True((await replay.Content.ReadFromJsonAsync<CompanyBankBindingReceipt>())!.IsReplay);
            using var stale = await RevokeAsync(client, f, receipt.BindingId, initial.Version); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var revoked = await RevokeAsync(client, f, receipt.BindingId, current.Version); Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            using var repeated = await RevokeAsync(client, f, receipt.BindingId, current.Version); Assert.True((await repeated.Content.ReadFromJsonAsync<CompanyBankBindingReceipt>())!.IsReplay);
            Assert.Empty((await ReportAsync(client, f)).ActiveBindings);
            Assert.Equal(history, await Core.HistoryAsync(db)); Assert.Empty(((CompanyBankAdminAccessTests.SyncProbe)(object)sync).Calls);
        });

    [Theory]
    [InlineData(false, "archived")]
    [InlineData(false, "former-own")]
    [InlineData(false, "both")]
    [InlineData(true, "archived")]
    [InlineData(true, "former-own")]
    [InlineData(true, "both")]
    public Task CleanupReadsOnlyStoredConfirmationsAndRequiresFreshVersion(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db); var service = new CompanyBankAccountBindingService(db);
            var initial = await Core.VersionAsync(db, f);
            var receipt = await service.BindAsync(Core.Principal(f.Actor), f.Connection, f.Bank, "local-id", initial, default);
            var beforeChange = await Core.VersionAsync(db, f);
            var company = await db.LegalEntities.SingleAsync();
            if (mode != "former-own") company.ProfileStatus = CompanyProfileStatuses.Archived;
            if (mode != "archived") (await db.SystemSettings.SingleAsync(x => x.Key == "legalEntities.mainCompanyIds")).Value = "[]";
            // Legacy malformed/oversized candidate lists must not prevent safe cleanup.
            company.BankAccountsJson = "[" + string.Join(',', Enumerable.Repeat("{}", 200)) + "]";
            await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient(); Identity(client, f.Actor);
            var report = await ReportAsync(client, f); Assert.False(report.CanBind); Assert.Empty(report.Accounts); Assert.Empty(report.Groups);
            Assert.Equal(receipt.BindingId, Assert.Single(report.ActiveBindings).Id); Assert.NotEqual(beforeChange, report.Version);
            using var stale = await RevokeAsync(client, f, receipt.BindingId, beforeChange); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var newBind = await BindAsync(client, f, report.Version); Assert.Equal(HttpStatusCode.NotFound, newBind.StatusCode);
            using var revoked = await RevokeAsync(client, f, receipt.BindingId, report.Version); Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            using var replay = await RevokeAsync(client, f, receipt.BindingId, report.Version); Assert.True((await replay.Content.ReadFromJsonAsync<CompanyBankBindingReceipt>())!.IsReplay);
            Assert.Empty((await ReportAsync(client, f)).ActiveBindings);
            Assert.Equal(history, await Core.HistoryAsync(db)); Assert.Empty(((CompanyBankAdminAccessTests.SyncProbe)(object)sync).Calls);
        });

    public static IEnumerable<object[]> Refusals()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in new[] { "anonymous", "no-admin", "unapproved", "role", "inactive", "session", "unknown-user", "unknown-connection" })
                yield return [postgres, mode];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public Task BothCommandsRequireCurrentAdministrativeAuthority(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db); var initial = await Core.VersionAsync(db, f);
            var receipt = await new CompanyBankAccountBindingService(db).BindAsync(Core.Principal(f.Actor), f.Connection, f.Bank, "local-id", initial, default);
            var current = await Core.VersionAsync(db, f);
            var user = await db.Users.SingleAsync();
            if (mode == "unapproved") user.IsApproved = false;
            if (mode == "inactive") user.IsActive = false;
            if (mode == "role") (await db.Roles.SingleAsync()).Name = "ordinary";
            if (mode == "session") user.AuthTokenVersion++;
            await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient();
            if (mode != "anonymous") Identity(client, mode == "unknown-user" ? int.MaxValue : f.Actor, mode != "no-admin");
            var target = mode == "unknown-connection" ? f with { Connection = int.MaxValue } : f;
            var expected = mode == "anonymous" ? HttpStatusCode.Unauthorized : mode == "unknown-connection" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
            using var bind = await BindAsync(client, target, initial); Assert.Equal(expected, bind.StatusCode);
            using var revoke = await RevokeAsync(client, target, receipt.BindingId, current); Assert.Equal(expected, revoke.StatusCode);
            Assert.Null((await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).RevokedAtUtc);
            Assert.Equal(history, await Core.HistoryAsync(db)); Assert.Empty(((CompanyBankAdminAccessTests.SyncProbe)(object)sync).Calls);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ForeignTypedBindingAndJsonCannotGrantCleanupOrCrossCompanyWrites(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await Core.SeedAsync(db); var version = await Core.VersionAsync(db, f);
            var bound = await new CompanyBankAccountBindingService(db).BindAsync(Core.Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default);
            var foreign = new LegalEntity { ShortName = "Unrelated", BankAccountsJson = "[{\"id\":\"local-id\",\"bankConnectionAccountId\":" + f.Bank + "}]" };
            db.LegalEntities.Add(foreign); await db.SaveChangesAsync();
            var connection = new BankConnection { LegalEntityId = foreign.Id, Provider = BankProviderKeys.Sber, Status = BankConnectionStatuses.Active };
            db.BankConnections.Add(connection); await db.SaveChangesAsync(); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient(); Identity(client, f.Actor);
            var target = f with { Connection = connection.Id };
            using var read = await client.GetAsync($"/api/bank-connections/{connection.Id}/profile-account-matches"); Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            using var revoke = await RevokeAsync(client, target, bound.BindingId, version); Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
            using var bind = await BindAsync(client, target, version); Assert.Equal(HttpStatusCode.NotFound, bind.StatusCode);
            Assert.Equal(bound.BindingId, Assert.Single((await ReportAsync(client, f)).ActiveBindings).Id);
            Assert.Null((await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).RevokedAtUtc); Assert.Equal(history, await Core.HistoryAsync(db));
        });

    [Theory]
    [InlineData("missing")]
    [InlineData("long-id")]
    [InlineData("long-version")]
    [InlineData("bank-id")]
    [InlineData("invalid-selection")]
    [InlineData("invalid-version")]
    [InlineData("wrong-bank")]
    public Task HttpValidationAndErrorsDoNotWrite(string mode)
        => RunAsync(false, async db =>
        {
            var f = await Core.SeedAsync(db); var version = await Core.VersionAsync(db, f); var history = await Core.HistoryAsync(db);
            var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
            using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient(); Identity(client, f.Actor);
            var request = new Dictionary<string, object>();
            if (mode != "missing")
            {
                request["bankAccountId"] = mode == "bank-id" ? 0 : mode == "wrong-bank" ? int.MaxValue : f.Bank;
                request["profileAccountId"] = mode == "long-id" ? new string('x', 16001) : mode == "invalid-selection" ? "absent" : "local-id";
                request["expectedVersion"] = mode == "long-version" ? new string('x', 87) : mode == "invalid-version" ? "invalid" : version;
            }
            using var response = await client.PostAsJsonAsync($"/api/bank-connections/{f.Connection}/profile-account-bindings", request);
            Assert.Equal(mode == "invalid-version" ? HttpStatusCode.Conflict : mode == "wrong-bank" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync()); Assert.Equal(history, await Core.HistoryAsync(db));
        });

    [Fact]
    public async Task NativeLockedCompanyReturnsConflictWithoutPartialWrite()
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var f = await Core.SeedAsync(db); var version = await Core.VersionAsync(db, f); var history = await Core.HistoryAsync(db);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", f.Company); await command.ExecuteScalarAsync();
        var sync = CompanyBankAdminAccessTests.SyncProbe.Create();
        using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient(); Identity(client, f.Actor);
        using var response = await BindAsync(client, f, version); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await db.CompanyBankAccountBindings.AsNoTracking().ToArrayAsync()); Assert.Equal(history, await Core.HistoryAsync(db));
    }

    private static void Identity(HttpClient client, int id, bool admin = true)
    {
        client.DefaultRequestHeaders.Add("X-Fixture-Identity", id.ToString());
        if (admin) client.DefaultRequestHeaders.Add("X-Fixture-Admin", "true");
    }
    private static Task<HttpResponseMessage> BindAsync(HttpClient client, Core.Fixture f, string version)
        => client.PostAsJsonAsync($"/api/bank-connections/{f.Connection}/profile-account-bindings", new { bankAccountId = f.Bank, profileAccountId = "local-id", expectedVersion = version });
    private static Task<HttpResponseMessage> RevokeAsync(HttpClient client, Core.Fixture f, int binding, string version)
        => client.PostAsJsonAsync($"/api/bank-connections/{f.Connection}/profile-account-bindings/{binding}/revoke", new { expectedVersion = version });
    private static async Task<CompanyBankAccountMatchReport> ReportAsync(HttpClient client, Core.Fixture f)
    {
        using var response = await client.GetAsync($"/api/bank-connections/{f.Connection}/profile-account-matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<CompanyBankAccountMatchReport>())!;
    }
    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
}

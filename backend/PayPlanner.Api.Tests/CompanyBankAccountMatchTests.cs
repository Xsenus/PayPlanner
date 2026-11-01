using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAccountMatchTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    private const string Number = "40702810200000000001";
    private const string Bik = "044525225";
    private sealed record Fixture(CompanyBankAdminAccessTests.Fixture Base, int Company, int Money);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in postgres ? new[] { "exact", "profile-currency", "peers", "money-owner", "archive", "closed", "duplicate-link" }
                         : new[] { "exact", "spacing", "number", "bik", "missing-bik", "profile-currency", "bank-currency", "different-currency", "RUR", "archive", "duplicate-local", "missing-id", "duplicate-id", "bad-json", "bad-flag", "peers", "closed", "hidden", "disconnected", "money-owner", "money-currency", "money-source", "money-inactive", "manual-money", "manual-other-source", "duplicate-link" })
                yield return [postgres, mode];
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public Task CandidatesUseKnownIdentityAndNeverMergeOrWrite(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var company = await db.LegalEntities.SingleAsync(); var bank = await db.BankConnectionAccounts.SingleAsync();
            var money = await db.MoneyAccounts.SingleAsync();
            switch (mode)
            {
                case "spacing": bank.AccountNumber = " 40702 810200000000001 "; bank.Bik = "04 4525225"; bank.Currency = " rub "; break;
                case "number": bank.AccountNumber = "40702810200000000002"; break;
                case "bik": bank.Bik = "044525104"; break;
                case "missing-bik": bank.Bik = null; break;
                case "profile-currency": company.BankAccountsJson = Profile(currency: null); break;
                case "bank-currency": bank.Currency = ""; break;
                case "different-currency": bank.Currency = "USD"; break;
                case "RUR": bank.Currency = "RUR"; break;
                case "archive": company.BankAccountsJson = Profile(archived: true); break;
                case "duplicate-local": company.BankAccountsJson = "[" + Profile()[1..^1] + "," + Profile(id: "second")[1..^1] + "]"; break;
                case "missing-id": company.BankAccountsJson = Profile(id: null); break;
                case "duplicate-id": company.BankAccountsJson = "[" + Profile()[1..^1] + "," + Profile()[1..^1] + "]"; break;
                case "bad-json": company.BankAccountsJson = "{broken"; break;
                case "bad-flag": company.BankAccountsJson = Profile().Replace("\"isArchived\":false", "\"isArchived\":\"false\""); break;
                case "peers":
                    var otherConnection = new BankConnection { LegalEntityId = f.Company, Provider = BankProviderKeys.Tochka, Status = BankConnectionStatuses.Disconnected };
                    db.BankConnections.Add(otherConnection); await db.SaveChangesAsync();
                    db.BankConnectionAccounts.Add(new() { BankConnectionId = otherConnection.Id, ExternalAccountId = "historical", AccountNumber = Number, Bik = Bik, Currency = "RUB", Status = BankAccountStatuses.Closed });
                    break;
                case "closed": bank.Status = BankAccountStatuses.Closed; break;
                case "hidden": bank.IsHidden = true; break;
                case "disconnected": (await db.BankConnections.SingleAsync()).Status = BankConnectionStatuses.Disconnected; break;
                case "money-owner":
                    var foreign = new LegalEntity { ShortName = "Foreign secret" }; db.LegalEntities.Add(foreign); await db.SaveChangesAsync(); money.CompanyId = foreign.Id; break;
                case "money-currency": money.Currency = "USD"; break;
                case "money-source": money.Source = MoneyOperationSource.Cash; break;
                case "money-inactive": money.IsActive = false; break;
                case "manual-money": money.BankConnectionAccountId = null; break;
                case "manual-other-source": money.BankConnectionAccountId = null; money.Source = MoneyOperationSource.Cash; break;
                case "duplicate-link":
                    var duplicate = Money(f.Company, f.Base.Account); db.MoneyAccounts.Add(duplicate);
                    await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.Entry(duplicate).State = EntityState.Detached;
                    break;
            }
            await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
            var report = Assert.IsType<CompanyBankAccountMatchReport>(await new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default));
            var row = Assert.Single(report.Accounts);
            Assert.Equal(f.Company, report.CompanyId); Assert.Matches("^bank-profile-match-v2:[A-F0-9]{64}$", report.Version);
            var group = Group(report, row);
            var status = mode switch { "bank-currency" or "missing-bik" => "InvalidBankIdentity", "duplicate-local" => "Ambiguous", "number" or "bik" or "profile-currency" or "different-currency" or "RUR" or "archive" or "missing-id" or "duplicate-id" or "bad-json" or "bad-flag" => "Missing", _ => "UniqueCandidate" };
            Assert.Equal(status, group?.ProfileMatchStatus ?? "InvalidBankIdentity");
            if (status == "UniqueCandidate") Assert.Equal("local", Assert.Single(group!.ProfileCandidates).Id);
            if (mode == "duplicate-local") Assert.Equal(2, group!.ProfileCandidates.Count);
            var issue = mode switch
            {
                "bank-currency" or "missing-bik" => "IncompleteBankIdentity",
                "archive" => "ArchivedProfileCandidates",
                "peers" => "MultipleBankSources",
                "closed" => "BankAccountUnavailable",
                "hidden" => "BankAccountHidden",
                "disconnected" => "ConnectionUnavailable",
                "money-owner" or "money-currency" or "money-source" => "LinkedMoneyIdentityMismatch",
                "money-inactive" => "LinkedMoneyAccountInactive",
                "manual-money" => "ManualMoneyCandidatesRequireReview",
                _ => null,
            };
            if (issue is not null) Assert.Contains(issue, row.Issues);
            if (mode is "money-owner" or "money-currency" or "money-source" or "bank-currency" or "missing-bik" or "number" or "bik" or "different-currency" or "RUR" or "manual-money" or "manual-other-source") Assert.Empty(row.LinkedMoneyAccountIds);
            else Assert.Contains(f.Money, row.LinkedMoneyAccountIds);
            if (mode == "manual-money") Assert.Equal(f.Money, Assert.Single(group!.ManualMoneyAccountCandidateIds));
            if (mode == "manual-other-source") Assert.Empty(group!.ManualMoneyAccountCandidateIds);
            if (mode == "peers") Assert.Equal(2, group!.BankAccountIds.Count);
            if (mode == "profile-currency") Assert.Contains("IncompleteProfileIdentity", report.ProfileIssues);
            if (mode == "missing-id") Assert.Contains("MissingStableProfileId", report.ProfileIssues);
            if (mode == "duplicate-id") Assert.Contains("DuplicateProfileId", report.ProfileIssues);
            if (mode is "bad-json" or "bad-flag") Assert.Contains("InvalidProfileAccounts", report.ProfileIssues);
            Assert.Equal(before, await SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
            Assert.DoesNotContain("synthetic-not-a-token", JsonSerializer.Serialize(report)); Assert.DoesNotContain("Foreign secret", JsonSerializer.Serialize(report));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CompanyIsolationVersionAndDirtyTrackerArePreserved(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var service = new CompanyBankAccountMatchService(db);
            var first = (await service.ReadAsync(f.Base.Connection, default))!;
            var foreign = new LegalEntity { ShortName = "Other company", BankAccountsJson = Profile() }; db.LegalEntities.Add(foreign); await db.SaveChangesAsync();
            var connection = new BankConnection { LegalEntityId = foreign.Id, Provider = BankProviderKeys.Tochka }; db.BankConnections.Add(connection); await db.SaveChangesAsync();
            db.BankConnectionAccounts.Add(new() { BankConnectionId = connection.Id, ExternalAccountId = "other", AccountNumber = Number, Bik = Bik, Currency = "RUB" }); await db.SaveChangesAsync();
            Assert.Null(await service.ReadAsync(connection.Id, default));
            Assert.Equal(first.Version, (await service.ReadAsync(f.Base.Connection, default))!.Version);
            (await db.MoneyAccounts.SingleAsync()).Name = "Renamed wallet"; await db.SaveChangesAsync();
            Assert.Equal(first.Version, (await service.ReadAsync(f.Base.Connection, default))!.Version);
            var company = await db.LegalEntities.SingleAsync(x => x.Id == f.Company); company.BankAccountsJson = Profile(id: "changed");
            Assert.Equal(first.Version, (await service.ReadAsync(f.Base.Connection, default))!.Version); Assert.True(db.ChangeTracker.HasChanges());
            Assert.Contains("local", (await db.LegalEntities.AsNoTracking().SingleAsync(x => x.Id == f.Company)).BankAccountsJson);
            await db.SaveChangesAsync(); var after = (await service.ReadAsync(f.Base.Connection, default))!;
            Assert.NotEqual(first.Version, after.Version); Assert.Equal("changed", Assert.Single(Group(after, Assert.Single(after.Accounts))!.ProfileCandidates).Id);
            await using var external = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => service.ReadAsync(f.Base.Connection, default));
            Assert.Same(external, db.Database.CurrentTransaction); await external.RollbackAsync();
        });

    [Theory]
    [InlineData(false, "bank")]
    [InlineData(false, "money")]
    [InlineData(false, "profile")]
    [InlineData(true, "bank")]
    public Task LimitsNeverReturnAPartialUniqueMatch(bool postgres, string kind)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db);
            if (kind == "bank") db.BankConnectionAccounts.AddRange(Enumerable.Range(0, CompanyBankAccountMatchService.MaximumBankAccounts).Select(i => new BankConnectionAccount { BankConnectionId = f.Base.Connection, ExternalAccountId = "extra-" + i, AccountNumber = Number, Bik = Bik, Currency = "RUB" }));
            if (kind == "money") db.MoneyAccounts.AddRange(Enumerable.Range(0, CompanyBankAccountMatchService.MaximumMoneyAccounts).Select(_ => Money(f.Company, null)));
            if (kind == "profile") (await db.LegalEntities.SingleAsync()).BankAccountsJson = "[" + string.Join(',', Enumerable.Range(0, CompanyBankAccountMatchService.MaximumProfileAccounts + 1).Select(i => "{\"id\":\"" + i + "\"}")) + "]";
            await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
            await Assert.ThrowsAsync<CompanyBankAccountMatchLimitException>(() => new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default));
            Assert.Equal(before, await SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
        });

    [Fact]
    public async Task OversizedLegacyProfileIsNotParsedOrReturned()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db);
        (await db.LegalEntities.SingleAsync()).BankAccountsJson = new string('x', CompanyBankAccountList.MaxJsonLength + 1); await db.SaveChangesAsync();
        var report = (await new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default))!;
        Assert.Contains("ProfileAccountsTooLarge", report.ProfileIssues); Assert.Empty(Group(report, Assert.Single(report.Accounts))!.ProfileCandidates);
    }

    [Fact]
    public async Task ExactLimitsRemainReadableWithoutQuadraticCandidatePayload()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db);
        (await db.LegalEntities.SingleAsync()).BankAccountsJson = JsonSerializer.Serialize(Enumerable.Range(0, CompanyBankAccountMatchService.MaximumProfileAccounts)
            .Select(i => new { id = i.ToString(), accountNumber = Number, bik = Bik, currency = "RUB" }));
        db.BankConnectionAccounts.AddRange(Enumerable.Range(0, CompanyBankAccountMatchService.MaximumBankAccounts - 1)
            .Select(i => new BankConnectionAccount { BankConnectionId = f.Base.Connection, ExternalAccountId = "extra-" + i, AccountNumber = Number, Bik = Bik, Currency = "RUB" }));
        db.MoneyAccounts.AddRange(Enumerable.Range(0, CompanyBankAccountMatchService.MaximumMoneyAccounts - 1).Select(_ => Money(f.Company, null)));
        await db.SaveChangesAsync(); var report = (await new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default))!;
        Assert.Equal(500, report.Accounts.Count); var group = Assert.Single(report.Groups);
        Assert.Equal(128, group.ProfileCandidates.Count); Assert.Equal(500, group.BankAccountIds.Count); Assert.Equal(999, group.ManualMoneyAccountCandidateIds.Count);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(report).Length < 200_000, "Shared candidates must not be repeated for every bank row.");
    }

    [Theory]
    [InlineData("approved", HttpStatusCode.OK)]
    [InlineData("unapproved", HttpStatusCode.Forbidden)]
    [InlineData("role", HttpStatusCode.Forbidden)]
    [InlineData("no-id", HttpStatusCode.Forbidden)]
    [InlineData("unknown", HttpStatusCode.NotFound)]
    [InlineData("not-own", HttpStatusCode.NotFound)]
    [InlineData("archived", HttpStatusCode.OK)]
    public async Task HttpUsesCurrentBankAdministrationAndOwnCompanyOnly(string mode, HttpStatusCode expected)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db);
        if (mode == "unapproved") (await db.Users.SingleAsync()).IsApproved = false;
        if (mode == "role") (await db.Roles.SingleAsync()).Name = "ordinary";
        if (mode == "not-own") db.SystemSettings.RemoveRange(await db.SystemSettings.Where(x => x.Key == "legalEntities.mainCompanyIds").ToArrayAsync());
        if (mode == "archived") (await db.LegalEntities.SingleAsync()).ProfileStatus = CompanyProfileStatuses.Archived;
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var sync = CompanyBankAdminAccessTests.SyncProbe.Create(); var probe = (CompanyBankAdminAccessTests.SyncProbe)(object)sync;
        using var server = CompanyBankAdminAccessTests.Server(db, sync); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-Identity", mode == "no-id" ? "no-id" : f.Base.User.ToString()); client.DefaultRequestHeaders.Add("X-Fixture-Admin", "true");
        using var response = await client.GetAsync($"/api/bank-connections/{(mode == "unknown" ? int.MaxValue : f.Base.Connection)}/profile-account-matches");
        Assert.Equal(expected, response.StatusCode); Assert.Empty(probe.Calls); Assert.Equal(before, await SnapshotAsync(db));
        if (expected == HttpStatusCode.OK)
        {
            var report = (await response.Content.ReadFromJsonAsync<CompanyBankAccountMatchReport>())!;
            Assert.Equal(mode != "archived", report.CanBind);
            if (report.CanBind) Assert.Equal("local", Assert.Single(Group(report, Assert.Single(report.Accounts))!.ProfileCandidates).Id);
            else { Assert.Empty(report.Accounts); Assert.Contains("CompanyUnavailable", report.ProfileIssues); }
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
    }

    [Fact]
    public async Task NativeReportUsesOneSnapshotAcrossProfileBankAndMoney()
    {
        await using var database = await template.CreateDatabaseAsync(); var gate = new BeforeBankRead();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate).Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var f = await SeedAsync(db); gate.Enabled = true; var read = new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using (var transaction = await other.Database.BeginTransactionAsync())
            {
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"LegalEntities\" SET \"BankAccountsJson\" = {Profile(currency: "USD")} WHERE \"Id\" = {f.Company}");
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"BankConnectionAccounts\" SET \"Currency\" = {"USD"} WHERE \"Id\" = {f.Base.Account}");
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MoneyAccounts\" SET \"Currency\" = {"USD"} WHERE \"Id\" = {f.Money}");
                await transaction.CommitAsync();
            }
            gate.Release.TrySetResult(); var report = (await read.WaitAsync(TimeSpan.FromSeconds(30)))!;
            var row = Assert.Single(report.Accounts); Assert.Equal("UniqueCandidate", Group(report, row)!.ProfileMatchStatus); Assert.Equal(f.Money, Assert.Single(row.LinkedMoneyAccountIds));
            var current = (await new CompanyBankAccountMatchService(db).ReadAsync(f.Base.Connection, default))!;
            Assert.NotEqual(report.Version, current.Version); Assert.Equal("UniqueCandidate", Group(current, Assert.Single(current.Accounts))!.ProfileMatchStatus);
        }
        finally { gate.Release.TrySetResult(); await read.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    private static CompanyBankAccountMatchGroup? Group(CompanyBankAccountMatchReport report, CompanyBankAccountMatch row)
        => row.GroupId is null ? null : Assert.Single(report.Groups, group => group.Id == row.GroupId);
    private static string Profile(string? id = "local", string? currency = "RUB", bool archived = false)
        => JsonSerializer.Serialize(new[] { new { id, name = "Local account", accountNumber = Number, bik = Bik, currency, isArchived = archived, isPrimary = !archived } });
    private static MoneyAccount Money(int company, int? bank)
        => new() { CompanyId = company, BankConnectionAccountId = bank, BankAccountNumber = Number, BankBik = Bik, Currency = "RUB", Source = MoneyOperationSource.Bank, Name = "Wallet " + Guid.NewGuid().ToString("N"), IsActive = true };
    private static async Task<Fixture> SeedAsync(PaymentContext db)
    {
        var f = await CompanyBankAdminAccessTests.SeedAsync(db); var company = await db.LegalEntities.SingleAsync(); company.BankAccountsJson = Profile();
        (await db.BankConnectionAccounts.SingleAsync()).Bik = Bik;
        db.SystemSettings.Add(new() { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" }); var money = Money(company.Id, f.Account); db.MoneyAccounts.Add(money);
        await db.SaveChangesAsync(); return new(f, company.Id, money.Id);
    }
    private static async Task<string> SnapshotAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            profiles = await db.LegalEntities.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankAccountsJson }).ToArrayAsync(),
            banks = await db.BankConnectionAccounts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.AccountNumber, x.Bik, x.Currency, x.Status, x.IsHidden }).ToArrayAsync(),
            money = await db.MoneyAccounts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.BankConnectionAccountId, x.Currency, x.Name }).ToArrayAsync(),
            history = await db.BankStatementOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Amount, x.BankConnectionAccountId }).ToArrayAsync(),
            operations = await db.MoneyOperations.CountAsync(),
        });
    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
    private sealed class BeforeBankRead : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("FROM \"BankConnectionAccounts\"", StringComparison.Ordinal)) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}

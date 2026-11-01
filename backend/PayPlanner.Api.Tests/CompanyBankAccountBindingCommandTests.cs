using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAccountBindingCommandTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    internal sealed record Fixture(int Company, int Connection, int Bank, int Actor, int Money);
    internal const string Number = "40702810200000000001";
    internal const string Bik = "044525225";
    internal static string Profile(string id = "local-id", string number = Number)
        => JsonSerializer.Serialize(new[] { new { id, accountNumber = number, bik = Bik, currency = "RUB", name = "Profile account", isPrimary = true, isArchived = false } });
    internal static ClaimsPrincipal Principal(int id, bool authenticated = true)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, "admin"), new Claim(ClaimTypes.Name, "Spoofed claim name")], authenticated ? "fixture" : null));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BindReplayRevokeAndRebindPreserveFinancialHistoryAndRejectOldAbaVersion(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var before = await HistoryAsync(db); var principal = Principal(f.Actor);
            var initial = await VersionAsync(db, f);
            var service = new CompanyBankAccountBindingService(db);
            var bound = await service.BindAsync(principal, f.Connection, f.Bank, "local-id", initial, default);
            Assert.False(bound.IsReplay); Assert.Equal("Bound", bound.State);
            var stored = await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync();
            Assert.Equal("Fixture administrator", stored.CreatedByUserName); Assert.Equal(f.Actor, stored.CreatedByUserId);
            Assert.Equal(initial, stored.ConfirmedMatchVersion); Assert.Equal(BankProviderKeys.Sber, stored.Provider);
            Assert.Equal("synthetic", stored.ExternalAccountId); Assert.Equal(Number, stored.AccountNumber);
            var replay = await service.BindAsync(principal, f.Connection, f.Bank, "local-id", initial, default);
            Assert.True(replay.IsReplay); Assert.Equal(bound.BindingId, replay.BindingId);
            var confirmedVersion = await VersionAsync(db, f); Assert.NotEqual(initial, confirmedVersion);
            var revoked = await service.RevokeAsync(principal, f.Connection, bound.BindingId, confirmedVersion, default);
            Assert.False(revoked.IsReplay); Assert.Equal("Revoked", revoked.State);
            Assert.True((await service.RevokeAsync(principal, f.Connection, bound.BindingId, confirmedVersion, default)).IsReplay);
            var afterRevoke = await VersionAsync(db, f); Assert.NotEqual(initial, afterRevoke);
            Assert.Equal("Changed", (await Assert.ThrowsAsync<CompanyBankBindingException>(() => service.BindAsync(principal, f.Connection, f.Bank, "local-id", initial, default))).Code);
            var rebound = await service.BindAsync(principal, f.Connection, f.Bank, "local-id", afterRevoke, default);
            Assert.NotEqual(bound.BindingId, rebound.BindingId);
            Assert.True((await service.RevokeAsync(principal, f.Connection, bound.BindingId, confirmedVersion, default)).IsReplay);
            var rows = await db.CompanyBankAccountBindings.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(2, rows.Length); Assert.NotNull(rows[0].RevokedAtUtc); Assert.Null(rows[1].RevokedAtUtc);
            Assert.Equal(before, await HistoryAsync(db));
        });

    public static IEnumerable<object[]> ChangedInputs()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in postgres ? new[] { "profile", "external", "hidden", "money-owner", "not-own", "provider" }
                         : new[] { "number", "bik", "currency", "closed", "hidden", "connection", "provider", "external", "profile", "profile-archive", "missing-id", "duplicate-id", "money-owner", "money-source", "money-currency", "money-inactive", "company-archive", "not-own", "wrong-bank", "invalid-version", "old-v1" })
                yield return [postgres, mode];
    }

    [Theory]
    [MemberData(nameof(ChangedInputs))]
    public Task ChangedOrUnavailableInputsNeverCreateAConfirmation(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var version = await VersionAsync(db, f);
            var bank = await db.BankConnectionAccounts.SingleAsync(); var company = await db.LegalEntities.SingleAsync(); var money = await db.MoneyAccounts.SingleAsync();
            switch (mode)
            {
                case "number": bank.AccountNumber = "40702810200000000002"; break;
                case "bik": bank.Bik = "044525104"; break;
                case "currency": bank.Currency = "USD"; break;
                case "closed": bank.Status = BankAccountStatuses.Closed; break;
                case "hidden": bank.IsHidden = true; break;
                case "connection": (await db.BankConnections.SingleAsync()).Status = BankConnectionStatuses.Disconnected; break;
                case "provider": (await db.BankConnections.SingleAsync()).Provider = BankProviderKeys.Tochka; break;
                case "external": bank.ExternalAccountId = "replaced-source"; break;
                case "profile": company.BankAccountsJson = Profile(number: "40702810200000000002"); break;
                case "profile-archive": company.BankAccountsJson = Profile().Replace("\"isArchived\":false", "\"isArchived\":true"); break;
                case "missing-id": company.BankAccountsJson = "[{\"accountNumber\":\"" + Number + "\",\"bik\":\"" + Bik + "\",\"currency\":\"RUB\"}]"; break;
                case "duplicate-id": company.BankAccountsJson = Profile()[..^1] + "," + Profile()[1..]; break;
                case "money-owner": money.CompanyId = null; break;
                case "money-source": money.Source = MoneyOperationSource.Cash; break;
                case "money-currency": money.Currency = "USD"; break;
                case "money-inactive": money.IsActive = false; break;
                case "company-archive": company.ProfileStatus = CompanyProfileStatuses.Archived; break;
                case "not-own": (await db.SystemSettings.SingleAsync(x => x.Key == "legalEntities.mainCompanyIds")).Value = "[]"; break;
                case "invalid-version": version = "bad"; break;
                case "old-v1": version = version.Replace("-v2:", "-v1:"); break;
            }
            await db.SaveChangesAsync(); var before = await HistoryAsync(db);
            var error = await Assert.ThrowsAsync<CompanyBankBindingException>(() => new CompanyBankAccountBindingService(db)
                .BindAsync(Principal(f.Actor), f.Connection, mode == "wrong-bank" ? f.Bank + 1000 : f.Bank, "local-id", version, default));
            Assert.Contains(error.Code, new[] { "Changed", "NotFound", "InvalidSelection" });
            Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync()); Assert.Equal(before, await HistoryAsync(db));
        });

    [Theory]
    [InlineData(false, "role")]
    [InlineData(false, "approval")]
    [InlineData(false, "inactive")]
    [InlineData(false, "unknown")]
    [InlineData(false, "unauthenticated")]
    [InlineData(false, "session")]
    [InlineData(false, "bad-session")]
    [InlineData(true, "role")]
    [InlineData(true, "approval")]
    [InlineData(true, "session")]
    public Task BindAndRevokeRequireCurrentApprovedAdministratorEvenForReplay(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var principal = Principal(f.Actor); var version = await VersionAsync(db, f); var service = new CompanyBankAccountBindingService(db);
            var first = await service.BindAsync(principal, f.Connection, f.Bank, "local-id", version, default);
            var currentVersion = await VersionAsync(db, f); var actor = await db.Users.SingleAsync();
            switch (mode)
            {
                case "role": (await db.Roles.SingleAsync()).Name = "employee"; break;
                case "approval": actor.IsApproved = false; break;
                case "inactive": actor.IsActive = false; break;
                case "unknown": principal = Principal(int.MaxValue); break;
                case "unauthenticated": principal = Principal(f.Actor, false); break;
                case "session": actor.AuthTokenVersion++; break;
                case "bad-session": ((ClaimsIdentity)principal.Identity!).AddClaim(new(AuthSessionClaims.TokenVersionClaimType, "-1")); break;
            }
            await db.SaveChangesAsync();
            Assert.Equal("Forbidden", (await Assert.ThrowsAsync<CompanyBankBindingException>(() => service.BindAsync(principal, f.Connection, f.Bank, "local-id", version, default))).Code);
            Assert.Equal("Forbidden", (await Assert.ThrowsAsync<CompanyBankBindingException>(() => service.RevokeAsync(principal, f.Connection, first.BindingId, currentVersion, default))).Code);
            Assert.Null((await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).RevokedAtUtc);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitChoiceResolvesDuplicatesWithoutTrustingJsonOrMergingManualWallets(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db);
            var company = await db.LegalEntities.SingleAsync();
            company.BankAccountsJson = Profile()[..^1] + "," + Profile("chosen")[1..];
            company.BankAccountsJson = company.BankAccountsJson.Replace("\"id\":\"chosen\"", "\"id\":\"chosen\",\"bankConnectionAccountId\":999,\"provider\":\"forged\"");
            var bank = new BankConnectionAccount { BankConnectionId = f.Connection, ExternalAccountId = "explicit-peer", AccountNumber = Number, Bik = Bik, Currency = "RUB" };
            var manual = new MoneyAccount { Name = "Historical manual", Source = MoneyOperationSource.Bank, CompanyId = f.Company, BankAccountNumber = Number, BankBik = Bik, Currency = "RUB" };
            db.BankConnectionAccounts.Add(bank); db.MoneyAccounts.Add(manual); await db.SaveChangesAsync(); var before = await HistoryAsync(db);
            var bound = await new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, bank.Id, "chosen", await VersionAsync(db, f), default);
            var row = await db.CompanyBankAccountBindings.AsNoTracking().SingleAsync(); Assert.Equal(bound.BindingId, row.Id);
            Assert.Equal("chosen", row.ProfileAccountId); Assert.Equal(bank.Id, row.BankConnectionAccountId); Assert.Equal("explicit-peer", row.ExternalAccountId);
            Assert.Equal(BankProviderKeys.Sber, row.Provider); Assert.Equal(before, await HistoryAsync(db));
            Assert.Null((await db.MoneyAccounts.AsNoTracking().SingleAsync(x => x.Id == manual.Id)).BankConnectionAccountId);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RevokeWorksAfterBankClosureOrIdentityDriftWithoutChangingTheSource(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var service = new CompanyBankAccountBindingService(db);
            var bound = await service.BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", await VersionAsync(db, f), default);
            var bank = await db.BankConnectionAccounts.SingleAsync(); bank.Status = BankAccountStatuses.Closed; bank.AccountNumber = "replaced";
            await db.SaveChangesAsync(); var before = await HistoryAsync(db);
            Assert.Equal("Revoked", (await service.RevokeAsync(Principal(f.Actor), f.Connection, bound.BindingId, await VersionAsync(db, f), default)).State);
            Assert.Equal(before, await HistoryAsync(db));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DirtyTrackerAndExternalTransactionAreRejectedWithoutConsumingCallerWork(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var version = await VersionAsync(db, f); var company = await db.LegalEntities.SingleAsync(); company.Phone = "unsaved caller";
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default));
            Assert.Equal("unsaved caller", company.Phone); Assert.Equal(EntityState.Modified, db.Entry(company).State);
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default));
            Assert.Same(transaction, db.Database.CurrentTransaction); Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
            await transaction.RollbackAsync();
        });

    [Fact]
    public async Task NativeAdmissionHoldsActorCompanyBankAndMoneyUntilCommit()
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeInsert();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(pause).Options;
        await using var db = new PostgreSqlPaymentContext(options); var f = await SeedAsync(db); var version = await VersionAsync(db, f);
        pause.Enabled = true; var pending = new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var concurrent = new NpgsqlConnection(database.ConnectionString); await concurrent.OpenAsync();
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", concurrent)) await timeout.ExecuteNonQueryAsync();
            foreach (var sql in new[] { $"UPDATE \"Users\" SET \"IsApproved\"=false WHERE \"Id\"={f.Actor}", $"UPDATE \"LegalEntities\" SET \"ShortName\"='Changed' WHERE \"Id\"={f.Company}", $"UPDATE \"BankConnectionAccounts\" SET \"IsHidden\"=true WHERE \"Id\"={f.Bank}", $"UPDATE \"MoneyAccounts\" SET \"Currency\"='USD' WHERE \"Id\"={f.Money}" })
            {
                await using var command = new NpgsqlCommand(sql, concurrent);
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
            await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => new CompanyBankAccountBindingService(other).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default));
        }
        finally { pause.Release.TrySetResult(); }
        var result = await pending; Assert.False(result.IsReplay); Assert.Single(await db.CompanyBankAccountBindings.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task NativeOlderProfileSnapshotCannotOverwriteAConfirmationCommittedAfterItsRead()
    {
        await using var database = await template.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options;
        await using var commandDb = new PostgreSqlPaymentContext(options); var f = await SeedAsync(commandDb);
        var version = await VersionAsync(commandDb, f);
        await using var importDb = new PostgreSqlPaymentContext(options);
        await using var import = await importDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var company = await importDb.LegalEntities.SingleAsync(); company.BankAccountsJson = Profile("stale-import-id"); company.Phone = "Must not leak";
        var bound = await new CompanyBankAccountBindingService(commandDb).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default);
        var error = await Record.ExceptionAsync(async () =>
        {
            await CompanyBankAccountBindingProfileGuard.ValidateTrackedAsync(importDb, default);
            await importDb.SaveChangesAsync(); await import.CommitAsync();
        });
        Assert.NotNull(error); PostgresException? postgres = null;
        for (Exception? e = error; e is not null; e = e.InnerException) if (e is PostgresException pg) postgres = pg;
        Assert.NotNull(postgres); Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres.SqlState);
        await import.RollbackAsync(); commandDb.ChangeTracker.Clear();
        var saved = await commandDb.LegalEntities.AsNoTracking().SingleAsync();
        Assert.Contains("local-id", saved.BankAccountsJson!); Assert.Null(saved.Phone);
        Assert.Equal(bound.BindingId, (await commandDb.CompanyBankAccountBindings.AsNoTracking().SingleAsync()).Id);
    }

    [Fact]
    public async Task NativeForeignBankIdNeverLocksAnotherCompanyMoney()
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        var f = await SeedAsync(db);
        var company = new LegalEntity { ShortName = "Foreign company" }; db.LegalEntities.Add(company); await db.SaveChangesAsync();
        var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Sber }; db.BankConnections.Add(connection); await db.SaveChangesAsync();
        var bank = new BankConnectionAccount { BankConnectionId = connection.Id, ExternalAccountId = "foreign-bank", AccountNumber = "40702810200000000002", Bik = Bik, Currency = "RUB" };
        db.BankConnectionAccounts.Add(bank); await db.SaveChangesAsync();
        var wallet = new MoneyAccount { Name = "Foreign wallet", CompanyId = company.Id, BankConnectionAccountId = bank.Id, Source = MoneyOperationSource.Bank, Currency = "RUB" };
        db.MoneyAccounts.Add(wallet); await db.SaveChangesAsync();
        var before = await HistoryAsync(db); var version = await VersionAsync(db, f);
        await using var holder = new NpgsqlConnection(database.ConnectionString); await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT \"Id\" FROM \"MoneyAccounts\" WHERE \"Id\"=@id FOR UPDATE", holder, transaction))
        {
            command.Parameters.AddWithValue("id", wallet.Id);
            Assert.Equal(wallet.Id, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        var error = await Assert.ThrowsAsync<CompanyBankBindingException>(() => new CompanyBankAccountBindingService(db)
            .BindAsync(Principal(f.Actor), f.Connection, bank.Id, "local-id", version, default));
        Assert.Equal("NotFound", error.Code); await transaction.RollbackAsync();
        Assert.Empty(await db.CompanyBankAccountBindings.AsNoTracking().ToArrayAsync()); Assert.Equal(before, await HistoryAsync(db));
    }

    [Fact]
    public async Task NativeCancellationBeforeInsertRollsBackTheWholeCommandAndReleasesLocks()
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeInsert();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(pause).Options);
        var f = await SeedAsync(db); var version = await VersionAsync(db, f); var before = await HistoryAsync(db);
        using var cancellation = new CancellationTokenSource(); pause.Enabled = true;
        var pending = new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, cancellation.Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            cancellation.Cancel(); pause.Release.TrySetResult();
            await Record.ExceptionAsync(() => pending);
        }
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(await db.CompanyBankAccountBindings.AsNoTracking().ToArrayAsync());
        Assert.Equal(before, await HistoryAsync(db)); Assert.Equal(version, await VersionAsync(db, f));
        Assert.False((await new CompanyBankAccountBindingService(db).BindAsync(Principal(f.Actor), f.Connection, f.Bank, "local-id", version, default)).IsReplay);
    }

    internal static async Task<Fixture> SeedAsync(PaymentContext db)
    {
        var basic = await CompanyBankAdminAccessTests.SeedAsync(db); var company = await db.LegalEntities.SingleAsync(); company.BankAccountsJson = Profile();
        (await db.BankConnectionAccounts.SingleAsync()).Bik = Bik;
        var money = new MoneyAccount { Name = "Explicit bank wallet", CompanyId = company.Id, BankConnectionAccountId = basic.Account, Source = MoneyOperationSource.Bank, BankAccountNumber = Number, BankBik = Bik, Currency = "RUB" };
        db.MoneyAccounts.Add(money); db.SystemSettings.Add(new() { Key = "legalEntities.mainCompanyIds", Value = $"[{company.Id}]" });
        await db.SaveChangesAsync();
        db.MoneyOperations.Add(new() { MoneyAccountId = money.Id, Source = MoneyOperationSource.Bank, Amount = 123.45m });
        await db.SaveChangesAsync(); return new(company.Id, basic.Connection, basic.Account, basic.User, money.Id);
    }
    internal static async Task<string> VersionAsync(PaymentContext db, Fixture f)
        => (await new CompanyBankAccountMatchService(db).ReadAsync(f.Connection, default))!.Version;
    internal static async Task<string> HistoryAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            companies = await db.LegalEntities.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankAccountsJson, x.Phone }).ToArrayAsync(),
            banks = await db.BankConnectionAccounts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            wallets = await db.MoneyAccounts.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            money = await db.MoneyOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.MoneyAccountId, x.Amount }).ToArrayAsync(),
            history = await db.BankStatementOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankConnectionAccountId, x.Amount }).ToArrayAsync(),
            payments = await db.Payments.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Account, x.Amount, x.PaidAmount }).ToArrayAsync()
        });
    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres) { await using var database = await template.CreateDatabaseAsync(); await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await test(db); }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
    private sealed class BeforeInsert : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO \"CompanyBankAccountBindings\"", StringComparison.Ordinal))
            { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}

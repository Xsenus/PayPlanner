using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

// Storage guarantees only. No public binding command or authorization is qualified here.
public sealed class CompanyBankAccountBindingStorageTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    internal sealed record Fixture(int Company, int Bank, int User);
    internal static readonly DateTime Created = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RevocationRetainsHistoryAndReleasesBothUniqueKeys(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db);
            var first = Binding(f);
            db.CompanyBankAccountBindings.Add(first); await db.SaveChangesAsync();
            var firstId = first.Id;
            Revoke(first, f.User); await db.SaveChangesAsync();
            var next = Binding(f); next.CreatedAtUtc = Created.AddMinutes(2);
            db.CompanyBankAccountBindings.Add(next); await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var rows = await db.CompanyBankAccountBindings.OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(2, rows.Length); Assert.Equal(firstId, rows[0].Id);
            Assert.Equal(Created.AddMinutes(1), rows[0].RevokedAtUtc);
            Assert.Equal("confirmed-v1", rows[0].ConfirmedMatchVersion);
            Assert.Equal("revoke-v1", rows[0].RevocationMatchVersion);
            Assert.Equal("Administrator", rows[0].CreatedByUserName);
            Assert.Null(rows[1].RevokedAtUtc); Assert.NotEqual(firstId, rows[1].Id);
            Assert.Equal(rows[0].AccountNumber, rows[1].AccountNumber);
        });

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task DatabaseRefusesCompetingActiveProfileOrBank(bool postgres, bool sameProfile)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var first = Binding(f);
            db.CompanyBankAccountBindings.Add(first); await db.SaveChangesAsync();
            var competing = Binding(f);
            if (sameProfile) competing.BankConnectionAccountId = await AddBankAsync(db, f.Bank);
            else { competing.ProfileAccountId = "other-profile"; competing.ProfileAccountKey = CompanyBankAccountBindingIdentity.ProfileKey(competing.ProfileAccountId); }
            db.CompanyBankAccountBindings.Add(competing);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(first.Id, (await db.CompanyBankAccountBindings.SingleAsync()).Id);
        });

    public static IEnumerable<object[]> InvalidRevocations()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var mode in new[] { "version-only", "user-only", "name-only", "time-only", "before-created" })
                yield return [postgres, mode];
    }

    [Theory]
    [MemberData(nameof(InvalidRevocations))]
    public Task DatabaseRefusesIncompleteOrBackdatedRevocation(bool postgres, string mode)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var row = Binding(f);
            switch (mode)
            {
                case "version-only": row.RevocationMatchVersion = "revoked"; break;
                case "user-only": row.RevokedByUserId = f.User; break;
                case "name-only": row.RevokedByUserName = "Administrator"; break;
                case "time-only": row.RevokedAtUtc = Created.AddMinutes(1); break;
                case "before-created": Revoke(row, f.User); row.RevokedAtUtc = Created.AddTicks(-10); break;
            }
            db.CompanyBankAccountBindings.Add(row);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear(); Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
        });

    [Theory]
    [InlineData(false, "company")]
    [InlineData(false, "bank")]
    [InlineData(true, "company")]
    [InlineData(true, "bank")]
    public Task HistoricalBindingPreventsPrincipalDeletion(bool postgres, string principal)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var row = Binding(f); Revoke(row, f.User);
            db.CompanyBankAccountBindings.Add(row); await db.SaveChangesAsync();
            // Other incoming references could also restrict the original company. Use a
            // separate company with only this binding as a dependent to isolate this FK.
            if (principal == "company")
            {
                var company = new LegalEntity { ShortName = "Storage FK target" };
                db.LegalEntities.Add(company); await db.SaveChangesAsync();
                row.LegalEntityId = company.Id; await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                await AssertForeignKeyFailureAsync(postgres, () => db.LegalEntities.Where(x => x.Id == company.Id).ExecuteDeleteAsync());
                Assert.True(await db.LegalEntities.AnyAsync(x => x.Id == company.Id));
            }
            else
            {
                db.ChangeTracker.Clear();
                await AssertForeignKeyFailureAsync(postgres, () => db.BankConnectionAccounts.Where(x => x.Id == f.Bank).ExecuteDeleteAsync());
                Assert.True(await db.BankConnectionAccounts.AnyAsync(x => x.Id == f.Bank));
            }
            Assert.Equal(row.Id, (await db.CompanyBankAccountBindings.SingleAsync()).Id);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeletingActorPreservesBothNamesAndRevokedHistory(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var row = Binding(f); Revoke(row, f.User);
            db.CompanyBankAccountBindings.Add(row); await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(1, await db.Users.Where(x => x.Id == f.User).ExecuteDeleteAsync());
            var saved = await db.CompanyBankAccountBindings.SingleAsync();
            Assert.Null(saved.CreatedByUserId); Assert.Null(saved.RevokedByUserId);
            Assert.Equal("Administrator", saved.CreatedByUserName); Assert.Equal("Administrator", saved.RevokedByUserName);
            Assert.Equal(Created.AddMinutes(1), saved.RevokedAtUtc);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LongStableIdIsPreservedAndOnlyBoundedKeyIsIndexed(bool postgres)
        => RunAsync(postgres, async db =>
        {
            var f = await SeedAsync(db); var row = Binding(f);
            row.ProfileAccountId = new string('я', CompanyBankAccountList.MaxJsonLength);
            row.ProfileAccountKey = CompanyBankAccountBindingIdentity.ProfileKey(row.ProfileAccountId);
            db.CompanyBankAccountBindings.Add(row); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var saved = await db.CompanyBankAccountBindings.SingleAsync();
            Assert.Equal(row.ProfileAccountId, saved.ProfileAccountId); Assert.Equal(64, saved.ProfileAccountKey.Length);
            var indexes = db.Model.FindEntityType(typeof(CompanyBankAccountBinding))!.GetIndexes();
            Assert.DoesNotContain(indexes.SelectMany(x => x.Properties), x => x.Name == nameof(CompanyBankAccountBinding.ProfileAccountId));
            Assert.Contains(indexes, x => x.IsUnique && x.GetDatabaseName() == "IX_CompanyBankBinding_ActiveProfile");
        });

    [Fact]
    public void StableKeysAreOrdinalAndRejectUnusableIds()
    {
        var key = CompanyBankAccountBindingIdentity.ProfileKey("legacy-ID");
        Assert.Equal(key, CompanyBankAccountBindingIdentity.ProfileKey("legacy-ID"));
        Assert.NotEqual(key, CompanyBankAccountBindingIdentity.ProfileKey("legacy-id"));
        Assert.NotEqual(key, CompanyBankAccountBindingIdentity.ProfileKey(" legacy-ID"));
        foreach (var id in new[] { null, "", " ", new string('x', CompanyBankAccountList.MaxJsonLength + 1) })
            Assert.ThrowsAny<ArgumentException>(() => CompanyBankAccountBindingIdentity.ProfileKey(id!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostgreSqlConcurrentInsertCannotStealAnActiveKey(bool sameProfile)
    {
        await using var database = await template.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options;
        await using var first = new PostgreSqlPaymentContext(options);
        var f = await SeedAsync(first); var otherBank = await AddBankAsync(first, f.Bank);
        await using var transaction = await first.Database.BeginTransactionAsync();
        first.CompanyBankAccountBindings.Add(Binding(f)); await first.SaveChangesAsync();
        await using var second = new PostgreSqlPaymentContext(options);
        // SET is session-scoped. Keep this nonpooled connection open through both
        // competing saves, otherwise EF closes it after ExecuteSqlRawAsync.
        await second.Database.OpenConnectionAsync();
        await second.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms'");
        var competitor = Binding(f);
        if (sameProfile) competitor.BankConnectionAccountId = otherBank;
        else { competitor.ProfileAccountId = "concurrent-other"; competitor.ProfileAccountKey = CompanyBankAccountBindingIdentity.ProfileKey(competitor.ProfileAccountId); }
        second.CompanyBankAccountBindings.Add(competitor);
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveChangesAsync());
        var update = Assert.IsType<DbUpdateException>(locked.InnerException);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(update.InnerException).SqlState);
        await transaction.CommitAsync();
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        second.ChangeTracker.Clear(); Assert.Single(await second.CompanyBankAccountBindings.ToArrayAsync());
    }

    internal static async Task<Fixture> SeedAsync(PaymentContext db)
    {
        var user = new User { Email = "binding@fixture.local", FullName = "Administrator", PasswordHash = "unused", Role = new Role { Name = "Binding storage fixture" } };
        var company = new LegalEntity { ShortName = "Synthetic company", BankAccountsJson = "[{\"id\":\"local-id\",\"accountNumber\":\"40702810200000000001\",\"bankConnectionAccountId\":999}]" };
        db.Users.Add(user); db.LegalEntities.Add(company); await db.SaveChangesAsync();
        var connection = new BankConnection { LegalEntityId = company.Id, Provider = BankProviderKeys.Sber };
        db.BankConnections.Add(connection); await db.SaveChangesAsync();
        var bank = new BankConnectionAccount { BankConnectionId = connection.Id, ExternalAccountId = "synthetic-source", AccountNumber = "40702810200000000001", Bik = "044525225", Currency = "RUB" };
        db.BankConnectionAccounts.Add(bank); await db.SaveChangesAsync();
        return new(company.Id, bank.Id, user.Id);
    }

    internal static CompanyBankAccountBinding Binding(Fixture f) => new()
    {
        LegalEntityId = f.Company,
        BankConnectionAccountId = f.Bank,
        ProfileAccountId = "local-id",
        ProfileAccountKey = CompanyBankAccountBindingIdentity.ProfileKey("local-id"),
        Provider = BankProviderKeys.Sber,
        ExternalAccountId = "synthetic-source",
        AccountNumber = "40702810200000000001",
        Bik = "044525225",
        Currency = "RUB",
        ConfirmedMatchVersion = "confirmed-v1",
        CreatedByUserId = f.User,
        CreatedByUserName = "Administrator",
        CreatedAtUtc = Created
    };

    private static void Revoke(CompanyBankAccountBinding row, int user)
    { row.RevokedAtUtc = Created.AddMinutes(1); row.RevokedByUserId = user; row.RevokedByUserName = "Administrator"; row.RevocationMatchVersion = "revoke-v1"; }

    private static async Task AssertForeignKeyFailureAsync(bool postgres, Func<Task<int>> action)
    {
        if (postgres)
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(action)).SqlState);
        else
        {
            var error = await Assert.ThrowsAsync<SqliteException>(action);
            // SQLite's immediate RESTRICT action uses SQLITE_CONSTRAINT_TRIGGER.
            Assert.Equal(19, error.SqliteErrorCode);
            Assert.Equal(1811, error.SqliteExtendedErrorCode);
            Assert.Contains("FOREIGN KEY constraint failed", error.Message, StringComparison.Ordinal);
        }
    }

    private static async Task<int> AddBankAsync(PaymentContext db, int bankId)
    {
        var bank = await db.BankConnectionAccounts.AsNoTracking().SingleAsync(x => x.Id == bankId);
        var other = new BankConnectionAccount { BankConnectionId = bank.BankConnectionId, ExternalAccountId = "other-synthetic-source", AccountNumber = "40702810200000000002", Currency = "RUB" };
        db.BankConnectionAccounts.Add(other); await db.SaveChangesAsync(); return other.Id;
    }

    private async Task RunAsync(bool postgres, Func<PaymentContext, Task> test)
    {
        if (postgres)
        {
            await using var database = await template.CreateDatabaseAsync();
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
            await test(db);
        }
        else { await using var db = await SqliteTestContextFactory.CreateAsync(); await test(db); }
    }
}

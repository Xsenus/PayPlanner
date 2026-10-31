using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankAccountBindingMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCleanAndUpgradePreserveHistoryAndSupportDownUp(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await VerifyAsync(db, upgrade, "20261031125245_AddPaymentOverdueNotificationOutbox", "20261031232958_AddCompanyBankAccountBindings");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlCleanAndUpgradePreserveHistoryAndSupportDownUp(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Existing isolated local PostgreSQL credentials are required.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        Assert.Equal("payplanner_ci", settings.Username);
        var name = "pp_binding_migration_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
        await using (var identity = new NpgsqlCommand("SELECT current_setting('server_version_num')::int >= 170000 AND inet_server_port() = 5433 AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await identity.ExecuteScalarAsync());
        var created = false;
        try
        {
            await using (var absent = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin))
            { absent.Parameters.AddWithValue("name", name); Assert.Equal(0L, await absent.ExecuteScalarAsync()); }
            created = true;
            Console.WriteLine($"Owned binding migration database: {name}");
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await command.ExecuteNonQueryAsync();
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options);
            await VerifyAsync(db, upgrade, "20261031125252_AddPaymentOverdueNotificationOutboxPostgreSql", "20261031233007_AddCompanyBankAccountBindingsPostgreSql");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateDatabase)
        { created = false; throw; }
        finally { if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name); }
    }

    private static async Task VerifyAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        CompanyBankAccountBindingStorageTests.Fixture? fixture = null;
        string? historical = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync(previous);
            fixture = await SeedHistoryAsync(db);
            historical = await HistoryAsync(db);
            db.ChangeTracker.Clear();
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        // Arbitrary bankConnectionAccountId in historical JSON is never backfilled.
        Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
        if (upgrade) Assert.Equal(historical, await HistoryAsync(db));
        else { fixture = await SeedHistoryAsync(db); historical = await HistoryAsync(db); }
        var row = CompanyBankAccountBindingStorageTests.Binding(fixture!);
        db.CompanyBankAccountBindings.Add(row); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(row.ProfileAccountId, (await db.CompanyBankAccountBindings.SingleAsync()).ProfileAccountId);
        Assert.Equal(historical, await HistoryAsync(db));
        var duplicate = CompanyBankAccountBindingStorageTests.Binding(fixture!);
        db.CompanyBankAccountBindings.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        // Exercise the NULL branch of the actual migrated CHECK, not only EnsureCreated.
        var saved = await db.CompanyBankAccountBindings.SingleAsync();
        saved.RevocationMatchVersion = "invalid-without-date";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync(previous);
        Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(historical, await HistoryAsync(db));
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.CompanyBankAccountBindings.ToArrayAsync());
        Assert.Equal(historical, await HistoryAsync(db));
    }

    private static async Task<CompanyBankAccountBindingStorageTests.Fixture> SeedHistoryAsync(PaymentContext db)
    {
        var fixture = await CompanyBankAccountBindingStorageTests.SeedAsync(db);
        var wallet = new MoneyAccount { Name = "Historical manual bank", CompanyId = fixture.Company, Source = MoneyOperationSource.Bank, BankAccountNumber = "40702810200000000001", BankBik = "044525225", Currency = "RUB" };
        db.MoneyAccounts.Add(wallet);
        db.Payments.Add(new Payment { Account = "HISTORICAL-123", Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 123.45m, PaidAmount = 23.45m, Notes = "Issued historical record" });
        db.BankStatementOperations.Add(new() { BankConnectionAccountId = fixture.Bank, ExternalOperationId = "old-operation", Amount = 123m, OperationDate = CompanyBankAccountBindingStorageTests.Created, Purpose = "Historical statement" });
        await db.SaveChangesAsync();
        db.MoneyOperations.Add(new MoneyOperation { MoneyAccountId = wallet.Id, Source = MoneyOperationSource.Bank, Amount = 67.89m, MatchComment = "Historical manual amount" });
        await db.SaveChangesAsync(); return fixture;
    }

    private static async Task<string> HistoryAsync(PaymentContext db)
        => JsonSerializer.Serialize(new
        {
            profiles = await db.LegalEntities.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankAccountsJson }).ToArrayAsync(),
            banks = await db.BankConnectionAccounts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankConnectionId, x.ExternalAccountId, x.AccountNumber, x.Bik, x.Currency }).ToArrayAsync(),
            money = await db.MoneyAccounts.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.CompanyId, x.BankConnectionAccountId, x.BankAccountNumber, x.Currency }).ToArrayAsync(),
            operations = await db.MoneyOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.MoneyAccountId, x.Amount, x.MatchComment }).ToArrayAsync(),
            statements = await db.BankStatementOperations.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.BankConnectionAccountId, x.Amount, x.Purpose }).ToArrayAsync(),
            payments = await db.Payments.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Account, x.Amount, x.PaidAmount, x.Notes }).ToArrayAsync()
        });
}

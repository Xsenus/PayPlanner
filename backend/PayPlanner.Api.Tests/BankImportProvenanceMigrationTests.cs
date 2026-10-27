using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BankImportProvenanceMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnedMigrationCleanup_WaitsForClosingSessionAndPreservesLiveSession(bool releaseDuringCleanup)
    {
        var fixture = await MigrationDatabase.CreateAsync(postgres: true);
        var settings = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString!)
        {
            Database = fixture.DatabaseName,
            Pooling = false,
            ApplicationName = "bank-migration-cleanup-held",
        };
        await using var held = new NpgsqlConnection(settings.ConnectionString);
        await held.OpenAsync();
        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        Task? disposal = null;
        var removed = false;
        try
        {
            if (releaseDuringCleanup)
            {
                disposal = fixture.DisposeAsync().AsTask();
                await Task.Delay(200);
                Assert.False(disposal.IsCompleted);
                await AssertHeldConnectionUsableAsync();
                await held.CloseAsync();
                await disposal;
                removed = true;
                Assert.Equal(0L, await DatabaseExistsAsync());
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());
                Assert.Contains("preserved:", error.Message);
                await AssertHeldConnectionUsableAsync();
                Assert.Equal(1L, await DatabaseExistsAsync());
            }
        }
        finally
        {
            await held.CloseAsync();
            if (disposal is not null && !removed)
            {
                try { await disposal; removed = true; }
                catch (InvalidOperationException) { /* The old immediate cleanup retains the database. */ }
            }
            if (!removed)
            {
                // Preserve a failing assertion while allowing the server to observe our socket close.
                await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                sessions.Parameters.AddWithValue("name", fixture.DatabaseName!);
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if ((long)(await sessions.ExecuteScalarAsync())! == 0) break;
                    await Task.Delay(100);
                }
                await fixture.DisposeAsync();
            }
        }

        async Task AssertHeldConnectionUsableAsync()
        {
            await using var command = new NpgsqlCommand("SELECT 1", held);
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
        }

        async Task<long> DatabaseExistsAsync()
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin);
            command.Parameters.AddWithValue("name", fixture.DatabaseName!);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Migration_CleanAndUpgradeKeepLegacyFactsWithoutAssigningOwnership(bool postgres, int previousStage)
    {
        await using var fixture = await MigrationDatabase.CreateAsync(postgres);
        var db = fixture.Db;
        var provenance = postgres ? "20261016101256_AddBankImportProvenancePostgreSql" : "20261016101247_AddBankImportProvenance";
        var current = postgres ? "20261016132320_AddMoneyBalanceScopePostgreSql" : "20261016132312_AddMoneyBalanceScope";
        var migrations = db.Database.GetMigrations().ToArray();
        var provenanceIndex = Array.IndexOf(migrations, provenance);
        Assert.True(provenanceIndex > 0);
        var policy = postgres ? "20261024110001_AddDocumentSellerSnapshotPolicyPostgres" : "20261024110000_AddDocumentSellerSnapshotPolicy";
        var paymentAttempts = postgres ? "20261027005432_AddPaymentCreateAttempts" : "20261027005425_AddPaymentCreateAttempts";
        var chatHistory = postgres ? "20261027014154_AddPersonalChatHistory" : "20261027014147_AddPersonalChatHistory";
        Assert.Equal(new[] { current, policy, paymentAttempts, chatHistory }, migrations[(provenanceIndex + 1)..]);
        var upgrade = previousStage > 0;
        string? before = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync(previousStage == 1 ? migrations[provenanceIndex - 1] : provenance);
            // Seed the real previous schema directly. Do not temporarily add new model columns.
            await SeedLegacyAsync(db);
            before = await LegacySnapshotAsync(db);
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        if (!await db.SystemSettings.AnyAsync(item => item.Key == "notifications.email.enabled"))
        {
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        if (upgrade)
        {
            Assert.Equal(before, await LegacySnapshotAsync(db));
            var account = await db.MoneyAccounts.AsNoTracking().SingleAsync(item => item.Id == 701);
            Assert.Null(account.CompanyId);
            Assert.Null(account.BankConnectionAccountId);
            Assert.Null(account.BankBik);
            Assert.Null((await db.MoneyOperations.AsNoTracking().SingleAsync(item => item.Id == 702)).SourceMovementKey);
            var batch = await db.BankStatementImportBatches.AsNoTracking().SingleAsync(item => item.Id == 703);
            Assert.Null(batch.CompanyId);
            Assert.Null(batch.MoneyAccountId);
            Assert.Null(batch.BankConnectionAccountId);
            Assert.Null(batch.ImportedByUserId);
            Assert.Null(batch.ResponsibleUserId);
            Assert.Null(batch.Source);
            Assert.Null(batch.Trigger);
            Assert.Null(batch.ParentBatchId);
            Assert.Null(batch.PackageKey);
            Assert.Null(batch.PeriodFrom);
            Assert.Null(batch.PeriodTo);
            Assert.Null(batch.CompletedAt);
            Assert.Null(batch.SourceResultJson);
            var row = await db.BankStatementImportRows.AsNoTracking().SingleAsync(item => item.Id == 704);
            Assert.Null(row.BankStatementOperationId);
            Assert.Null(row.Currency);
            Assert.Null(row.ExternalOperationId);
            Assert.Null(row.IsBooked);
            Assert.Null(row.MovementKey);
            Assert.Null(row.OriginRowId);
            Assert.Null(row.RowIdentityKey);
            var reconciliation = await db.MoneyBalanceReconciliationResults.AsNoTracking().SingleAsync(item => item.Id == 705);
            Assert.Null(reconciliation.CompanyId);
            Assert.Null(reconciliation.MoneyAccountId);
            Assert.Null(reconciliation.Currency);
            Assert.Null(reconciliation.ScopeKey);
            Assert.Equal(900m, reconciliation.Income);
            Assert.Equal(876.55m, reconciliation.ClosingBalance);
        }
        await AssertCompanyIndexesAndForeignKeyAsync(db);
        await AssertReconciliationIndexesAndForeignKeysAsync(db);
        await db.Database.MigrateAsync();
        if (upgrade) Assert.Equal(before, await LegacySnapshotAsync(db));
    }

    private static async Task SeedLegacyAsync(PaymentContext db)
    {
        var company = new LegalEntity { ShortName = "Синтетическая основная компания" };
        db.LegalEntities.Add(company);
        await db.SaveChangesAsync();
        db.SystemSettings.AddRange(
            new SystemSetting { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id }) },
            new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        await db.SaveChangesAsync();
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MoneyAccounts" ("Id","Name","Source","Currency","BankName","BankAccountNumber","IsDefault","IsActive","CreatedAt")
            VALUES ({701},{"Старый счёт"},{"Bank"},{"RUB"},{"Синтетический банк"},{"synthetic-legacy-account"},{true},{true},{date})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BankStatementImportBatches" ("Id","FileName","FileHash","FileSize","Status","RowsRead","CreatedRows","SkippedRows","ErrorRows","RollbackRows","CreatedAt")
            VALUES ({703},{"synthetic-legacy.csv"},{"synthetic-legacy-hash"},{123L},{"Applied"},{1},{1},{0},{0},{0},{date})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MoneyOperations" ("Id","MoneyAccountId","Source","Direction","AccountingType","Amount","Currency","Date","IsAdvancePayment","MatchStatus","BankStatementImportBatchId","Purpose","CreatedAt")
            VALUES ({702},{701},{"Bank"},{"Expense"},{"Fact"},{123.45m},{"RUB"},{date},{false},{"None"},{703},{"Синтетическая старая операция"},{date})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BankStatementImportRows" ("Id","BatchId","RowNumber","ImportKey","Status","MoneyOperationId","Date","Direction","Amount","AccountName","CreatedAt")
            VALUES ({704},{703},{1},{"synthetic-legacy-import-key"},{"Created"},{702},{date},{"Expense"},{123.45m},{"Старый счёт"},{date})
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MoneyBalanceReconciliationResults" ("Id","Date","Source","AccountName","OpeningBalance","Income","Expense","ClosingBalance","IncomeCount","ExpenseCount","IsBalanced","IssueCount","CheckedAt")
            VALUES ({705},{date},{"Bank"},{"Старый счёт"},{100m},{900m},{123.45m},{876.55m},{1},{1},{true},{0},{date})
            """);
    }

    private static async Task<string> LegacySnapshotAsync(PaymentContext db)
    {
        var queries = new[]
        {
            "SELECT \"Id\",\"Name\",\"Source\",\"Currency\",\"BankName\",\"BankAccountNumber\",\"IsDefault\",\"IsActive\",\"CreatedAt\",\"UpdatedAt\" FROM \"MoneyAccounts\" WHERE \"Id\"=701",
            "SELECT \"Id\",\"MoneyAccountId\",\"Source\",\"Direction\",\"AccountingType\",\"Amount\",\"Currency\",\"Date\",\"IsAdvancePayment\",\"MatchStatus\",\"BankStatementImportBatchId\",\"Purpose\",\"CreatedAt\" FROM \"MoneyOperations\" WHERE \"Id\"=702",
            "SELECT \"Id\",\"FileName\",\"FileHash\",\"FileSize\",\"Status\",\"RowsRead\",\"CreatedRows\",\"SkippedRows\",\"ErrorRows\",\"RollbackRows\",\"CreatedAt\",\"RolledBackAt\" FROM \"BankStatementImportBatches\" WHERE \"Id\"=703",
            "SELECT \"Id\",\"BatchId\",\"RowNumber\",\"ImportKey\",\"Status\",\"MoneyOperationId\",\"Date\",\"Direction\",\"Amount\",\"AccountName\",\"CreatedAt\" FROM \"BankStatementImportRows\" WHERE \"Id\"=704",
            "SELECT \"Id\",\"Date\",\"Source\",\"AccountName\",\"OpeningBalance\",\"Income\",\"Expense\",\"ClosingBalance\",\"IncomeCount\",\"ExpenseCount\",\"IsBalanced\",\"IssueCount\",\"CheckedAt\",\"CheckedByUserId\" FROM \"MoneyBalanceReconciliationResults\" WHERE \"Id\"=705",
        };
        await db.Database.OpenConnectionAsync();
        var result = new List<object?[]>();
        foreach (var query in queries)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = query;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            result.Add(values);
            Assert.False(await reader.ReadAsync());
        }
        return JsonSerializer.Serialize(result);
    }

    private static async Task AssertReconciliationIndexesAndForeignKeysAsync(PaymentContext db)
    {
        var accounts = await db.MoneyAccounts.AsNoTracking().Where(item => item.Name == "Общий счёт")
            .OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, accounts.Length);
        MoneyBalanceReconciliationResult Result(string? key, MoneyAccount? account) => new()
        {
            Date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Source = MoneyOperationSource.Bank,
            ScopeKey = key,
            CompanyId = account?.CompanyId,
            MoneyAccountId = account?.Id,
            Currency = account?.Currency,
            AccountName = account?.Name ?? "Историческая сверка индекса",
            Income = 17.25m,
            ClosingBalance = 17.25m,
            IncomeCount = 1,
        };
        var first = Result("synthetic:balance:first", accounts[0]);
        var second = Result("synthetic:balance:second", accounts[1]);
        var legacy = Result(null, null);
        db.MoneyBalanceReconciliationResults.AddRange(first, second, legacy);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.MoneyBalanceReconciliationResults.CountAsync(item => item.AccountName == "Общий счёт"));

        async Task Reject(MoneyBalanceReconciliationResult invalid)
        {
            db.MoneyBalanceReconciliationResults.Add(invalid);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.Entry(invalid).State = EntityState.Detached;
        }
        await Reject(Result(first.ScopeKey, accounts[1]));
        await Reject(Result(null, null));
        var badCompany = Result("synthetic:balance:bad-company", accounts[0]);
        badCompany.CompanyId = int.MaxValue;
        await Reject(badCompany);
        var badAccount = Result("synthetic:balance:bad-account", accounts[0]);
        badAccount.MoneyAccountId = int.MaxValue;
        await Reject(badAccount);
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MoneyAccounts\" WHERE \"Id\"={accounts[0].Id}"));
        db.ChangeTracker.Clear();
        Assert.True(await db.MoneyAccounts.AnyAsync(item => item.Id == accounts[0].Id));
        Assert.Equal(2, await db.MoneyBalanceReconciliationResults.CountAsync(item => item.AccountName == "Общий счёт"));
    }

    private static async Task AssertCompanyIndexesAndForeignKeyAsync(PaymentContext db)
    {
        var first = new LegalEntity { ShortName = "Синтетическая компания А" };
        var second = new LegalEntity { ShortName = "Синтетическая компания Б" };
        db.LegalEntities.AddRange(first, second);
        await db.SaveChangesAsync();
        db.MoneyAccounts.AddRange(
            new MoneyAccount { Name = "Общий счёт", Source = MoneyOperationSource.Bank, CompanyId = first.Id },
            new MoneyAccount { Name = "Общий счёт", Source = MoneyOperationSource.Bank, CompanyId = second.Id },
            new MoneyAccount { Name = "Без компании", Source = MoneyOperationSource.Bank });
        await db.SaveChangesAsync();
        var duplicate = new MoneyAccount { Name = "Общий счёт", Source = MoneyOperationSource.Bank, CompanyId = first.Id };
        db.MoneyAccounts.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(duplicate).State = EntityState.Detached;
        var legacyDuplicate = new MoneyAccount { Name = "Без компании", Source = MoneyOperationSource.Bank };
        db.MoneyAccounts.Add(legacyDuplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(legacyDuplicate).State = EntityState.Detached;
        var invalid = new MoneyAccount { Name = "Несуществующая компания", Source = MoneyOperationSource.Bank, CompanyId = int.MaxValue };
        db.MoneyAccounts.Add(invalid);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(invalid).State = EntityState.Detached;
        // Exercise the physical FK, independently of the application's soft-delete interceptor.
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"LegalEntities\" WHERE \"Id\"={first.Id}"));
        db.ChangeTracker.Clear();
        Assert.True(await db.LegalEntities.AnyAsync(item => item.Id == first.Id));
        Assert.Equal(2, await db.MoneyAccounts.CountAsync(item => item.Name == "Общий счёт"));
    }

    private sealed class MigrationDatabase(PaymentContext db, DbConnection? connection, string? databaseName, string? adminConnection) : IAsyncDisposable
    {
        public PaymentContext Db { get; } = db;
        public string? DatabaseName => databaseName;
        public string? AdminConnectionString => adminConnection;

        public static async Task<MigrationDatabase> CreateAsync(bool postgres)
        {
            if (!postgres)
            {
                var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
                await connection.OpenAsync();
                return new(new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options), connection, null, null);
            }
            var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Migration tests require an owned PostgreSQL endpoint.");
            var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
            if (settings.Host is not ("127.0.0.1" or "localhost" or "::1")) throw new InvalidOperationException("Only loopback PostgreSQL is allowed.");
            var adminConnection = settings.ConnectionString;
            var name = "pp_bank_migrate_" + Guid.NewGuid().ToString("N");
            await using (var admin = new NpgsqlConnection(adminConnection))
            {
                await admin.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await command.ExecuteNonQueryAsync();
                Console.WriteLine($"Owned bank migration database created: {name}");
            }
            settings.Database = name;
            return new(new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options), null, name, adminConnection);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
            if (databaseName is null) return;
            Assert.Matches("^pp_bank_migrate_[a-f0-9]{32}$", databaseName);
            await using var admin = new NpgsqlConnection(adminConnection!);
            await admin.OpenAsync();
            await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
            sessions.Parameters.AddWithValue("name", databaseName);
            // A closed client socket may remain visible briefly on the server.
            // Match the model fixture's grace period; never terminate a live session.
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                if ((long)(await sessions.ExecuteScalarAsync())! == 0) break;
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException($"Owned migration database still has sessions; preserved: {databaseName}");
                await Task.Delay(100);
            }
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupCatalogMigrationTests
{
    private const string SqlitePrevious = "20260927052749_AddCourtRegistrationDate";
    private const string SqliteCurrent = "20260927183050_AddDatabaseBackupCatalog";
    private const string PostgresPrevious = "20260927052814_AddCourtRegistrationDatePostgres";
    private const string PostgresCurrent = "20260927183105_AddDatabaseBackupCatalogPostgres";

    [Fact]
    public void Migrations_AreDiscoverableForBothProviders()
    {
        Assert.Equal(SqliteCurrent, Assert.Single(typeof(AddDatabaseBackupCatalog)
            .GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>()).Id);
        Assert.Equal(PostgresCurrent, Assert.Single(typeof(AddDatabaseBackupCatalogPostgres)
            .GetCustomAttributes(typeof(MigrationAttribute), false).Cast<MigrationAttribute>()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_PersistsBackupSetsAndEnforcesOneCopyPerDestination(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        if (upgrade) await context.Database.MigrateAsync(SqlitePrevious);
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteCurrent, await context.Database.GetAppliedMigrationsAsync());
        await AssertCatalogRoundTripAsync(context);
    }

    [Fact]
    public void PostgreSqlMigration_GeneratesCatalogTablesWithoutRebuildingExistingData()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test").Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPrevious, PostgresCurrent);
        Assert.Contains("CREATE TABLE \"DatabaseBackupSets\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"DatabaseBackupCopies\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE \"StorageObjects\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlMigration_WorksOnDisposableCleanAndUpgradeDatabases(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "payplanner_backup_catalog_test_" + Guid.NewGuid().ToString("N");
        var adminConnection = new NpgsqlConnectionStringBuilder(rawConnection) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testConnection = new NpgsqlConnectionStringBuilder(rawConnection) { Database = databaseName, Pooling = false };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testConnection.ConnectionString).Options;
            await using var context = new PostgreSqlPaymentContext(options);
            if (upgrade) await context.Database.MigrateAsync(PostgresPrevious);
            await context.Database.MigrateAsync();
            Assert.Contains(PostgresCurrent, await context.Database.GetAppliedMigrationsAsync());
            await AssertCatalogRoundTripAsync(context);
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertCatalogRoundTripAsync(PaymentContext context)
    {
        var set = new DatabaseBackupSet
        {
            Id = Guid.NewGuid().ToString("N"),
            BackupRelativePath = "2026/09/catalog-test.zip",
            DatabaseProvider = context.Database.IsNpgsql() ? "postgresql" : "sqlite",
            BackupFormat = "zip",
            ManifestSha256 = new string('a', 64),
            Status = DatabaseBackupSetStatuses.LocalVerified,
        };
        set.Copies.Add(new DatabaseBackupCopy
        {
            ProviderKey = "local",
            Locator = set.BackupRelativePath,
            Status = DatabaseBackupCopyStatuses.Verified,
        });
        context.DatabaseBackupSets.Add(set);
        await context.SaveChangesAsync();

        Assert.Equal(1, await context.DatabaseBackupCopies.CountAsync(item => item.BackupSetId == set.Id));
        context.DatabaseBackupCopies.Add(new DatabaseBackupCopy
        {
            BackupSetId = set.Id,
            ProviderKey = "local",
            Locator = "duplicate",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}

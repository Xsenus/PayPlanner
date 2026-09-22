using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCatalogMigrationTests
{
    private const string SqliteMigrationId = "20260922052603_AddStorageCatalog";
    private const string PostgreSqlMigrationId = "20260922052737_AddStorageCatalogPostgres";

    [Fact]
    public void StorageCatalogMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddStorageCatalog>(SqliteMigrationId, typeof(PaymentContext));
        AssertMigration<AddStorageCatalogPostgres>(PostgreSqlMigrationId, typeof(PostgreSqlPaymentContext));
    }

    [Fact]
    public async Task SqliteMigration_UpgradesExistingDatabaseAndEnforcesCatalogInvariants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;

        await using (var previousContext = new PaymentContext(options))
        {
            await previousContext.Database.MigrateAsync("20260921151818_AddInternalChat");
        }

        await using (var upgradedContext = new PaymentContext(options))
        {
            await upgradedContext.Database.MigrateAsync();
            Assert.Contains(SqliteMigrationId, await upgradedContext.Database.GetAppliedMigrationsAsync());

            var storageObject = new StorageObject
            {
                DataClass = "document",
                OwnerType = "test",
                OwnerId = "owner-1",
                LogicalName = "evidence.pdf",
                ContentType = "application/pdf",
                CurrentGeneration = 1,
            };
            var generation = new StorageObjectGeneration
            {
                StorageObject = storageObject,
                Generation = 1,
                SizeBytes = 4,
                Sha256 = new string('a', 64),
                PolicyVersion = "test-v1",
            };
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = "local",
                Locator = "document/test/owner-1/evidence.pdf",
                Status = StorageReplicaStatuses.Available,
                SizeBytes = 4,
                Sha256 = new string('a', 64),
            });
            storageObject.Generations.Add(generation);
            storageObject.Operations.Add(new StorageOperation
            {
                StorageObjectGeneration = generation,
                OperationType = "put",
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = "migration-test-put-1",
                ProviderKey = "local",
            });
            upgradedContext.StorageObjects.Add(storageObject);
            await upgradedContext.SaveChangesAsync();

            Assert.Equal(1, await upgradedContext.StorageObjects.CountAsync());
            Assert.Equal(1, await upgradedContext.StorageObjectGenerations.CountAsync());
            Assert.Equal(1, await upgradedContext.StorageReplicas.CountAsync());
            Assert.Equal(1, await upgradedContext.StorageOperations.CountAsync());
        }

        await using var duplicateContext = new PaymentContext(options);
        duplicateContext.StorageOperations.Add(new StorageOperation
        {
            StorageObjectId = await duplicateContext.StorageObjects.Select(item => item.Id).SingleAsync(),
            OperationType = "put",
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = "migration-test-put-1",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
    }

    [Fact]
    public void PostgreSqlMigration_GeneratesProviderSpecificCatalogSql()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var migrator = context.GetService<IMigrator>();

        var script = migrator.GenerateScript(
            "20260921152240_AddInternalChatPostgres",
            PostgreSqlMigrationId);

        Assert.Contains("CREATE TABLE \"StorageObjects\"", script, StringComparison.Ordinal);
        Assert.Contains("uuid", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bigint", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("boolean", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_StorageOperations_IdempotencyKey\"", script, StringComparison.Ordinal);
    }

    private static void AssertMigration<TMigration>(string expectedId, Type expectedContext)
    {
        var migration = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), inherit: false));
        Assert.Equal(expectedId, ((MigrationAttribute)migration).Id);

        var dbContext = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
        Assert.Equal(expectedContext, ((DbContextAttribute)dbContext).ContextType);
    }
}

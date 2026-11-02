using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskContractorPaymentMigrationTests
{
    [Theory]
    [InlineData(typeof(AddTaskContractorPayments), typeof(PaymentContext), "20260521190000_AddTaskContractorPayments")]
    [InlineData(typeof(AddTaskContractorPaymentsPostgres), typeof(PostgreSqlPaymentContext), "20260521190030_AddTaskContractorPaymentsPostgres")]
    public void ContractorPaymentMigrationHasProviderSpecificIdentity(Type migration, Type context, string id)
    {
        var identity = Assert.Single(migration.GetCustomAttributes(typeof(MigrationAttribute), inherit: false));
        Assert.Equal(id, ((MigrationAttribute)identity).Id);
        var owner = Assert.Single(migration.GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
        Assert.Equal(context, ((DbContextAttribute)owner).ContextType);
    }

    [Fact]
    public void PostgreSqlCatalogIncludesPreviouslyInstalledContractorPaymentMigration()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5433;Database=payplanner_test_catalog;Username=payplanner_ci").Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        Assert.Contains("20260521190030_AddTaskContractorPaymentsPostgres", context.Database.GetMigrations());
    }

    [Fact]
    public void EveryMigrationHasUniqueIdentityOrKnownLegacyExclusion()
    {
        // These two historical classes have no EF registration in the installed
        // baseline. Registering them would change its already verified catalog.
        var unregisteredLegacyTypes = new HashSet<Type>
        {
            typeof(BackfillPlannedDates),
            typeof(AddCashInventoriesPostgres)
        };
        var identities = new HashSet<(Type Context, string Id)>();
        foreach (var migration in typeof(AddTaskContractorPaymentsPostgres).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type)))
        {
            var migrationAttributes = migration.GetCustomAttributes(typeof(MigrationAttribute), inherit: false);
            var contextAttributes = migration.GetCustomAttributes(typeof(DbContextAttribute), inherit: false);
            if (unregisteredLegacyTypes.Contains(migration))
            {
                Assert.Empty(migrationAttributes);
                Assert.Empty(contextAttributes);
                continue;
            }
            var identity = Assert.Single(migrationAttributes);
            var owner = Assert.Single(contextAttributes);
            Assert.True(identities.Add((((DbContextAttribute)owner).ContextType, ((MigrationAttribute)identity).Id)),
                $"Duplicate migration identity: {migration.FullName}");
        }
        Assert.NotEmpty(identities);
    }
}

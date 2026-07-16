using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CashOrderMigrationTests
{
    [Fact]
    public void CashOrderReadIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddCashOrderReadIndexes>(
            "20260716223446_AddCashOrderReadIndexes",
            typeof(PaymentContext));

        AssertMigration<AddCashOrderReadIndexesPostgres>(
            "20260716223532_AddCashOrderReadIndexesPostgres",
            typeof(PostgreSqlPaymentContext));
    }

    private static void AssertMigration<TMigration>(string expectedId, Type expectedContext)
    {
        var migration = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), inherit: false));
        Assert.Equal(expectedId, ((MigrationAttribute)migration).Id);

        var dbContext = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
        Assert.Equal(expectedContext, ((DbContextAttribute)dbContext).ContextType);
    }
}

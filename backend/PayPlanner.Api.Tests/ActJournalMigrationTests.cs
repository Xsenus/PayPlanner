using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ActJournalMigrationTests
{
    [Fact]
    public void ActJournalReadIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddActJournalReadIndex>(
            "20260716195731_AddActJournalReadIndex",
            typeof(PaymentContext));

        AssertMigration<AddActJournalReadIndexPostgres>(
            "20260716195737_AddActJournalReadIndexPostgres",
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

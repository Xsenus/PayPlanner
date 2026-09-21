using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskJournalMigrationTests
{
    [Fact]
    public void TaskJournalReadIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddTaskJournalReadIndexes>(
            "20260716185515_AddTaskJournalReadIndexes",
            typeof(PaymentContext));

        AssertMigration<AddTaskJournalReadIndexesPostgres>(
            "20260716185521_AddTaskJournalReadIndexesPostgres",
            typeof(PostgreSqlPaymentContext));
    }

    [Fact]
    public void TaskRecurrenceMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddTaskRecurrence>(
            "20260921101627_AddTaskRecurrence",
            typeof(PaymentContext));

        AssertMigration<AddTaskRecurrencePostgres>(
            "20260921101641_AddTaskRecurrencePostgres",
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

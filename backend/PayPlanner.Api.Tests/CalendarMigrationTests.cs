using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CalendarMigrationTests
{
    [Fact]
    public void CalendarSyncFromDateMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddCalendarSyncFromDate>(
            "20260602053000_AddCalendarSyncFromDate",
            typeof(PaymentContext));

        AssertMigration<AddCalendarSyncFromDatePostgres>(
            "20260602053100_AddCalendarSyncFromDatePostgres",
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

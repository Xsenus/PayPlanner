using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentJournalMigrationTests
{
    [Fact]
    public void PaymentJournalReadIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<AddPaymentJournalReadIndexes>(
            "20260716201756_AddPaymentJournalReadIndexes",
            typeof(PaymentContext));

        AssertMigration<AddPaymentJournalReadIndexesPostgres>(
            "20260716201802_AddPaymentJournalReadIndexesPostgres",
            typeof(PostgreSqlPaymentContext));
    }

    [Fact]
    public void PaymentStatusUpdaterIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<OptimizePaymentStatusUpdaterIndex>(
            "20260716234103_OptimizePaymentStatusUpdaterIndex",
            typeof(PaymentContext));

        AssertMigration<OptimizePaymentStatusUpdaterIndexPostgres>(
            "20260716234145_OptimizePaymentStatusUpdaterIndexPostgres",
            typeof(PostgreSqlPaymentContext));
    }

    [Fact]
    public void PaymentStatusUpdaterIndex_CoversPaidStateStatusAndDate_InBothModels()
    {
        var sqliteOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var sqliteContext = new PaymentContext(sqliteOptions, useObjectAuditModel: false);
        AssertPaymentStatusUpdaterIndex(sqliteContext);

        var postgresOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_model_test;Username=postgres;Password=postgres")
            .Options;
        using var postgresContext = new PostgreSqlPaymentContext(postgresOptions, useObjectAuditModel: false);
        AssertPaymentStatusUpdaterIndex(postgresContext);
    }

    private static void AssertMigration<TMigration>(string expectedId, Type expectedContext)
    {
        var migration = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), inherit: false));
        Assert.Equal(expectedId, ((MigrationAttribute)migration).Id);

        var dbContext = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
        Assert.Equal(expectedContext, ((DbContextAttribute)dbContext).ContextType);
    }

    private static void AssertPaymentStatusUpdaterIndex(DbContext context)
    {
        var payment = context.Model.FindEntityType(typeof(Payment));
        Assert.NotNull(payment);
        var index = Assert.Single(
            payment!.GetIndexes(),
            item => item.GetDatabaseName() == "IX_Payments_IsPaid_Status_Date");

        Assert.Equal(
            [nameof(Payment.IsPaid), nameof(Payment.Status), nameof(Payment.Date)],
            index.Properties.Select(property => property.Name));
        Assert.DoesNotContain(
            payment.GetIndexes(),
            item => item.GetDatabaseName() == "IX_Payments_IsPaid_Status");
    }
}

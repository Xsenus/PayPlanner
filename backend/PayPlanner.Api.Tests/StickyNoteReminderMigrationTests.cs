using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StickyNoteReminderMigrationTests
{
    [Fact]
    public void StickyNoteReminderIndexMigrations_AreDiscoverableByEfCore()
    {
        AssertMigration<OptimizeStickyNoteReminderIndex>(
            "20260717010158_OptimizeStickyNoteReminderIndex",
            typeof(PaymentContext));

        AssertMigration<OptimizeStickyNoteReminderIndexPostgres>(
            "20260717010239_OptimizeStickyNoteReminderIndexPostgres",
            typeof(PostgreSqlPaymentContext));
    }

    [Fact]
    public void StickyNoteReminderIndex_PrioritizesUnsentDueReminders_InBothModels()
    {
        var sqliteOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var sqliteContext = new PaymentContext(sqliteOptions, useObjectAuditModel: false);
        AssertStickyNoteReminderIndex(sqliteContext);

        var postgresOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_model_test;Username=postgres;Password=postgres")
            .Options;
        using var postgresContext = new PostgreSqlPaymentContext(postgresOptions, useObjectAuditModel: false);
        AssertStickyNoteReminderIndex(postgresContext);
    }

    private static void AssertMigration<TMigration>(string expectedId, Type expectedContext)
    {
        var migration = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(MigrationAttribute), inherit: false));
        Assert.Equal(expectedId, ((MigrationAttribute)migration).Id);

        var dbContext = Assert.Single(typeof(TMigration).GetCustomAttributes(typeof(DbContextAttribute), inherit: false));
        Assert.Equal(expectedContext, ((DbContextAttribute)dbContext).ContextType);
    }

    private static void AssertStickyNoteReminderIndex(DbContext context)
    {
        var stickyNote = context.Model.FindEntityType(typeof(StickyNote));
        Assert.NotNull(stickyNote);
        var index = Assert.Single(
            stickyNote!.GetIndexes(),
            item => item.GetDatabaseName() == "IX_StickyNotes_ReminderSentAt_ReminderAt_Id");

        Assert.Equal(
            [nameof(StickyNote.ReminderSentAt), nameof(StickyNote.ReminderAt), nameof(StickyNote.Id)],
            index.Properties.Select(property => property.Name));
        Assert.DoesNotContain(
            stickyNote.GetIndexes(),
            item => item.GetDatabaseName() == "IX_StickyNotes_ReminderAt_ReminderSentAt");
    }
}

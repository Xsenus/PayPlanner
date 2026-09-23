using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CasePositionReviewMigrationTests
{
    private const string SqlitePreviousMigration = "20260923125617_AddCaseWritMilestones";
    private const string SqliteMigration = "20260923144728_AddCasePositionReview";
    private const string PostgresPreviousMigration = "20260923125903_AddCaseWritMilestonesPostgres";
    private const string PostgresMigration = "20260923144837_AddCasePositionReviewPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_AddsReviewFieldsOnCleanAndExistingDatabase(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;
        await using var context = new PaymentContext(options);
        if (upgrade)
        {
            await context.Database.MigrateAsync(SqlitePreviousMigration);
        }
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteMigration, await context.Database.GetAppliedMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ClientCases')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("PositionReviewedAt", columns);
        Assert.Contains("PositionReviewedByUserId", columns);
        Assert.Contains("PositionReviewedByName", columns);
    }

    [Fact]
    public void PostgreSqlMigration_AddsReviewFields()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);
        Assert.Contains("PositionReviewedAt", script, StringComparison.Ordinal);
        Assert.Contains("PositionReviewedByUserId", script, StringComparison.Ordinal);
        Assert.Contains("PositionReviewedByName", script, StringComparison.Ordinal);
    }
}

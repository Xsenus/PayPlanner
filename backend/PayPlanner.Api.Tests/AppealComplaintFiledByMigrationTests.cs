using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class AppealComplaintFiledByMigrationTests
{
    private const string SqlitePreviousMigration = "20260923144728_AddCasePositionReview";
    private const string SqliteMigration = "20260923152347_AddAppealComplaintFiledBy";
    private const string PostgresPreviousMigration = "20260923144837_AddCasePositionReviewPostgres";
    private const string PostgresMigration = "20260923152458_AddAppealComplaintFiledByPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_AddsSafeDefaultOnCleanAndExistingDatabase(bool upgrade)
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
        command.CommandText = "PRAGMA table_info('ClientCaseCourtComplaints')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<(string Name, string Default)>();
        while (await reader.ReadAsync())
        {
            columns.Add((reader.GetString(1), reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }
        Assert.Contains(columns, item => item.Name == "FiledBy" && item.Default.Contains("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void PostgreSqlMigration_DefaultsExistingComplaintsToUnknown()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);
        Assert.Contains("FiledBy", script, StringComparison.Ordinal);
        Assert.Contains("unknown", script, StringComparison.Ordinal);
    }
}

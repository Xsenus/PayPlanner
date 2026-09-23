using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class NextHearingConfirmationMigrationTests
{
    private const string SqlitePreviousMigration = "20260923183954_AddCourtHearingOutcome";
    private const string SqliteMigration = "20260923193308_AddNextHearingConfirmation";
    private const string PostgresPreviousMigration = "20260923184012_AddCourtHearingOutcomePostgres";
    private const string PostgresMigration = "20260923193454_AddNextHearingConfirmationPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_AddsConfirmationFieldsOnCleanAndExistingDatabase(bool upgrade)
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
        Assert.Contains("NextHearingConfirmedForAt", columns);
        Assert.Contains("NextHearingConfirmedAt", columns);
        Assert.Contains("NextHearingConfirmedByUserId", columns);
        Assert.Contains("NextHearingConfirmedByName", columns);
    }

    [Fact]
    public void PostgreSqlMigration_AddsConfirmationFields()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);
        Assert.Contains("NextHearingConfirmedForAt", script, StringComparison.Ordinal);
        Assert.Contains("NextHearingConfirmedAt", script, StringComparison.Ordinal);
        Assert.Contains("NextHearingConfirmedByUserId", script, StringComparison.Ordinal);
        Assert.Contains("NextHearingConfirmedByName", script, StringComparison.Ordinal);
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Migrations;
using PayPlanner.Api.Migrations.PostgreSql;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncHealthMigrationTests
{
    private const string SqliteMigrationId = "20260923110813_AddCourtSyncHealthState";
    private const string PostgreSqlMigrationId = "20260923110958_AddCourtSyncHealthStatePostgres";

    [Fact]
    public void BothProviderMigrations_PreserveMonitoringForExistingCases()
    {
        var sqliteOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite("Data Source=:memory:")
            .UsePayPlannerSqliteMigrations()
            .Options;
        using var sqliteContext = new PaymentContext(sqliteOptions);
        var sqliteScript = sqliteContext.GetService<IMigrator>().GenerateScript(
            "20260923074950_AddClientCaseAppealDecision", SqliteMigrationId);
        Assert.Contains("CourtSyncMonitoringEnabled", sqliteScript, StringComparison.Ordinal);
        Assert.Contains("DEFAULT 1", sqliteScript, StringComparison.OrdinalIgnoreCase);

        var postgresOptions = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var postgresContext = new PostgreSqlPaymentContext(postgresOptions, useObjectAuditModel: false);
        var postgresScript = postgresContext.GetService<IMigrator>().GenerateScript(
            "20260923075121_AddClientCaseAppealDecisionPostgres", PostgreSqlMigrationId);
        Assert.Contains("CourtSyncMonitoringEnabled", postgresScript, StringComparison.Ordinal);
        Assert.Contains("DEFAULT TRUE", postgresScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqliteMigration_UpgradesExistingDatabaseAndCreatesHealthColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;
        await using var context = new PaymentContext(options);
        await context.Database.MigrateAsync("20260923074950_AddClientCaseAppealDecision");
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteMigrationId, await context.Database.GetAppliedMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ClientCases')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("CourtSyncMonitoringEnabled", columns);
        Assert.Contains("CourtSyncFailureStartedAt", columns);
        Assert.Contains("CourtSyncLastSuccessAt", columns);
    }
}

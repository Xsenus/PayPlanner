using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseWritSubmissionMigrationTests
{
    private const string SqlitePreviousMigration = "20260924002703_AddClientCaseCourtAmounts";
    private const string SqliteMigration = "20260924022616_AddCaseWritSubmission";
    private const string PostgresPreviousMigration = "20260924002727_AddClientCaseCourtAmountsPostgres";
    private const string PostgresMigration = "20260924022633_AddCaseWritSubmissionPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_AddsNullableSubmissionFields(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .UsePayPlannerSqliteMigrations()
            .Options;
        await using var context = new PaymentContext(options);
        if (upgrade) await context.Database.MigrateAsync(SqlitePreviousMigration);
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteMigration, await context.Database.GetAppliedMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ClientCases')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("ExecutiveWritSubmittedAt", columns);
        Assert.Contains("ExecutiveWritSubmittedTo", columns);
    }

    [Fact]
    public void PostgreSqlMigration_AddsNullableSubmissionFields()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);
        Assert.Contains("ExecutiveWritSubmittedAt", script, StringComparison.Ordinal);
        Assert.Contains("ExecutiveWritSubmittedTo", script, StringComparison.Ordinal);
    }
}

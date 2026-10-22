using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseCourtRecoveryMigrationTests
{
    private const string SqlitePreviousMigration = "20260924022616_AddCaseWritSubmission";
    private const string SqliteMigration = "20260924032948_AddCaseCourtRecoveryPayments";
    private const string PostgresPreviousMigration = "20260924022633_AddCaseWritSubmissionPostgres";
    private const string PostgresMigration = "20260924033011_AddCaseCourtRecoveryPaymentsPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_CreatesRecoveryLinksOnCleanAndUpgradePaths(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        if (upgrade) await context.Database.MigrateAsync(SqlitePreviousMigration);
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteMigration, await context.Database.GetAppliedMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ClientCaseCourtRecoveryPayments')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("PaymentId", columns);
        Assert.Contains("ClientCaseId", columns);
        Assert.Contains("AllocatedAmount", columns);
    }

    [Fact]
    public void PostgreSqlMigration_CreatesRecoveryLinksWithoutDuplicatingPayments()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);
        Assert.Contains("ClientCaseCourtRecoveryPayments", script, StringComparison.Ordinal);
        Assert.Contains("AllocatedAmount", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE \"Payments\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlMigration_CreatesRecoveryLinksOnDisposableLocalDatabase(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "payplanner_court_recovery_test_" + Guid.NewGuid().ToString("N");
        var adminConnection = new NpgsqlConnectionStringBuilder(rawConnection)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testConnection = new NpgsqlConnectionStringBuilder(rawConnection)
            {
                Database = databaseName,
                Pooling = false,
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testConnection.ConnectionString).Options;
            await using var context = new PostgreSqlPaymentContext(options);
            if (upgrade) await context.Database.MigrateAsync(PostgresPreviousMigration);
            await context.Database.MigrateAsync();
            Assert.Contains(PostgresMigration, await context.Database.GetAppliedMigrationsAsync());
            Assert.True(await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE table_name = 'ClientCaseCourtRecoveryPayments'")
                .SingleAsync() > 0);
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, databaseName);
        }
    }
}

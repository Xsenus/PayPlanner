using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseHearingPreparationMigrationTests
{
    private const string SqlitePrevious = "20260924174230_AddInternalChatNoReplyReminder";
    private const string SqliteCurrent = "20260924184930_AddCaseHearingPreparation";
    private const string PostgresPrevious = "20260924174244_AddInternalChatNoReplyReminderPostgres";
    private const string PostgresCurrent = "20260924185041_AddCaseHearingPreparationPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CreatesPreparationAndDocumentLinksOnCleanAndUpgradedDatabase(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        if (upgrade)
            await context.Database.MigrateAsync(SqlitePrevious);
        await context.Database.MigrateAsync();
        Assert.Contains(SqliteCurrent, await context.Database.GetAppliedMigrationsAsync());

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ClientCaseHearingPreparations')";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("HearingAt", columns);
        Assert.Contains("ContractCheckedAt", columns);
        Assert.Contains("AuthorityCheckedAt", columns);
        Assert.Contains("MaterialsCheckedAt", columns);
        await reader.CloseAsync();
        command.CommandText = "PRAGMA table_info('ClientCaseHearingPreparationDocuments')";
        await using var linksReader = await command.ExecuteReaderAsync();
        var links = new HashSet<string>(StringComparer.Ordinal);
        while (await linksReader.ReadAsync()) links.Add(linksReader.GetString(1));
        Assert.Contains("ClientCaseDocumentId", links);
    }

    [Fact]
    public void PostgreSql_GeneratesTablesAndUniqueHearingIndex()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = context.GetService<IMigrator>().GenerateScript(PostgresPrevious, PostgresCurrent);
        Assert.Contains("ClientCaseHearingPreparations", script, StringComparison.Ordinal);
        Assert.Contains("ClientCaseHearingPreparationDocuments", script, StringComparison.Ordinal);
        Assert.Contains("ClientCaseId", script, StringComparison.Ordinal);
        Assert.Contains("HearingAt", script, StringComparison.Ordinal);
        Assert.Contains("UNIQUE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CreatesPreparationOnDisposableLocalDatabase(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "payplanner_hearing_prep_test_" + Guid.NewGuid().ToString("N");
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
            if (upgrade) await context.Database.MigrateAsync(PostgresPrevious);
            await context.Database.MigrateAsync();
            Assert.Contains(PostgresCurrent, await context.Database.GetAppliedMigrationsAsync());
            Assert.True(await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE table_name = 'ClientCaseHearingPreparations'")
                .SingleAsync() > 0);
            var clientCase = new ClientCase
            {
                Client = new Client { Name = "Тестовый клиент", IsActive = true },
                Title = "Подготовка к заседанию",
                Status = ClientCaseStatus.Open,
                NextHearingAt = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc),
            };
            context.ClientCases.Add(clientCase);
            context.ClientCaseHearingPreparations.Add(new ClientCaseHearingPreparation
            {
                ClientCase = clientCase,
                HearingAt = clientCase.NextHearingAt.Value,
                ContractCheckedAt = DateTime.UtcNow,
                ContractCheckedByName = "Юрист",
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            var stored = await context.ClientCaseHearingPreparations.SingleAsync();
            Assert.Equal("Юрист", stored.ContractCheckedByName);
            Assert.Equal(clientCase.NextHearingAt.Value.Ticks, stored.HearingAt.Ticks);
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

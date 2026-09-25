using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CasePositionTemplateMigrationTests
{
    private const string SqlitePrevious = "20260925113000_ReleaseTasksFromCancelledInvoices";
    private const string SqliteCurrent = "20260925140000_AddCasePositionTemplates";
    private const string PostgresPrevious = "20260925113001_ReleaseTasksFromCancelledInvoicesPostgres";
    private const string PostgresCurrent = "20260925140001_AddCasePositionTemplatesPostgres";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteMigration_CreatesTemplatesOnCleanAndUpgradedDatabase(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        if (upgrade) await db.Database.MigrateAsync(SqlitePrevious);
        await db.Database.MigrateAsync();

        Assert.Contains(SqliteCurrent, await db.Database.GetAppliedMigrationsAsync());
        await VerifyStorageAsync(db);
    }

    [Fact]
    public void PostgreSqlMigrationScript_IsAfterPriorDataMigration()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test").Options;
        using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
        var script = db.GetService<IMigrator>().GenerateScript(PostgresPrevious, PostgresCurrent);
        Assert.Contains("CREATE TABLE \"CasePositionTemplates\"", script, StringComparison.Ordinal);
        Assert.Contains("OwnerUserId", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlMigration_CreatesTemplatesOnDisposableLocalDatabase(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(raw);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "pp_position_" + Guid.NewGuid().ToString("N");
        var adminConnection = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync();
            }

            var testConnection = new NpgsqlConnectionStringBuilder(raw) { Database = databaseName, Pooling = false };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testConnection.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            if (upgrade) await db.Database.MigrateAsync(PostgresPrevious);
            await db.Database.MigrateAsync();
            Assert.Contains(PostgresCurrent, await db.Database.GetAppliedMigrationsAsync());
            await VerifyStorageAsync(db);
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyStorageAsync(PaymentContext db)
    {
        var template = new CasePositionTemplate { Title = "Шаблон", Text = "Позиция", OwnerUserId = null };
        db.CasePositionTemplates.Add(template);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var loaded = await db.CasePositionTemplates.AsNoTracking().SingleAsync(item => item.Id == template.Id);
        Assert.Equal("Позиция", loaded.Text);
        Assert.Null(loaded.OwnerUserId);
    }
}

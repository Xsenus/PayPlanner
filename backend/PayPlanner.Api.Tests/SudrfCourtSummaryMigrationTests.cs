using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SudrfCourtSummaryMigrationTests
{
    private const string SqlitePreviousMigration = "20260924184930_AddCaseHearingPreparation";
    private const string SqliteMigration = "20260924222618_ReclassifySudrfCourtSummaries";
    private const string PostgresPreviousMigration = "20260924185041_AddCaseHearingPreparationPostgres";
    private const string PostgresMigration = "20260924222802_ReclassifySudrfCourtSummariesPostgres";

    [Fact]
    public async Task SqliteMigration_ReclassifiesOnlyGeneratedSummariesOnUpgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        await context.Database.MigrateAsync(SqlitePreviousMigration);
        // The test seeds the old schema through the current entity model. Remove this
        // temporary compatibility column before testing the actual upgrade path.
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientCases\" ADD COLUMN \"CourtRegisteredAt\" TEXT");

        var clientCase = new ClientCase
        {
            Client = new Client { Name = "Тестовый клиент", IsActive = true },
            Title = "Тестовое судебное дело",
            Status = ClientCaseStatus.Open,
        };
        context.ClientCases.Add(clientCase);
        await context.SaveChangesAsync();
        context.ClientCaseDocuments.AddRange(
            CreateDocument(clientCase.Id, "sudrf-act-generated", "sudrf"),
            CreateDocument(clientCase.Id, "manual-act", "manual"),
            CreateDocument(clientCase.Id, "sudrf-other", "sudrf"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientCases\" DROP COLUMN \"CourtRegisteredAt\"");

        await context.Database.MigrateAsync();

        Assert.Contains(SqliteMigration, await context.Database.GetAppliedMigrationsAsync());
        var documents = await context.ClientCaseDocuments
            .Where(item => item.ClientCaseId == clientCase.Id)
            .ToDictionaryAsync(item => item.DocumentKey);
        Assert.Equal(CourtSyncNormalization.DocumentTypes.CourtEventSummary, documents["sudrf-act-generated"].DocumentType);
        Assert.Equal(CourtSyncNormalization.DocumentTypes.CourtAct, documents["manual-act"].DocumentType);
        Assert.Equal(CourtSyncNormalization.DocumentTypes.CourtAct, documents["sudrf-other"].DocumentType);
    }

    [Fact]
    public async Task SqliteMigration_AppliesOnCleanDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);

        await context.Database.MigrateAsync();

        Assert.Contains(SqliteMigration, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void PostgreSqlMigration_UsesScopedDataUpdate()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql("Host=localhost;Database=payplanner_migration_script;Username=test;Password=test")
            .Options;
        using var context = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);

        var script = context.GetService<IMigrator>().GenerateScript(PostgresPreviousMigration, PostgresMigration);

        Assert.Contains("UPDATE \"ClientCaseDocuments\"", script, StringComparison.Ordinal);
        Assert.Contains("\"DocumentKey\" LIKE 'sudrf-act-%'", script, StringComparison.Ordinal);
        Assert.Contains("\"SourceType\" = 'sudrf'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE \"ClientCases\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlMigration_AppliesToDisposableLocalDatabase(bool upgrade)
    {
        var rawConnection = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(rawConnection)) return;
        var baseConnection = new NpgsqlConnectionStringBuilder(rawConnection);
        Assert.Contains(baseConnection.Host, new[] { "127.0.0.1", "localhost" });
        var databaseName = "payplanner_sudrf_summary_test_" + Guid.NewGuid().ToString("N");
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
            if (upgrade)
            {
                await context.Database.MigrateAsync(PostgresPreviousMigration);
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientCases\" ADD COLUMN \"CourtRegisteredAt\" timestamp without time zone");
                var clientCase = new ClientCase
                {
                    Client = new Client { Name = "Тестовый клиент", IsActive = true },
                    Title = "Тестовое судебное дело",
                    Status = ClientCaseStatus.Open,
                };
                context.ClientCases.Add(clientCase);
                await context.SaveChangesAsync();
                context.ClientCaseDocuments.AddRange(
                    CreateDocument(clientCase.Id, "sudrf-act-generated", "sudrf"),
                    CreateDocument(clientCase.Id, "manual-act", "manual"));
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"ClientCases\" DROP COLUMN \"CourtRegisteredAt\"");
            }

            await context.Database.MigrateAsync();
            Assert.Contains(PostgresMigration, await context.Database.GetAppliedMigrationsAsync());
            if (upgrade)
            {
                var documents = await context.ClientCaseDocuments.ToDictionaryAsync(item => item.DocumentKey);
                Assert.Equal(CourtSyncNormalization.DocumentTypes.CourtEventSummary, documents["sudrf-act-generated"].DocumentType);
                Assert.Equal(CourtSyncNormalization.DocumentTypes.CourtAct, documents["manual-act"].DocumentType);
            }
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, databaseName);
        }
    }

    private static ClientCaseDocument CreateDocument(int caseId, string key, string sourceType)
        => new()
        {
            ClientCaseId = caseId,
            DocumentKey = key,
            DocumentType = CourtSyncNormalization.DocumentTypes.CourtAct,
            Status = "received",
            OriginalFileName = key + ".html",
            StoredFileName = key + ".html",
            StoragePath = key + ".html",
            ContentType = "text/html",
            SourceType = sourceType,
        };
}

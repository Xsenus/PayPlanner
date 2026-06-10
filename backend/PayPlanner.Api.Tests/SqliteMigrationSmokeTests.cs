using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SqliteMigrationSmokeTests
{
    [Fact]
    public async Task SqliteMigrations_CanCreateFreshLocalDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "payplanner-sqlite-migrations");
        var databasePath = Path.Combine(directory, $"migration-smoke-{Guid.NewGuid():N}.db");

        Directory.CreateDirectory(directory);

        try
        {
            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={databasePath}")
                .UsePayPlannerSqliteMigrations()
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var context = new PaymentContext(options))
            {
                await context.Database.MigrateAsync();

                var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();

                Assert.Contains("20260521162000_AddClientCaseCourtDirectory", appliedMigrations);
                Assert.True(await context.Database.CanConnectAsync());

                _ = await context.Users.Take(1).ToListAsync();
                _ = await context.TaskJournalItems.Take(1).ToListAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(databasePath + "-wal");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

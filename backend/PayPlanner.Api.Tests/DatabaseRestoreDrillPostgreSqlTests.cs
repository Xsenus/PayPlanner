using Npgsql;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreDrillPostgreSqlTests
{
    [Fact]
    public async Task PostgreSqlDrill_AcceptsOnlyAnEmptyDisposableDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }

        var databaseName = "payplanner_restore_test_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var targetBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = databaseName };
            await DatabaseRestoreDrillService.EnsureEmptyPostgreSqlTargetAsync(targetBuilder.ConnectionString, CancellationToken.None);

            await using var target = new NpgsqlConnection(targetBuilder.ConnectionString);
            await target.OpenAsync();
            await using (var seed = target.CreateCommand())
            {
                seed.CommandText = "CREATE TABLE public.existing_data (id integer PRIMARY KEY); INSERT INTO public.existing_data VALUES (42);";
                await seed.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DatabaseRestoreDrillService.EnsureEmptyPostgreSqlTargetAsync(targetBuilder.ConnectionString, CancellationToken.None));
            await using var count = target.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM public.existing_data WHERE id = 42";
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var terminate = admin.CreateCommand();
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid()";
            terminate.Parameters.AddWithValue("databaseName", databaseName);
            await terminate.ExecuteNonQueryAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
        }
    }
}

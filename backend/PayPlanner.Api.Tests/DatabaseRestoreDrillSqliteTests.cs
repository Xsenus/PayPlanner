using Microsoft.Data.Sqlite;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreDrillSqliteTests
{
    [Fact]
    public async Task MissingSettingsTableCannotBeReportedAsIsolated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await Assert.ThrowsAsync<SqliteException>(() => DatabaseRestoreDrillService.SanitizeRestoredSqliteAsync(connection, CancellationToken.None));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailIsolationSupportsLegacyMissingQueueAndRollsBackOnInvalidQueue(bool invalidQueue)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE SystemSettings (Id INTEGER PRIMARY KEY, Key TEXT NOT NULL UNIQUE, Value TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            INSERT INTO SystemSettings (Key, Value, CreatedAt, UpdatedAt) VALUES ('notifications.email.enabled', 'true', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """;
        await command.ExecuteNonQueryAsync();
        if (invalidQueue)
        {
            command.CommandText = "CREATE TABLE NotificationDeliveries (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqliteException>(() => DatabaseRestoreDrillService.SanitizeRestoredSqliteAsync(connection, CancellationToken.None));
        }
        else
        {
            await DatabaseRestoreDrillService.SanitizeRestoredSqliteAsync(connection, CancellationToken.None);
            await DatabaseRestoreDrillService.SanitizeRestoredSqliteAsync(connection, CancellationToken.None);
        }
        command.CommandText = "SELECT Value FROM SystemSettings WHERE Key = 'notifications.email.enabled'";
        Assert.Equal(invalidQueue ? "true" : "false", await command.ExecuteScalarAsync());
    }
}

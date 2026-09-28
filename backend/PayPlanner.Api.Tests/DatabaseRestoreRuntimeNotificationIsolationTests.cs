using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseRestoreRuntimeNotificationIsolationTests
{
    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(false, true, "none")]
    [InlineData(true, true, "none")]
    [InlineData(false, true, "notifications")]
    [InlineData(true, true, "notifications")]
    [InlineData(false, true, "calendar")]
    [InlineData(true, true, "calendar")]
    public async Task RuntimeIsolationDisablesExternalChannelsAtomically(bool postgres, bool rollback, string failure)
    {
        var connectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (postgres && string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        await using DbConnection connection = postgres
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // Session-local tables shadow any real tables; no application host or workers run.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE "SystemSettings" (
                "Key" TEXT PRIMARY KEY, "Value" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
            CREATE TEMP TABLE "NotificationSenders" (
                "Id" INTEGER PRIMARY KEY, "Channel" TEXT NOT NULL, "IsEnabled" BOOLEAN NOT NULL,
                "ConfigJson" TEXT NOT NULL, "UpdatedAt" TEXT NULL);
            CREATE TEMP TABLE "NotificationDeliveries" (
                "Id" INTEGER PRIMARY KEY, "Channel" TEXT NOT NULL, "Status" TEXT NOT NULL);
            CREATE TEMP TABLE "MaxOutboxMessages" (
                "Id" INTEGER PRIMARY KEY, "Status" TEXT NOT NULL, "LastErrorCode" TEXT NULL, "UpdatedAt" TEXT NULL);
            CREATE TEMP TABLE "MessengerDocumentDeliveries" (
                "Id" INTEGER PRIMARY KEY, "Status" TEXT NOT NULL, "NextAttemptAt" TEXT NULL,
                "CancelledAt" TEXT NULL, "UpdatedAt" TEXT NULL, "LastError" TEXT NULL,
                "LinkTokenHash" TEXT NULL, "ProtectedLinkToken" TEXT NULL, "LinkExpiresAt" TEXT NULL);
            CREATE TEMP TABLE "ExternalCalendarTaskSyncRequests" (
                "Id" INTEGER PRIMARY KEY, "Status" TEXT NOT NULL, "UpdatedAt" TEXT NULL);
            INSERT INTO "SystemSettings" VALUES
                ('notifications.telegram.enabled', 'true', 'before', 'before'),
                ('notifications.telegram.botToken', 'fixture-token', 'before', 'before'),
                ('auth.telegram.botToken', 'fixture-token', 'before', 'before'),
                ('notifications.max.botToken', 'fixture-token', 'before', 'before'),
                ('notifications.max.webhookSecret', 'fixture-token', 'before', 'before'),
                ('notifications.sms.apiToken', 'fixture-token', 'before', 'before'),
                ('unrelated.setting', 'preserved', 'before', 'before');
            INSERT INTO "NotificationSenders" VALUES
                (1, 'email', TRUE, '{"fixture":true}', 'before'),
                (2, 'telegram', TRUE, '{"fixture":true}', 'before'),
                (3, 'max', TRUE, '{"fixture":true}', 'before'),
                (4, 'sms', TRUE, '{"fixture":true}', 'before');
            INSERT INTO "NotificationDeliveries" VALUES
                (1, 'email', 'pending'), (2, 'telegram', 'failed'),
                (3, 'max', 'pending'), (4, 'sms', 'failed'),
                (5, 'max', 'sent'), (6, 'inapp', 'pending');
            INSERT INTO "MaxOutboxMessages" ("Id", "Status") VALUES
                (1, 'queued'), (2, 'processing'), (3, 'failed'), (4, 'sent'), (5, 'cancelled');
            INSERT INTO "MessengerDocumentDeliveries" ("Id", "Status", "NextAttemptAt", "LinkTokenHash", "ProtectedLinkToken", "LinkExpiresAt") VALUES
                (1, 'prepared', 'before', 'fixture-hash', 'fixture-token', 'before'),
                (2, 'queued', 'before', 'fixture-hash', 'fixture-token', 'before'),
                (3, 'processing', 'before', 'fixture-hash', 'fixture-token', 'before'),
                (4, 'failed', 'before', 'fixture-hash', 'fixture-token', 'before'),
                (5, 'sent', NULL, 'fixture-hash', 'fixture-token', 'before'),
                (6, 'delivered', NULL, 'fixture-hash', 'fixture-token', 'before'),
                (7, 'read', NULL, 'fixture-hash', 'fixture-token', 'before'),
                (8, 'cancelled', NULL, NULL, NULL, NULL), (9, 'expired', NULL, NULL, NULL, NULL);
            INSERT INTO "ExternalCalendarTaskSyncRequests" ("Id", "Status") VALUES (1, 'queued'), (2, 'failed');
            """;
        if (postgres)
            command.CommandText = command.CommandText.Replace("\"CancelledAt\" TEXT", "\"CancelledAt\" TIMESTAMP", StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync();
        if (failure != "notifications")
        {
            command.CommandText = "ALTER TABLE \"NotificationDeliveries\" ADD COLUMN \"Error\" TEXT NULL;";
            await command.ExecuteNonQueryAsync();
        }
        if (failure != "calendar")
        {
            command.CommandText = "ALTER TABLE \"ExternalCalendarTaskSyncRequests\" ADD COLUMN \"LastError\" TEXT NULL;";
            await command.ExecuteNonQueryAsync();
        }
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            if (failure != "none")
                await Assert.ThrowsAnyAsync<DbException>(() => DatabaseRestoreDrillService.IsolateRuntimeNotificationsAsync(
                    connection, transaction, CancellationToken.None));
            else
            {
                await DatabaseRestoreDrillService.IsolateRuntimeNotificationsAsync(connection, transaction, CancellationToken.None);
                await DatabaseRestoreDrillService.IsolateRuntimeNotificationsAsync(connection, transaction, CancellationToken.None);
            }
            if (rollback) await transaction.RollbackAsync();
            else await transaction.CommitAsync();
        }
        command.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = 'notifications.telegram.enabled';";
        Assert.Equal(rollback ? "true" : "false", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT \"Value\" FROM \"SystemSettings\" WHERE \"Key\" = 'unrelated.setting';";
        Assert.Equal("preserved", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM \"SystemSettings\" WHERE \"Value\" = 'fixture-token';";
        Assert.Equal(rollback ? 5 : 0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM \"NotificationSenders\" WHERE \"IsEnabled\" = TRUE;";
        Assert.Equal(rollback ? 4 : 0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT \"Status\" FROM \"NotificationDeliveries\" ORDER BY \"Id\";";
        Assert.Equal(rollback
            ? ["pending", "failed", "pending", "failed", "sent", "pending"]
            : new[] { "skipped", "skipped", "skipped", "skipped", "sent", "pending" }, await ReadStatusesAsync());
        command.CommandText = "SELECT \"Status\" FROM \"MaxOutboxMessages\" ORDER BY \"Id\";";
        Assert.Equal(rollback ? ["queued", "processing", "failed", "sent", "cancelled"]
            : new[] { "cancelled", "cancelled", "cancelled", "sent", "cancelled" }, await ReadStatusesAsync());
        command.CommandText = "SELECT \"Status\" FROM \"MessengerDocumentDeliveries\" ORDER BY \"Id\";";
        Assert.Equal(rollback ? ["prepared", "queued", "processing", "failed", "sent", "delivered", "read", "cancelled", "expired"]
            : new[] { "cancelled", "cancelled", "cancelled", "cancelled", "sent", "delivered", "read", "cancelled", "expired" }, await ReadStatusesAsync());
        command.CommandText = "SELECT COUNT(*) FROM \"MessengerDocumentDeliveries\" WHERE \"LinkTokenHash\" IS NOT NULL OR \"ProtectedLinkToken\" IS NOT NULL OR \"LinkExpiresAt\" IS NOT NULL;";
        Assert.Equal(rollback ? 7 : 0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM \"MessengerDocumentDeliveries\" WHERE \"CancelledAt\" IS NOT NULL AND \"NextAttemptAt\" IS NULL;";
        Assert.Equal(rollback ? 0 : 4, Convert.ToInt32(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT \"Status\" FROM \"ExternalCalendarTaskSyncRequests\" ORDER BY \"Id\";";
        Assert.Equal(rollback ? ["queued", "failed"] : new[] { "failed", "failed" }, await ReadStatusesAsync());

        async Task<List<string>> ReadStatusesAsync()
        {
            await using var reader = await command.ExecuteReaderAsync();
            var statuses = new List<string>();
            while (await reader.ReadAsync()) statuses.Add(reader.GetString(0));
            return statuses;
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;
using PostgreSqlIndexMigration =
    PayPlanner.Api.Migrations.PostgreSql.AddNotificationDeliveryChannelCreatedAtIndexPostgres;
using SqliteIndexMigration =
    PayPlanner.Api.Migrations.AddNotificationDeliveryChannelCreatedAtIndex;

namespace PayPlanner.Api.Tests;

public sealed class NotificationDeliveryMigrationTests
{
    private const string PreviousMigration = "20260729133312_AddCourtSyncProvenance";
    private const string DeliveryIndexMigration = "20260729230733_AddNotificationDeliveryChannelCreatedAtIndex";

    [Fact]
    public void ProviderMigrations_CreateEquivalentChannelCreatedAtIndex()
    {
        AssertIndexOperation(new TestableSqliteMigration().GetUpOperations("Microsoft.EntityFrameworkCore.Sqlite"));
        AssertIndexOperation(new TestablePostgreSqlMigration().GetUpOperations("Npgsql.EntityFrameworkCore.PostgreSQL"));
    }

    [Fact]
    public async Task LatestMigration_PreservesExistingDeliveriesAndAddsLookupIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "payplanner-notification-delivery-migrations");
        var databasePath = Path.Combine(directory, $"delivery-compatibility-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(connection)
                .UsePayPlannerSqliteMigrations()
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using (var legacyContext = new PaymentContext(options))
            {
                await legacyContext.Database.MigrateAsync(PreviousMigration);
                legacyContext.NotificationDeliveries.AddRange(
                    CreateDelivery(
                        "legacy-email",
                        NotificationChannels.Email,
                        NotificationDeliveryStatus.Sent,
                        "client@example.com",
                        new DateTime(2026, 7, 28, 7, 0, 0, DateTimeKind.Utc)),
                    CreateDelivery(
                        "legacy-web-push",
                        NotificationChannels.WebPush,
                        NotificationDeliveryStatus.Failed,
                        "https://push.example/legacy",
                        new DateTime(2026, 7, 28, 8, 0, 0, DateTimeKind.Utc),
                        "legacy-safe-error"),
                    CreateDelivery(
                        "legacy-sms",
                        NotificationChannels.Sms,
                        NotificationDeliveryStatus.Skipped,
                        "+79990000000",
                        new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc),
                        "provider-not-configured"));
                await legacyContext.SaveChangesAsync();

                var appliedMigrations = await legacyContext.Database.GetAppliedMigrationsAsync();
                Assert.DoesNotContain(DeliveryIndexMigration, appliedMigrations);
            }

            await using (var upgradedContext = new PaymentContext(options))
            {
                await upgradedContext.Database.MigrateAsync();

                var appliedMigrations = await upgradedContext.Database.GetAppliedMigrationsAsync();
                Assert.Contains(DeliveryIndexMigration, appliedMigrations);

                var deliveries = await upgradedContext.NotificationDeliveries
                    .AsNoTracking()
                    .OrderBy(item => item.CreatedAt)
                    .ToListAsync();

                Assert.Collection(
                    deliveries,
                    item => AssertDelivery(
                        item,
                        "legacy-email",
                        NotificationChannels.Email,
                        NotificationDeliveryStatus.Sent,
                        "client@example.com",
                        null),
                    item => AssertDelivery(
                        item,
                        "legacy-web-push",
                        NotificationChannels.WebPush,
                        NotificationDeliveryStatus.Failed,
                        "https://push.example/legacy",
                        "legacy-safe-error"),
                    item => AssertDelivery(
                        item,
                        "legacy-sms",
                        NotificationChannels.Sms,
                        NotificationDeliveryStatus.Skipped,
                        "+79990000000",
                        "provider-not-configured"));
            }

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_index_list('NotificationDeliveries')
                WHERE name = 'IX_NotificationDeliveries_Channel_CreatedAt';
                """;
            Assert.Equal(1L, (long)(await indexCommand.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(databasePath);
            DeleteIfExists(databasePath + "-shm");
            DeleteIfExists(databasePath + "-wal");
        }
    }

    private static NotificationDelivery CreateDelivery(
        string eventKey,
        string channel,
        string status,
        string recipientAddress,
        DateTime createdAt,
        string? error = null)
        => new()
        {
            EventKey = eventKey,
            Channel = channel,
            SenderName = "Legacy sender",
            RecipientAddress = recipientAddress,
            Subject = "Legacy subject",
            BodyPreview = "Legacy preview",
            Status = status,
            Error = error,
            CreatedAt = createdAt,
            SentAt = status == NotificationDeliveryStatus.Sent ? createdAt.AddMinutes(1) : null,
        };

    private static void AssertDelivery(
        NotificationDelivery delivery,
        string eventKey,
        string channel,
        string status,
        string recipientAddress,
        string? error)
    {
        Assert.Equal(eventKey, delivery.EventKey);
        Assert.Equal(channel, delivery.Channel);
        Assert.Equal(status, delivery.Status);
        Assert.Equal(recipientAddress, delivery.RecipientAddress);
        Assert.Equal("Legacy subject", delivery.Subject);
        Assert.Equal("Legacy preview", delivery.BodyPreview);
        Assert.Equal(error, delivery.Error);
    }

    private static void AssertIndexOperation(IReadOnlyList<MigrationOperation> operations)
    {
        var operation = Assert.IsType<CreateIndexOperation>(Assert.Single(operations));
        Assert.Equal("IX_NotificationDeliveries_Channel_CreatedAt", operation.Name);
        Assert.Equal("NotificationDeliveries", operation.Table);
        Assert.Equal(["Channel", "CreatedAt"], operation.Columns);
        Assert.False(operation.IsUnique);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class TestableSqliteMigration : SqliteIndexMigration
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations(string activeProvider)
        {
            var builder = new MigrationBuilder(activeProvider);
            Up(builder);
            return builder.Operations;
        }
    }

    private sealed class TestablePostgreSqlMigration : PostgreSqlIndexMigration
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations(string activeProvider)
        {
            var builder = new MigrationBuilder(activeProvider);
            Up(builder);
            return builder.Operations;
        }
    }
}

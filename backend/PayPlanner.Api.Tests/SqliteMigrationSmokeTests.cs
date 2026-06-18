using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
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

                var user = new User
                {
                    Email = "sqlite-push-smoke@example.com",
                    FullName = "SQLite Push Smoke",
                    PasswordHash = "hash",
                    Role = new Role
                    {
                        Name = $"sqlite-push-smoke-{Guid.NewGuid():N}",
                        Description = "SQLite push smoke role",
                        CreatedAt = DateTime.UtcNow,
                    },
                    IsActive = true,
                    IsApproved = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                var notification = new UserNotification
                {
                    RecipientUser = user,
                    Type = UserNotificationTypes.TaskAssigned,
                    EventKey = $"sqlite-push-smoke:{Guid.NewGuid():N}",
                    Title = "Новая задача",
                    BodyPreview = "Проверка локальной БД для push-уведомлений.",
                    Tone = "blue",
                    ObjectType = "task",
                    MetadataJson = "{}",
                };
                context.UserPushSubscriptions.Add(new UserPushSubscription
                {
                    User = user,
                    Endpoint = $"https://push.example/sqlite-smoke/{Guid.NewGuid():N}",
                    P256dh = "p256dh",
                    Auth = "auth",
                    Browser = "Edge",
                    Platform = "Windows",
                    DeviceName = "Local smoke",
                    UserAgent = "PayPlanner SQLite smoke",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                });
                context.UserNotifications.Add(notification);
                context.NotificationDeliveries.Add(new NotificationDelivery
                {
                    EventKey = notification.EventKey,
                    Channel = NotificationChannels.WebPush,
                    SenderName = "Web Push",
                    RecipientUser = user,
                    RecipientAddress = "https://push.example/sqlite-smoke",
                    Subject = notification.Title,
                    BodyPreview = notification.BodyPreview,
                    Status = NotificationDeliveryStatus.Sent,
                    CreatedAt = DateTime.UtcNow,
                    SentAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }

            await using (var verificationContext = new PaymentContext(options))
            {
                var storedSubscription = await verificationContext.UserPushSubscriptions.SingleAsync();
                var storedNotification = await verificationContext.UserNotifications.SingleAsync();
                var storedDelivery = await verificationContext.NotificationDeliveries.SingleAsync();

                Assert.True(storedSubscription.IsActive);
                Assert.Equal("Edge", storedSubscription.Browser);
                Assert.Equal(UserNotificationTypes.TaskAssigned, storedNotification.Type);
                Assert.Equal(NotificationChannels.WebPush, storedDelivery.Channel);
                Assert.Equal(NotificationDeliveryStatus.Sent, storedDelivery.Status);
                Assert.Equal(storedNotification.EventKey, storedDelivery.EventKey);
                Assert.Equal(storedNotification.RecipientUserId, storedDelivery.RecipientUserId);
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

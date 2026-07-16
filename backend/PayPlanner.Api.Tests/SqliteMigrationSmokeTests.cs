using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
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
                Assert.Contains("20260716173515_AddCalendarSyncLogCreatedAtIndexes", appliedMigrations);
                Assert.True(await context.Database.CanConnectAsync());

                _ = await context.Users.Take(1).ToListAsync();
                _ = await context.TaskJournalItems.Take(1).ToListAsync();

                var adminRole = await context.Roles.SingleOrDefaultAsync(role => role.Name == "admin")
                    ?? new Role
                    {
                        Name = "admin",
                        Description = "Администратор",
                        CreatedAt = DateTime.UtcNow,
                    };
                if (adminRole.Id == 0)
                {
                    context.Roles.Add(adminRole);
                }

                var user = new User
                {
                    Email = "sqlite-push-smoke@example.com",
                    FullName = "SQLite Push Smoke",
                    PasswordHash = "hash",
                    Role = adminRole,
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

                var bankController = CreateBanksController(context, user.Id);
                var bankAction = await bankController.CreateBank(
                    new BankUpsertRequest
                    {
                        Name = "Локальный банк",
                        Bik = "044525104",
                        CorrespondentAccount = "30101810745374525104",
                        City = "Самара",
                        IsForeign = false,
                    },
                    CancellationToken.None);
                var bankCreated = Assert.IsType<CreatedAtActionResult>(bankAction.Result);
                var bank = Assert.IsType<BankDto>(bankCreated.Value);
                Assert.Equal("Локальный банк", bank.Name);

                var cashController = CreateCashOrdersController(context, user.Id);
                var orderAction = await cashController.Create(new CashOrder
                {
                    Type = CashOrderType.Incoming,
                    Status = CashOrderStatus.Posted,
                    Date = new DateTime(2026, 6, 24),
                    Amount = 2_500m,
                    Currency = "RUB",
                    Account = "Основная касса",
                    PayerName = "Тестовый клиент",
                    Basis = "Локальная проверка кассы",
                }, CancellationToken.None);
                var orderCreated = Assert.IsType<CreatedResult>(orderAction);
                var order = Assert.IsType<CashOrderDto>(orderCreated.Value);
                Assert.StartsWith("ПКО-2026-", order.Number);

                var cashBookAction = await cashController.GetCashBook(
                    new DateTime(2026, 6, 24),
                    new DateTime(2026, 6, 24),
                    "Основная касса",
                    ct: CancellationToken.None);
                var cashBook = Assert.IsType<CashBookResponseDto>(
                    Assert.IsType<OkObjectResult>(cashBookAction.Result).Value);
                Assert.Equal(2_500m, cashBook.Income);
                Assert.Equal(2_500m, cashBook.ClosingBalance);

                var inventoryAction = await cashController.CreateInventory(
                    new CashInventoryCreateRequest
                    {
                        Date = new DateTime(2026, 6, 24),
                        AccountName = "Основная касса",
                        ActualBalance = 2_500m,
                        CreateCorrection = false,
                        Comment = "Локальная SQLite-проверка",
                    },
                    CancellationToken.None);
                var inventoryCreated = Assert.IsType<CreatedResult>(inventoryAction);
                var inventory = Assert.IsType<CashInventoryDto>(inventoryCreated.Value);
                Assert.StartsWith("ИНВК-2026-", inventory.Number);
            }

            await using (var verificationContext = new PaymentContext(options))
            {
                var storedSubscription = await verificationContext.UserPushSubscriptions.SingleAsync();
                var storedNotification = await verificationContext.UserNotifications.SingleAsync();
                var storedDelivery = await verificationContext.NotificationDeliveries.SingleAsync();
                var storedBank = await verificationContext.Banks.SingleAsync();
                var storedCashOrder = await verificationContext.CashOrders.SingleAsync();
                var storedInventory = await verificationContext.CashInventories.SingleAsync();

                Assert.True(storedSubscription.IsActive);
                Assert.Equal("Edge", storedSubscription.Browser);
                Assert.Equal(UserNotificationTypes.TaskAssigned, storedNotification.Type);
                Assert.Equal(NotificationChannels.WebPush, storedDelivery.Channel);
                Assert.Equal(NotificationDeliveryStatus.Sent, storedDelivery.Status);
                Assert.Equal(storedNotification.EventKey, storedDelivery.EventKey);
                Assert.Equal(storedNotification.RecipientUserId, storedDelivery.RecipientUserId);
                Assert.Equal("Локальный банк", storedBank.Name);
                Assert.Equal(2_500m, storedCashOrder.Amount);
                Assert.Equal(2_500m, storedInventory.ActualBalance);
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

    private static BanksController CreateBanksController(PaymentContext context, int userId)
        => new(context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateAdminPrincipal(userId),
                },
            },
        };

    private static CashOrdersController CreateCashOrdersController(PaymentContext context, int userId)
        => new(context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateAdminPrincipal(userId),
                },
            },
        };

    private static ClaimsPrincipal CreateAdminPrincipal(int userId)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, "admin"),
        ],
        authenticationType: "Test"));

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

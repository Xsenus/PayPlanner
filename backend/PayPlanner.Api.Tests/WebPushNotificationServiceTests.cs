using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class WebPushNotificationServiceTests
{
    [Fact]
    public async Task SendUserNotificationAsync_SanitizesFinancialPayloadInDeliveryLogWhenServerIsNotReady()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "finance-push@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.PaymentAdvanceReviewRequired,
            EventKey = "payment:42:advance-review",
            Title = "Проверьте платеж 125 000 ₽",
            BodyPreview = "Платеж от Иванов Иван на сумму 125 000 ₽ требует решения по авансу.",
            Tone = "rose",
            ObjectType = "payment",
            ObjectId = 42,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        await service.SendUserNotificationAsync(notification);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationChannels.WebPush, delivery.Channel);
        Assert.Equal("Финансовое уведомление PayPlanner", delivery.Subject);
        Assert.Equal("Откройте CRM, чтобы посмотреть детали финансового события после проверки прав доступа.", delivery.BodyPreview);
        Assert.DoesNotContain("125 000", delivery.Subject + delivery.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("Иванов", delivery.Subject + delivery.BodyPreview, StringComparison.Ordinal);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
    }

    [Theory]
    [InlineData(UserNotificationTypes.PaymentAssigned, "payment")]
    [InlineData(UserNotificationTypes.InvoiceChanged, "invoice")]
    [InlineData(UserNotificationTypes.ActChanged, "act")]
    [InlineData(UserNotificationTypes.ContractChanged, "contract")]
    public async Task SendUserNotificationAsync_SanitizesFinancialObjectTypesInDeliveryLogWhenServerIsNotReady(
        string notificationType,
        string objectType)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, $"finance-{objectType}-push@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = notificationType,
            EventKey = $"{objectType}:42:sensitive",
            Title = "Изменение по Иванову на 125 000 ₽",
            BodyPreview = "Иванов Иван: сумма 125 000 ₽, счет №77, договор D-77.",
            Tone = "amber",
            ObjectType = objectType,
            ObjectId = 42,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        await service.SendUserNotificationAsync(notification);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal("Финансовое уведомление PayPlanner", delivery.Subject);
        Assert.Equal("Откройте CRM, чтобы посмотреть детали финансового события после проверки прав доступа.", delivery.BodyPreview);
        Assert.DoesNotContain("125 000", delivery.Subject + delivery.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("Иванов", delivery.Subject + delivery.BodyPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("№77", delivery.Subject + delivery.BodyPreview, StringComparison.Ordinal);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
    }

    [Fact]
    public async Task SendUserNotificationAsync_KeepsTaskPayloadPreviewInDeliveryLogWhenServerIsNotReady()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "task-push@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskAssigned,
            EventKey = "task:77:assigned",
            Title = "Новая задача: подготовить документы",
            BodyPreview = "Подготовьте документы по делу клиента.",
            Tone = "blue",
            ObjectType = "task",
            ObjectId = 77,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        await service.SendUserNotificationAsync(notification);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal("Новая задача: подготовить документы", delivery.Subject);
        Assert.Equal("Подготовьте документы по делу клиента.", delivery.BodyPreview);
        Assert.Equal(NotificationDeliveryStatus.Skipped, delivery.Status);
    }

    [Fact]
    public async Task SendUserNotificationAsync_PersistsPendingOutboxBeforeCallingPushProvider()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "outbox-push@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskAssigned,
            EventKey = "task:88:assigned",
            Title = "Новая задача",
            BodyPreview = "Проверьте задачу.",
            Tone = "blue",
            ObjectType = "task",
            ObjectId = 88,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = new FailingWebPushNotificationService(db, notification.EventKey);

        await service.SendUserNotificationAsync(notification);

        Assert.True(service.PendingDeliveryWasPersistedBeforeSend);
        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("Simulated push provider failure", delivery.Error, StringComparison.Ordinal);
    }

    private static WebPushNotificationService CreateService(PaymentContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebPush:Enabled"] = "true",
                ["WebPush:PublicKey"] = "",
                ["WebPush:PrivateKey"] = "",
                ["WebPush:Subject"] = "",
            })
            .Build();

        return new WebPushNotificationService(
            db,
            configuration,
            NullLogger<WebPushNotificationService>.Instance);
    }

    private static async Task<User> CreateUserWithSubscriptionAsync(PaymentContext db, string email)
    {
        var role = new Role
        {
            Name = $"role-{Guid.NewGuid():N}",
            Description = "Test role",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Email = email,
            FullName = "Test User",
            PasswordHash = "hash",
            Role = role,
            IsActive = true,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserPushSubscriptions.Add(new UserPushSubscription
        {
            UserId = user.Id,
            Endpoint = $"https://push.example/{Guid.NewGuid():N}",
            P256dh = "p256dh",
            Auth = "auth",
            Browser = "Chrome",
            Platform = "Windows",
            DeviceName = "Chrome Windows",
            UserAgent = "Test",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static UserNotification CreateNotification(int userId, UserNotification notification)
    {
        notification.RecipientUserId = userId;
        notification.CreatedAt = DateTime.UtcNow;
        notification.MetadataJson = "{}";
        return notification;
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class FailingWebPushNotificationService : WebPushNotificationService
    {
        private readonly PaymentContext _db;
        private readonly string _eventKey;

        public FailingWebPushNotificationService(PaymentContext db, string eventKey)
            : base(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WebPush:Enabled"] = "true",
                        ["WebPush:PublicKey"] = "public-key",
                        ["WebPush:PrivateKey"] = "private-key",
                        ["WebPush:Subject"] = "mailto:test@example.com",
                    })
                    .Build(),
                NullLogger<WebPushNotificationService>.Instance)
        {
            _db = db;
            _eventKey = eventKey;
        }

        public bool PendingDeliveryWasPersistedBeforeSend { get; private set; }

        protected override async Task SendSubscriptionAsync(
            UserPushSubscription subscription,
            string payload,
            string subject,
            string publicKey,
            string privateKey,
            CancellationToken cancellationToken)
        {
            PendingDeliveryWasPersistedBeforeSend = await _db.NotificationDeliveries
                .AsNoTracking()
                .AnyAsync(item =>
                    item.EventKey == _eventKey
                    && item.Channel == NotificationChannels.WebPush
                    && item.Status == NotificationDeliveryStatus.Pending,
                    cancellationToken);

            throw new InvalidOperationException("Simulated push provider failure");
        }
    }
}

using System.Text.Json;
using System.Net;
using System.Text;
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
        using var payload = JsonDocument.Parse(service.CapturedPayload);
        Assert.Equal(NotificationPriorities.High, payload.RootElement.GetProperty("priority").GetString());
        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("Simulated push provider failure", delivery.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendUserNotificationAsync_TruncatesLargePayloadBeforeCallingPushProvider()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "large-payload-push@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskChanged,
            EventKey = "task:91:large-payload",
            Title = new string('Т', 600),
            BodyPreview = new string('Д', 6000),
            Tone = "amber",
            ObjectType = "task",
            ObjectId = 91,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = new FailingWebPushNotificationService(db, notification.EventKey);

        await service.SendUserNotificationAsync(notification);

        Assert.True(Encoding.UTF8.GetByteCount(service.CapturedPayload) <= 3800);
        using var payload = JsonDocument.Parse(service.CapturedPayload);
        Assert.EndsWith("...", payload.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UserNotificationTypes.TaskAssigned, "task", 88, null, "{}", "/tasks/88")]
    [InlineData(UserNotificationTypes.PaymentAssigned, "payment", 42, null, "{}", "/payments/42")]
    [InlineData(UserNotificationTypes.InvoiceChanged, "invoice", 43, null, "{}", "/invoices/43")]
    [InlineData(UserNotificationTypes.ActChanged, "act", 44, null, "{}", "/acts/44")]
    [InlineData(UserNotificationTypes.ContractChanged, "contract", 45, null, "{}", "/contracts/45")]
    [InlineData(UserNotificationTypes.CaseInactivityReminder, "case", 46, null, "{}", "/cases/46")]
    [InlineData(UserNotificationTypes.CaseDocument, "case", 47, null, "{\"clientCaseDocumentId\":901}", "/cases/47?ppCaseTab=documents")]
    [InlineData(UserNotificationTypes.CourtHearing, "case", 48, null, "{\"courtEventId\":902}", "/cases/48?ppCaseTab=court")]
    public async Task SendUserNotificationAsync_PayloadContainsDirectCrmUrl(
        string notificationType,
        string objectType,
        int objectId,
        int? taskJournalItemId,
        string metadataJson,
        string expectedUrl)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, $"push-url-{objectId}@example.com");
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = notificationType,
            EventKey = $"{objectType}:{objectId}:url",
            Title = "Новое уведомление",
            BodyPreview = "Откройте связанный объект.",
            Tone = "blue",
            ObjectType = objectType,
            ObjectId = objectId,
            TaskJournalItemId = taskJournalItemId,
            MetadataJson = metadataJson,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = new FailingWebPushNotificationService(db, notification.EventKey);

        await service.SendUserNotificationAsync(notification);

        using var payload = JsonDocument.Parse(service.CapturedPayload);
        Assert.Equal(expectedUrl, payload.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task SendUserNotificationAsync_DeactivatesDeadSubscriptionWhenPushProviderReturnsGone()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "dead-subscription-push@example.com");
        var subscription = await db.UserPushSubscriptions.SingleAsync(item => item.UserId == user.Id);
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskAssigned,
            EventKey = "task:89:assigned",
            Title = "Новая задача",
            BodyPreview = "Проверьте задачу.",
            Tone = "blue",
            ObjectType = "task",
            ObjectId = 89,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = new DeadSubscriptionWebPushNotificationService(db, HttpStatusCode.Gone);

        await service.SendUserNotificationAsync(notification);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("Push subscription is gone", delivery.Error, StringComparison.Ordinal);
        await db.Entry(subscription).ReloadAsync();
        Assert.False(subscription.IsActive);
        Assert.NotNull(subscription.RevokedAt);
        Assert.NotNull(subscription.LastErrorAt);
        Assert.Contains("Push subscription is gone", subscription.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendUserNotificationAsync_DeactivatesSubscriptionWhenVapidCredentialsDoNotMatch()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "vapid-mismatch-push@example.com");
        var subscription = await db.UserPushSubscriptions.SingleAsync(item => item.UserId == user.Id);
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskReminder,
            EventKey = "task:90:reminder",
            Title = "Просроченная задача",
            BodyPreview = "Проверьте задачу.",
            Tone = "red",
            ObjectType = "task",
            ObjectId = 90,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        var service = new DeadSubscriptionWebPushNotificationService(
            db,
            HttpStatusCode.Forbidden,
            "Received unexpected response code: 403. Details: the VAPID credentials in the authorization header do not correspond to the credentials used to create the subscriptions.");

        await service.SendUserNotificationAsync(notification);

        var delivery = await db.NotificationDeliveries.SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Contains("VAPID credentials", delivery.Error, StringComparison.Ordinal);
        await db.Entry(subscription).ReloadAsync();
        Assert.False(subscription.IsActive);
        Assert.NotNull(subscription.RevokedAt);
        Assert.NotNull(subscription.LastErrorAt);
        Assert.Contains("VAPID credentials", subscription.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryDeliveryAsync_CreatesPendingOutboxBeforeCallingPushProvider()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "retry-push@example.com");
        var subscription = await db.UserPushSubscriptions.SingleAsync(item => item.UserId == user.Id);
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskChanged,
            EventKey = "task:99:changed",
            Title = "Задача изменена",
            BodyPreview = "Проверьте изменения в задаче.",
            Tone = "amber",
            ObjectType = "task",
            ObjectId = 99,
        });
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync();

        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = notification.EventKey,
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = subscription.Endpoint,
            Subject = notification.Title,
            BodyPreview = notification.BodyPreview,
            Status = NotificationDeliveryStatus.Failed,
            Error = "Temporary push provider failure",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        });
        await db.SaveChangesAsync();
        var original = await db.NotificationDeliveries.SingleAsync();
        var service = new SuccessfulWebPushNotificationService(db, original.EventKey);

        var retry = await service.RetryDeliveryAsync(original.Id);

        Assert.NotNull(retry);
        Assert.True(service.PendingRetryWasPersistedBeforeSend);
        Assert.NotEqual(original.Id, retry.Id);
        Assert.Equal(NotificationDeliveryStatus.Sent, retry.Status);
        Assert.StartsWith($"{original.EventKey}:retry:", retry.EventKey, StringComparison.Ordinal);
        await db.Entry(original).ReloadAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, original.Status);
        await db.Entry(subscription).ReloadAsync();
        Assert.Null(subscription.LastError);
        Assert.Null(subscription.LastErrorAt);
    }

    [Fact]
    public async Task RetryDeliveryAsync_SkipsInactiveSubscription()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserWithSubscriptionAsync(db, "inactive-retry-push@example.com");
        var subscription = await db.UserPushSubscriptions.SingleAsync(item => item.UserId == user.Id);
        subscription.IsActive = false;
        subscription.RevokedAt = DateTime.UtcNow;
        var notification = CreateNotification(user.Id, new UserNotification
        {
            Type = UserNotificationTypes.TaskChanged,
            EventKey = "task:100:changed",
            Title = "Задача изменена",
            BodyPreview = "Проверьте изменения в задаче.",
            Tone = "amber",
            ObjectType = "task",
            ObjectId = 100,
        });
        db.UserNotifications.Add(notification);
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            EventKey = notification.EventKey,
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = user.Id,
            RecipientAddress = subscription.Endpoint,
            Subject = notification.Title,
            BodyPreview = notification.BodyPreview,
            Status = NotificationDeliveryStatus.Failed,
            Error = "Subscription gone",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        });
        await db.SaveChangesAsync();
        var original = await db.NotificationDeliveries.SingleAsync();
        var service = new SuccessfulWebPushNotificationService(db, original.EventKey);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryDeliveryAsync(original.Id));

        Assert.Equal("WebPushSubscriptionInactive", ex.Message);
        Assert.Equal(1, await db.NotificationDeliveries.CountAsync());
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
        public string CapturedPayload { get; private set; } = "{}";

        protected override async Task SendSubscriptionAsync(
            UserPushSubscription subscription,
            string payload,
            string subject,
            string publicKey,
            string privateKey,
            CancellationToken cancellationToken)
        {
            CapturedPayload = payload;
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

    private sealed class SuccessfulWebPushNotificationService : WebPushNotificationService
    {
        private readonly PaymentContext _db;
        private readonly string _eventKeyPrefix;

        public SuccessfulWebPushNotificationService(PaymentContext db, string eventKeyPrefix)
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
            _eventKeyPrefix = eventKeyPrefix;
        }

        public bool PendingRetryWasPersistedBeforeSend { get; private set; }

        protected override async Task SendSubscriptionAsync(
            UserPushSubscription subscription,
            string payload,
            string subject,
            string publicKey,
            string privateKey,
            CancellationToken cancellationToken)
        {
            PendingRetryWasPersistedBeforeSend = await _db.NotificationDeliveries
                .AsNoTracking()
                .AnyAsync(item =>
                    item.EventKey.StartsWith($"{_eventKeyPrefix}:retry:")
                    && item.Channel == NotificationChannels.WebPush
                    && item.Status == NotificationDeliveryStatus.Pending,
                    cancellationToken);
        }
    }

    private sealed class DeadSubscriptionWebPushNotificationService : WebPushNotificationService
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _errorMessage;

        public DeadSubscriptionWebPushNotificationService(
            PaymentContext db,
            HttpStatusCode statusCode,
            string errorMessage = "Push subscription is gone")
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
            _statusCode = statusCode;
            _errorMessage = errorMessage;
        }

        protected override Task SendSubscriptionAsync(
            UserPushSubscription subscription,
            string payload,
            string subject,
            string publicKey,
            string privateKey,
            CancellationToken cancellationToken)
        {
            var pushSubscription = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            var response = new HttpResponseMessage(_statusCode);
            throw new WebPush.WebPushException(_errorMessage, pushSubscription, response);
        }
    }
}

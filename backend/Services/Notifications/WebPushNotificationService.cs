using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using WebPush;

namespace PayPlanner.Api.Services.Notifications;

public sealed record WebPushPublicKeyStatus(
    bool Enabled,
    bool Ready,
    string PublicKey,
    bool PublicKeyConfigured,
    bool PrivateKeyConfigured,
    bool SubjectConfigured);

public interface IWebPushNotificationService
{
    WebPushPublicKeyStatus GetPublicKeyStatus();
    Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default);
}

public class WebPushNotificationService : IWebPushNotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string FinancialPushTitle = "Финансовое уведомление PayPlanner";
    private const string FinancialPushBody = "Откройте CRM, чтобы посмотреть детали финансового события после проверки прав доступа.";

    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebPushNotificationService> _logger;

    private sealed record VapidConfig(bool Enabled, string PublicKey, string PrivateKey, string Subject)
    {
        public bool Ready => Enabled
            && !string.IsNullOrWhiteSpace(PublicKey)
            && !string.IsNullOrWhiteSpace(PrivateKey)
            && !string.IsNullOrWhiteSpace(Subject);
    }

    public WebPushNotificationService(
        PaymentContext context,
        IConfiguration configuration,
        ILogger<WebPushNotificationService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public WebPushPublicKeyStatus GetPublicKeyStatus()
    {
        var config = ResolveVapidConfig();
        return new WebPushPublicKeyStatus(
            config.Enabled,
            config.Ready,
            config.Ready ? config.PublicKey : string.Empty,
            !string.IsNullOrWhiteSpace(config.PublicKey),
            !string.IsNullOrWhiteSpace(config.PrivateKey),
            !string.IsNullOrWhiteSpace(config.Subject));
    }

    public async Task SendUserNotificationAsync(UserNotification notification, CancellationToken cancellationToken = default)
    {
        if (notification.RecipientUserId <= 0)
        {
            return;
        }

        var subscriptions = await _context.UserPushSubscriptions
            .Where(item => item.UserId == notification.RecipientUserId && item.IsActive && item.RevokedAt == null)
            .OrderByDescending(item => item.LastSeenAt ?? item.UpdatedAt)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
        {
            return;
        }

        var config = ResolveVapidConfig();
        var pushTitle = ResolvePushTitle(notification);
        var pushBodyPreview = ResolvePushBodyPreview(notification);
        var payload = BuildPayload(notification, pushTitle, pushBodyPreview);
        var pendingDeliveries = new List<(NotificationDelivery Delivery, UserPushSubscription Subscription)>();
        foreach (var subscription in subscriptions)
        {
            if (await HasCompletedWebPushDeliveryAsync(notification.EventKey, subscription.Endpoint, cancellationToken))
            {
                continue;
            }

            var delivery = new NotificationDelivery
            {
                EventKey = notification.EventKey,
                Channel = NotificationChannels.WebPush,
                SenderName = "Web Push",
                RecipientUserId = notification.RecipientUserId,
                RecipientAddress = subscription.Endpoint,
                Subject = pushTitle,
                BodyPreview = pushBodyPreview,
                Status = NotificationDeliveryStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            _context.NotificationDeliveries.Add(delivery);
            pendingDeliveries.Add((delivery, subscription));
        }

        if (pendingDeliveries.Count == 0)
        {
            return;
        }

        await _context.SaveChangesAsync(cancellationToken);

        foreach (var (delivery, subscription) in pendingDeliveries)
        {
            if (!config.Ready)
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = ResolveNotReadyReason(config);
                continue;
            }

            try
            {
                await SendSubscriptionAsync(subscription, payload, config.Subject, config.PublicKey, config.PrivateKey, cancellationToken);
                delivery.Status = NotificationDeliveryStatus.Sent;
                delivery.SentAt = DateTime.UtcNow;
                subscription.LastSeenAt = DateTime.UtcNow;
                subscription.LastErrorAt = null;
                subscription.LastError = null;
            }
            catch (WebPushException ex) when (!cancellationToken.IsCancellationRequested)
            {
                delivery.Status = NotificationDeliveryStatus.Failed;
                delivery.Error = TruncateError(ex.Message);
                MarkSubscriptionError(subscription, delivery.Error, ex.StatusCode);
                _logger.LogWarning(
                    ex,
                    "Web Push delivery failed for user {UserId}, subscription {SubscriptionId}.",
                    notification.RecipientUserId,
                    subscription.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.Status = NotificationDeliveryStatus.Failed;
                delivery.Error = TruncateError(ex.Message);
                MarkSubscriptionError(subscription, delivery.Error, null);
                _logger.LogWarning(
                    ex,
                    "Web Push delivery failed for user {UserId}, subscription {SubscriptionId}.",
                    notification.RecipientUserId,
                    subscription.Id);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    protected virtual async Task SendSubscriptionAsync(
        UserPushSubscription subscription,
        string payload,
        string subject,
        string publicKey,
        string privateKey,
        CancellationToken cancellationToken)
    {
        var pushSubscription = new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        var vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        using var client = new WebPushClient();
        await client.SendNotificationAsync(pushSubscription, payload, vapidDetails, cancellationToken);
    }

    private VapidConfig ResolveVapidConfig()
    {
        var enabled = _configuration.GetValue("WebPush:Enabled", true);
        var publicKey = _configuration["WebPush:PublicKey"]?.Trim() ?? string.Empty;
        var privateKey = _configuration["WebPush:PrivateKey"]?.Trim() ?? string.Empty;
        var subject = _configuration["WebPush:Subject"]?.Trim() ?? string.Empty;
        return new VapidConfig(enabled, publicKey, privateKey, subject);
    }

    private async Task<bool> HasCompletedWebPushDeliveryAsync(
        string eventKey,
        string endpoint,
        CancellationToken cancellationToken)
        => await _context.NotificationDeliveries
            .AsNoTracking()
            .AnyAsync(item =>
                item.EventKey == eventKey
                && item.Channel == NotificationChannels.WebPush
                && item.RecipientAddress == endpoint
                && item.Status != NotificationDeliveryStatus.Failed,
                cancellationToken);

    private static string BuildPayload(UserNotification notification, string title, string bodyPreview)
        => JsonSerializer.Serialize(new
        {
            title,
            body = bodyPreview,
            notificationId = notification.Id,
            eventKey = notification.EventKey,
            type = notification.Type,
            tone = notification.Tone,
            objectType = notification.ObjectType,
            objectId = notification.ObjectId,
            taskJournalItemId = notification.TaskJournalItemId,
            url = "/notifications",
            createdAt = notification.CreatedAt,
        }, JsonOptions);

    private static string ResolvePushTitle(UserNotification notification)
        => IsSensitiveFinancialNotification(notification)
            ? FinancialPushTitle
            : notification.Title;

    private static string ResolvePushBodyPreview(UserNotification notification)
        => IsSensitiveFinancialNotification(notification)
            ? FinancialPushBody
            : notification.BodyPreview;

    private static bool IsSensitiveFinancialNotification(UserNotification notification)
    {
        if (notification.Type is UserNotificationTypes.PaymentAdvanceReviewRequired or UserNotificationTypes.PaymentAdvanceReviewEscalation)
        {
            return true;
        }

        return notification.ObjectType.Trim().ToLowerInvariant() switch
        {
            "payment" or "invoice" or "account" or "act" or "contract" or "cash-order" or "money-operation" => true,
            _ => false,
        };
    }

    private static string ResolveNotReadyReason(VapidConfig config)
    {
        if (!config.Enabled)
        {
            return "Web Push выключен в настройках сервера.";
        }

        return "Web Push не настроен: отсутствуют VAPID public key, private key или subject.";
    }

    private static void MarkSubscriptionError(UserPushSubscription subscription, string? error, HttpStatusCode? statusCode)
    {
        subscription.LastErrorAt = DateTime.UtcNow;
        subscription.LastError = error;
        subscription.UpdatedAt = DateTime.UtcNow;

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            subscription.IsActive = false;
            subscription.RevokedAt = DateTime.UtcNow;
        }
    }

    private static string TruncateError(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Web Push delivery failed." : value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }
}

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

public sealed class WebPushNotificationService : IWebPushNotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        var payload = BuildPayload(notification);
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
                Subject = notification.Title,
                BodyPreview = notification.BodyPreview,
                Status = NotificationDeliveryStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            };
            _context.NotificationDeliveries.Add(delivery);

            if (!config.Ready)
            {
                delivery.Status = NotificationDeliveryStatus.Skipped;
                delivery.Error = ResolveNotReadyReason(config);
                continue;
            }

            try
            {
                await SendSubscriptionAsync(subscription, payload, config, cancellationToken);
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

    private async Task SendSubscriptionAsync(
        UserPushSubscription subscription,
        string payload,
        VapidConfig config,
        CancellationToken cancellationToken)
    {
        var pushSubscription = new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
        var vapidDetails = new VapidDetails(config.Subject, config.PublicKey, config.PrivateKey);
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

    private static string BuildPayload(UserNotification notification)
        => JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.BodyPreview,
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

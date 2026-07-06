using System.Net;
using System.Text;
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
    Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default);
}

public class WebPushNotificationService : IWebPushNotificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxPayloadBytes = 3800;
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

    public async Task<NotificationDelivery?> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
    {
        var original = await _context.NotificationDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (original is null)
        {
            throw new InvalidOperationException("NotificationDeliveryNotFound");
        }

        if (!string.Equals(original.Channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliveryChannelInvalid");
        }

        if (string.Equals(original.Status, NotificationDeliveryStatus.Sent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliveryAlreadySent");
        }

        if (string.Equals(original.Status, NotificationDeliveryStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliveryPending");
        }

        if (string.Equals(original.Status, NotificationDeliveryStatus.Skipped, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NotificationDeliverySkipped");
        }

        if (!original.RecipientUserId.HasValue || string.IsNullOrWhiteSpace(original.RecipientAddress))
        {
            throw new InvalidOperationException("NotificationDeliveryRecipientMissing");
        }

        var baseEventKey = ResolveBaseEventKey(original.EventKey);
        var notification = await _context.UserNotifications
            .AsNoTracking()
            .FirstOrDefaultAsync(item =>
                item.EventKey == baseEventKey
                && item.RecipientUserId == original.RecipientUserId.Value,
                cancellationToken);
        if (notification is null)
        {
            throw new InvalidOperationException("UserNotificationNotFound");
        }

        var subscription = await _context.UserPushSubscriptions
            .FirstOrDefaultAsync(item =>
                item.UserId == original.RecipientUserId.Value
                && item.Endpoint == original.RecipientAddress
                && item.IsActive
                && item.RevokedAt == null,
                cancellationToken);
        if (subscription is null)
        {
            throw new InvalidOperationException("WebPushSubscriptionInactive");
        }

        var config = ResolveVapidConfig();
        var pushTitle = string.IsNullOrWhiteSpace(original.Subject)
            ? ResolvePushTitle(notification)
            : original.Subject;
        var pushBodyPreview = string.IsNullOrWhiteSpace(original.BodyPreview)
            ? ResolvePushBodyPreview(notification)
            : original.BodyPreview;
        var payload = BuildPayload(notification, pushTitle, pushBodyPreview);
        var retry = new NotificationDelivery
        {
            EventKey = $"{original.EventKey}:retry:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
            Channel = NotificationChannels.WebPush,
            SenderName = "Web Push",
            RecipientUserId = original.RecipientUserId,
            RecipientAddress = subscription.Endpoint,
            Subject = pushTitle,
            BodyPreview = pushBodyPreview,
            Status = NotificationDeliveryStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        _context.NotificationDeliveries.Add(retry);
        await _context.SaveChangesAsync(cancellationToken);

        if (!config.Ready)
        {
            retry.Status = NotificationDeliveryStatus.Skipped;
            retry.Error = ResolveNotReadyReason(config);
            await _context.SaveChangesAsync(cancellationToken);
            return retry;
        }

        try
        {
            await SendSubscriptionAsync(subscription, payload, config.Subject, config.PublicKey, config.PrivateKey, cancellationToken);
            retry.Status = NotificationDeliveryStatus.Sent;
            retry.SentAt = DateTime.UtcNow;
            subscription.LastSeenAt = DateTime.UtcNow;
            subscription.LastErrorAt = null;
            subscription.LastError = null;
        }
        catch (WebPushException ex) when (!cancellationToken.IsCancellationRequested)
        {
            retry.Status = NotificationDeliveryStatus.Failed;
            retry.Error = TruncateError(ex.Message);
            MarkSubscriptionError(subscription, retry.Error, ex.StatusCode);
            _logger.LogWarning(
                ex,
                "Web Push retry failed for user {UserId}, subscription {SubscriptionId}.",
                original.RecipientUserId,
                subscription.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            retry.Status = NotificationDeliveryStatus.Failed;
            retry.Error = TruncateError(ex.Message);
            MarkSubscriptionError(subscription, retry.Error, null);
            _logger.LogWarning(
                ex,
                "Web Push retry failed for user {UserId}, subscription {SubscriptionId}.",
                original.RecipientUserId,
                subscription.Id);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return retry;
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

    private static string ResolveBaseEventKey(string eventKey)
    {
        var retryIndex = eventKey.IndexOf(":retry:", StringComparison.Ordinal);
        return retryIndex < 0 ? eventKey : eventKey[..retryIndex];
    }

    private static string BuildPayload(UserNotification notification, string title, string bodyPreview)
    {
        var payload = SerializePayload(notification, title, bodyPreview);
        if (Encoding.UTF8.GetByteCount(payload) <= MaxPayloadBytes)
        {
            return payload;
        }

        var safeTitle = TrimToUtf8ByteLimit(title, 180);
        var safeBody = bodyPreview;
        for (var limit = Math.Min(1800, Encoding.UTF8.GetByteCount(bodyPreview)); limit >= 120; limit -= 120)
        {
            safeBody = TrimToUtf8ByteLimit(bodyPreview, limit);
            payload = SerializePayload(notification, safeTitle, safeBody);
            if (Encoding.UTF8.GetByteCount(payload) <= MaxPayloadBytes)
            {
                return payload;
            }
        }

        safeTitle = TrimToUtf8ByteLimit(title, 80);
        safeBody = TrimToUtf8ByteLimit(bodyPreview, 240);
        payload = SerializePayload(notification, safeTitle, safeBody);
        return Encoding.UTF8.GetByteCount(payload) <= MaxPayloadBytes
            ? payload
            : SerializeMinimalPayload(notification);
    }

    private static string SerializePayload(UserNotification notification, string title, string bodyPreview)
        => JsonSerializer.Serialize(new
        {
            title,
            body = bodyPreview,
            notificationId = notification.Id,
            eventKey = notification.EventKey,
            type = notification.Type,
            priority = UserNotificationTypes.Find(notification.Type)?.Priority ?? NotificationPriorities.Normal,
            tone = notification.Tone,
            objectType = notification.ObjectType,
            objectId = notification.ObjectId,
            taskJournalItemId = notification.TaskJournalItemId,
            url = ResolveNotificationUrl(notification),
            createdAt = notification.CreatedAt,
        }, JsonOptions);

    private static string SerializeMinimalPayload(UserNotification notification)
        => JsonSerializer.Serialize(new
        {
            title = "PayPlanner",
            body = "Откройте CRM, чтобы посмотреть уведомление.",
            notificationId = notification.Id,
            url = ResolveNotificationUrl(notification),
            createdAt = notification.CreatedAt,
        }, JsonOptions);

    private static string TrimToUtf8ByteLimit(string value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var byteCount = 0;
        var suffixBytes = Encoding.UTF8.GetByteCount("...");
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes + suffixBytes > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            byteCount += runeBytes;
        }

        return builder.Length == 0 ? string.Empty : $"{builder}...";
    }

    private static string ResolveNotificationUrl(UserNotification notification)
    {
        var objectType = notification.ObjectType.Trim().ToLowerInvariant();
        if (notification.TaskJournalItemId is int taskId && taskId > 0)
        {
            return $"/tasks/{taskId}";
        }

        if (notification.ObjectId is not { } objectId || objectId <= 0)
        {
            return "/notifications";
        }

        return objectType switch
        {
            "task" => $"/tasks/{objectId}",
            "payment" => $"/payments/{objectId}",
            "invoice" or "account" => $"/invoices/{objectId}",
            "act" => $"/acts/{objectId}",
            "contract" => $"/contracts/{objectId}",
            "case" or "client-case" => ResolveCaseNotificationUrl(notification, objectId),
            "calendar" => "/tasks/calendar",
            "note" or "sticky-note" => "/notes",
            _ => "/notifications",
        };
    }

    private static string ResolveCaseNotificationUrl(UserNotification notification, int caseId)
    {
        if (notification.Type == UserNotificationTypes.CaseDocument
            || TryReadPositiveMetadataInt(notification.MetadataJson, "clientCaseDocumentId").HasValue)
        {
            return $"/cases/{caseId}?ppCaseTab=documents";
        }

        if (notification.Type is UserNotificationTypes.CaseCourtEvent
            or UserNotificationTypes.CaseProceduralDeadline
            or UserNotificationTypes.CourtHearing
            or UserNotificationTypes.CourtStatusChanged
            or UserNotificationTypes.CourtDecisionPublished
            or UserNotificationTypes.CourtSyncFailed
            || TryReadPositiveMetadataInt(notification.MetadataJson, "courtEventId").HasValue
            || TryReadPositiveMetadataInt(notification.MetadataJson, "proceduralDeadlineId").HasValue)
        {
            return $"/cases/{caseId}?ppCaseTab=court";
        }

        return $"/cases/{caseId}";
    }

    private static int? TryReadPositiveMetadataInt(string? metadataJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            var parsed = value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
                _ => 0,
            };
            return parsed > 0 ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

        if (WebPushSubscriptionHealth.TryRevokeInvalidSubscription(subscription, DateTime.UtcNow, error, statusCode))
        {
            subscription.LastErrorAt ??= DateTime.UtcNow;
        }
    }

    private static string TruncateError(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Web Push delivery failed." : value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }
}

using System.Net;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

internal static class WebPushSubscriptionHealth
{
    public static bool TryRevokeInvalidSubscription(
        UserPushSubscription subscription,
        DateTime now,
        string? error = null,
        HttpStatusCode? statusCode = null)
    {
        var storedError = error ?? subscription.LastError;
        if (!IsInvalidSubscriptionError(storedError, statusCode))
        {
            return false;
        }

        subscription.IsActive = false;
        subscription.UpdatedAt = now;
        subscription.RevokedAt ??= now;
        return true;
    }

    public static bool IsInvalidSubscriptionError(string? error, HttpStatusCode? statusCode = null)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
        }

        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return true;
        }

        if (statusCode is not null and not HttpStatusCode.Forbidden)
        {
            return false;
        }

        return IsVapidCredentialsMismatch(error)
            || Contains(error, "push subscription has unsubscribed or expired")
            || Contains(error, "subscription no longer valid")
            || Contains(error, "unsubscribed or expired");
    }

    public static bool IsNonRetryableDeliveryError(string? error, HttpStatusCode? statusCode = null)
        => IsInvalidSubscriptionError(error, statusCode)
            || IsPayloadTooLarge(error);

    private static bool IsVapidCredentialsMismatch(string error)
        => Contains(error, "VAPID credentials")
            && Contains(error, "do not correspond");

    private static bool IsPayloadTooLarge(string? error)
        => !string.IsNullOrWhiteSpace(error)
            && (Contains(error, "less than 4096 bytes")
                || Contains(error, "payload too large")
                || Contains(error, "request entity too large"));

    private static bool Contains(string value, string pattern)
        => value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
}

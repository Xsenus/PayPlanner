using System.Security.Cryptography;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

/// <summary>
/// Готовит безопасное отображение технических реквизитов доставки для административного журнала.
/// Исходный адрес остаётся в сущности и доступен внутреннему механизму повторной отправки.
/// </summary>
public static class NotificationDeliveryPresentation
{
    public static string ResolveChannel(NotificationDelivery delivery)
    {
        var channel = delivery.Channel?.Trim() ?? string.Empty;
        if (string.Equals(channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
        {
            return NotificationChannels.WebPush;
        }

        if (string.Equals(channel, NotificationChannels.Email, StringComparison.OrdinalIgnoreCase)
            && IsLegacyWebPush(delivery))
        {
            return NotificationChannels.WebPush;
        }

        return channel;
    }

    public static string ResolveRecipientAddress(NotificationDelivery delivery)
    {
        var address = delivery.RecipientAddress?.Trim() ?? string.Empty;
        if (!string.Equals(ResolveChannel(delivery), NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        if (address.Length == 0)
        {
            return "Push-устройство";
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)))[..8];
        return $"Push-устройство • {fingerprint}";
    }

    public static string? ResolveError(NotificationDelivery delivery)
        => ResolveError(ResolveChannel(delivery), delivery.Error);

    public static string? ResolveError(string? channel, string? rawError)
    {
        var error = rawError?.Trim();
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        if (string.Equals(channel?.Trim(), NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
        {
            if (error.Contains("binary data passed", StringComparison.OrdinalIgnoreCase) &&
                error.Contains("4096", StringComparison.OrdinalIgnoreCase))
            {
                return "Размер данных Push-уведомления превышает допустимый лимит.";
            }

            if (error.Contains("VAPID credentials", StringComparison.OrdinalIgnoreCase))
            {
                return "Push-подписка создана с другими ключами. Пользователю нужно заново разрешить уведомления.";
            }

            if (error.Contains("response code: 403", StringComparison.OrdinalIgnoreCase))
            {
                return "Push-подписка больше не принимает уведомления (код 403). Пользователю нужно подключить Push заново.";
            }

            if (error.Contains("response code: 404", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("response code: 410", StringComparison.OrdinalIgnoreCase))
            {
                return "Push-подписка устарела или была удалена. Пользователю нужно подключить Push заново.";
            }
        }

        const int maxUserVisibleLength = 1_000;
        return error.Length <= maxUserVisibleLength ? error : $"{error[..maxUserVisibleLength]}…";
    }

    private static bool IsLegacyWebPush(NotificationDelivery delivery)
        => delivery.SenderName.Contains("Web Push", StringComparison.OrdinalIgnoreCase)
           || delivery.ProviderKey?.Contains("webpush", StringComparison.OrdinalIgnoreCase) == true
           || delivery.RecipientAddress.Contains("fcm.googleapis.com/fcm/send", StringComparison.OrdinalIgnoreCase);
}

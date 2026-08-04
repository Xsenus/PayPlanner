using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed partial class ExternalAuthService
{
    private static readonly JsonSerializerOptions TelegramNotificationJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ResolvedTelegramAuthBot(
        bool Enabled,
        string BotToken,
        string BotUsername,
        string WebhookSecret,
        DateTime? BotInfoSyncedAt,
        DateTime? WebhookSyncedAt,
        string WebhookUrl,
        int? NotificationSenderId);

    private async Task<ResolvedTelegramAuthBot?> ResolveTelegramAuthBotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notificationSender = await ResolveNotificationTelegramAuthSenderAsync(cancellationToken);
        if (notificationSender is not null)
        {
            return notificationSender;
        }

        var settings = await LoadSettingsSnapshotAsync(
            [
                TelegramEnabledKey,
                TelegramBotTokenKey,
                TelegramBotUsernameKey,
                TelegramWebhookSecretKey,
                TelegramBotInfoSyncedAtKey,
                TelegramWebhookSyncedAtKey,
            ],
            cancellationToken);
        return ResolveLegacyTelegramAuthBot(settings);
    }

    /// <summary>
    /// Разрешает Telegram-бота с уже загруженным снимком legacy-настроек.
    /// Это не допускает повторного чтения каждой настройки отдельным SQL-запросом.
    /// </summary>
    private async Task<ResolvedTelegramAuthBot?> ResolveTelegramAuthBotAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ResolveNotificationTelegramAuthSenderAsync(cancellationToken)
               ?? ResolveLegacyTelegramAuthBot(settings);
    }

    private ResolvedTelegramAuthBot ResolveLegacyTelegramAuthBot(IReadOnlyDictionary<string, string> settings)
    {
        var enabled = GetBooleanSettingOrConfig(settings, TelegramEnabledKey, "Auth:Telegram:Enabled", fallback: false);
        var botToken = GetSettingOrConfig(settings, TelegramBotTokenKey, "Auth:Telegram:BotToken") ?? string.Empty;
        var botUsername = GetSettingOrConfig(settings, TelegramBotUsernameKey, "Auth:Telegram:BotUsername") ?? string.Empty;
        var webhookSecret = GetSettingOrConfig(settings, TelegramWebhookSecretKey, "Auth:Telegram:WebhookSecret") ?? string.Empty;
        var botInfoSyncedAt = ParseDateTime(GetSettingOrConfig(settings, TelegramBotInfoSyncedAtKey, null));
        var webhookSyncedAt = ParseDateTime(GetSettingOrConfig(settings, TelegramWebhookSyncedAtKey, null));

        return new ResolvedTelegramAuthBot(
            enabled,
            botToken,
            botUsername,
            webhookSecret,
            botInfoSyncedAt,
            webhookSyncedAt,
            string.Empty,
            null);
    }

    private async Task<ResolvedTelegramAuthBot?> ResolveNotificationTelegramAuthSenderAsync(CancellationToken cancellationToken)
    {
        var senders = await _context.NotificationSenders
            .AsNoTracking()
            .Where(sender => sender.Channel == NotificationChannels.Telegram && sender.IsEnabled)
            .OrderBy(sender => sender.SortOrder)
            .ThenBy(sender => sender.Id)
            .ToListAsync(cancellationToken);

        foreach (var sender in senders)
        {
            if (!HasNotificationPurpose(sender.PurposesJson, NotificationPurposes.TelegramAuth))
            {
                continue;
            }

            var config = DeserializeTelegramNotificationConfig(sender.ConfigJson);
            if (string.IsNullOrWhiteSpace(config.BotToken) || string.IsNullOrWhiteSpace(config.BotUsername))
            {
                continue;
            }

            return new ResolvedTelegramAuthBot(
                true,
                config.BotToken,
                config.BotUsername,
                config.WebhookSecret,
                config.ProfileSyncedAt,
                config.WebhookSyncedAt,
                config.WebhookUrl,
                sender.Id);
        }

        return null;
    }

    private static bool HasNotificationPurpose(string? rawPurposes, string purpose)
        => DeserializeNotificationPurposes(rawPurposes)
            .Any(item => string.Equals(item, purpose, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> DeserializeNotificationPurposes(string? rawPurposes)
    {
        if (string.IsNullOrWhiteSpace(rawPurposes))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(rawPurposes, TelegramNotificationJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TelegramSenderConfig DeserializeTelegramNotificationConfig(string? rawConfig)
    {
        if (string.IsNullOrWhiteSpace(rawConfig))
        {
            return new TelegramSenderConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<TelegramSenderConfig>(rawConfig, TelegramNotificationJsonOptions)
                   ?? new TelegramSenderConfig();
        }
        catch (JsonException)
        {
            return new TelegramSenderConfig();
        }
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed class TelegramNotificationService : ITelegramNotificationService
{
    private const string NotificationEnabledKey = "notifications.telegram.enabled";
    private const string NotificationBotTokenKey = "notifications.telegram.botToken";
    private const string AuthEnabledKey = "auth.telegram.enabled";
    private const string AuthBotTokenKey = "auth.telegram.botToken";
    private const string AuthBotUsernameKey = "auth.telegram.botUsername";

    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(
        PaymentContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramNotificationService> logger)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var senderConfigs = await LoadEnabledTelegramSenderConfigsAsync(cancellationToken);
        var authSenderConfig = senderConfigs
            .FirstOrDefault(item => HasPurpose(item.PurposesJson, NotificationPurposes.TelegramAuth))
            .Config;
        var firstSenderConfig = senderConfigs.FirstOrDefault().Config;
        var token = ResolveValue(settings, NotificationBotTokenKey, null)
                    ?? ResolveValue(settings, AuthBotTokenKey, "Auth:Telegram:BotToken")
                    ?? authSenderConfig?.BotToken
                    ?? firstSenderConfig?.BotToken;
        var enabled = ResolveBool(settings, NotificationEnabledKey, "Notifications:Telegram:Enabled")
                      ?? ResolveBool(settings, AuthEnabledKey, "Auth:Telegram:Enabled")
                      ?? senderConfigs.Count > 0;
        var username = authSenderConfig?.BotUsername
                       ?? firstSenderConfig?.BotUsername
                       ?? ResolveValue(settings, AuthBotUsernameKey, "Auth:Telegram:BotUsername")
                       ?? string.Empty;
        var linkedUsersCount = await _context.UserExternalIdentities
            .AsNoTracking()
            .Where(identity => identity.Provider == "telegram" && identity.ChatId.HasValue)
            .Select(identity => identity.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new TelegramNotificationSettingsDto
        {
            Enabled = enabled,
            BotTokenSet = !string.IsNullOrWhiteSpace(token),
            BotUsername = username,
            LinkedUsersCount = linkedUsersCount,
            Ready = enabled && !string.IsNullOrWhiteSpace(token),
        };
    }

    private async Task<IReadOnlyList<(string PurposesJson, TelegramSenderConfig Config)>> LoadEnabledTelegramSenderConfigsAsync(
        CancellationToken cancellationToken)
    {
        var senders = await _context.NotificationSenders
            .AsNoTracking()
            .Where(sender => sender.Channel == NotificationChannels.Telegram && sender.IsEnabled)
            .OrderBy(sender => sender.SortOrder)
            .ThenBy(sender => sender.Id)
            .Select(sender => new { sender.PurposesJson, sender.ConfigJson })
            .ToListAsync(cancellationToken);

        return senders
            .Select(sender => (sender.PurposesJson, Config: DeserializeTelegramSenderConfig(sender.ConfigJson)))
            .Where(sender => !string.IsNullOrWhiteSpace(sender.Config.BotToken))
            .ToList();
    }

    public async Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
    {
        if (chatId == 0)
        {
            return new TelegramSendResult(false, NotificationDeliveryStatus.Skipped, "У получателя не указан Telegram chat id.");
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        var enabled = ResolveBool(settings, NotificationEnabledKey, "Notifications:Telegram:Enabled")
                      ?? ResolveBool(settings, AuthEnabledKey, "Auth:Telegram:Enabled")
                      ?? false;
        if (!enabled)
        {
            _logger.LogInformation("Telegram notifications are disabled. Dry-run for chat {ChatId}.", chatId);
            return new TelegramSendResult(false, NotificationDeliveryStatus.Skipped, "Telegram-уведомления отключены.");
        }

        var token = ResolveValue(settings, NotificationBotTokenKey, null)
                    ?? ResolveValue(settings, AuthBotTokenKey, "Auth:Telegram:BotToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Telegram bot token is not configured. Skipping message to chat {ChatId}.", chatId);
            return new TelegramSendResult(false, NotificationDeliveryStatus.Skipped, "Telegram bot token не настроен.");
        }

        return await SendMessageWithTokenAsync(chatId, text, token, cancellationToken);
    }

    public async Task<TelegramSendResult> SendMessageWithTokenAsync(
        long chatId,
        string text,
        string botToken,
        CancellationToken cancellationToken = default,
        TelegramBotConnectionSettings? connection = null)
    {
        if (chatId == 0)
        {
            return new TelegramSendResult(false, NotificationDeliveryStatus.Skipped, "У получателя не указан Telegram chat id.");
        }

        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new TelegramSendResult(false, NotificationDeliveryStatus.Skipped, "Telegram bot token не настроен.");
        }

        try
        {
            using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, connection);
            using var response = await client.PostAsync(
                $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["text"] = text,
                    ["disable_web_page_preview"] = "true",
                }),
                cancellationToken);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ExtractTelegramError(payload) ?? response.ReasonPhrase ?? "Telegram API вернул ошибку.";
                _logger.LogWarning("Telegram message to {ChatId} failed: {Detail}", chatId, detail);
                return new TelegramSendResult(false, NotificationDeliveryStatus.Failed, detail);
            }

            return new TelegramSendResult(true, NotificationDeliveryStatus.Sent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to chat {ChatId}.", chatId);
            return new TelegramSendResult(false, NotificationDeliveryStatus.Failed, ex.Message);
        }
    }

    private async Task<Dictionary<string, string>> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            NotificationEnabledKey,
            NotificationBotTokenKey,
            AuthEnabledKey,
            AuthBotTokenKey,
            AuthBotUsernameKey,
        };

        return await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
    }

    private string? ResolveValue(IReadOnlyDictionary<string, string> settings, string settingKey, string? configPath)
    {
        if (settings.TryGetValue(settingKey, out var stored) && !string.IsNullOrWhiteSpace(stored))
        {
            return stored.Trim();
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        var configured = _configuration[configPath];
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private bool? ResolveBool(IReadOnlyDictionary<string, string> settings, string settingKey, string? configPath)
    {
        var raw = ResolveValue(settings, settingKey, configPath);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
    }

    private static bool HasPurpose(string? rawPurposes, string purpose)
    {
        if (string.IsNullOrWhiteSpace(rawPurposes))
        {
            return false;
        }

        try
        {
            var purposes = JsonSerializer.Deserialize<List<string>>(rawPurposes, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            return purposes.Any(item => string.Equals(item, purpose, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TelegramSenderConfig DeserializeTelegramSenderConfig(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TelegramSenderConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<TelegramSenderConfig>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? new TelegramSenderConfig();
        }
        catch (JsonException)
        {
            return new TelegramSenderConfig();
        }
    }

    private static string? ExtractTelegramError(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("description", out var description)
                ? description.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

namespace PayPlanner.Api.Models.Responses;

public class AuthProviderSettingsDto
{
    public bool GoogleEnabled { get; set; }
    public string GoogleClientId { get; set; } = string.Empty;
    public string GoogleClientSecret { get; set; } = string.Empty;
    public bool GoogleReady { get; set; }
    public string? GoogleCallbackUrl { get; set; }

    public bool VkEnabled { get; set; }
    public string VkClientId { get; set; } = string.Empty;
    public string VkClientSecret { get; set; } = string.Empty;
    public bool VkReady { get; set; }
    public string? VkCallbackUrl { get; set; }

    public bool YandexEnabled { get; set; }
    public string YandexClientId { get; set; } = string.Empty;
    public string YandexClientSecret { get; set; } = string.Empty;
    public bool YandexReady { get; set; }
    public string? YandexCallbackUrl { get; set; }

    public bool TelegramEnabled { get; set; }
    public bool TelegramWidgetEnabled { get; set; }
    public string TelegramBotToken { get; set; } = string.Empty;
    public string TelegramBotUsername { get; set; } = string.Empty;
    public string TelegramWebhookSecret { get; set; } = string.Empty;
    public string? TelegramWebhookUrl { get; set; }
    public bool TelegramWebhookConfigured { get; set; }
    public bool TelegramBotReady { get; set; }
    public bool TelegramWidgetReady { get; set; }
    public DateTime? TelegramBotInfoSyncedAt { get; set; }
    public DateTime? TelegramWebhookSyncedAt { get; set; }
}

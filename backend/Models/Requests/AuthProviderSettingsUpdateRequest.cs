namespace PayPlanner.Api.Models.Requests;

public class AuthProviderSettingsUpdateRequest
{
    public bool GoogleEnabled { get; set; }
    public string? GoogleClientId { get; set; }
    public string? GoogleClientSecret { get; set; }

    public bool VkEnabled { get; set; }
    public string? VkClientId { get; set; }
    public string? VkClientSecret { get; set; }

    public bool YandexEnabled { get; set; }
    public string? YandexClientId { get; set; }
    public string? YandexClientSecret { get; set; }

    public bool TelegramEnabled { get; set; }
    public bool TelegramWidgetEnabled { get; set; }
    public string? TelegramBotToken { get; set; }
}

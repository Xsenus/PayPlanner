namespace PayPlanner.Api.Models.Responses;

public class AuthProviderPublicSettingsDto
{
    public bool GoogleEnabled { get; set; }
    public bool VkEnabled { get; set; }
    public bool YandexEnabled { get; set; }
    public bool TelegramEnabled { get; set; }
    public bool TelegramWidgetEnabled { get; set; }
    public string? TelegramBotUsername { get; set; }
}

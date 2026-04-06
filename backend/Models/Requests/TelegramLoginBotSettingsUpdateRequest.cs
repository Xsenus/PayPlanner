namespace PayPlanner.Api.Models.Requests;

public class TelegramLoginBotSettingsUpdateRequest
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
}

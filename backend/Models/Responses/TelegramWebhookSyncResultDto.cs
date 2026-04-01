namespace PayPlanner.Api.Models.Responses;

public class TelegramWebhookSyncResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? BotUsername { get; set; }
    public string? WebhookUrl { get; set; }
}

namespace PayPlanner.Api.Models.Requests;

public class TelegramBotAuthStartRequest
{
    public string? ReturnUrl { get; set; }
    public string? Intent { get; set; }
}

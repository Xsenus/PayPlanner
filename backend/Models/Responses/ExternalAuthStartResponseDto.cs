namespace PayPlanner.Api.Models.Responses;

public class ExternalAuthStartResponseDto
{
    public string Provider { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string AuthUrl { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/";
    public DateTime ExpiresAt { get; set; }
    public int PollIntervalMs { get; set; } = 2000;
}

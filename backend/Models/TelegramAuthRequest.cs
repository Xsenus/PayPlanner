namespace PayPlanner.Api.Models;

public class TelegramAuthRequest
{
    public int Id { get; set; }
    public string State { get; set; } = string.Empty;
    public int? UserId { get; set; }
    public string Intent { get; set; } = "signin";
    public string? TelegramUserId { get; set; }
    public long? ChatId { get; set; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public User? User { get; set; }
}

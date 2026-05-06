namespace PayPlanner.Api.Models;

public class NotificationBotSubscription
{
    public int Id { get; set; }
    public int NotificationSenderId { get; set; }
    public NotificationSender? NotificationSender { get; set; }
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string TelegramUserId { get; set; } = string.Empty;
    public long ChatId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

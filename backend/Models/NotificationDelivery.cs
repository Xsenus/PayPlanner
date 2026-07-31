namespace PayPlanner.Api.Models;

public class NotificationDelivery
{
    public int Id { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public int? NotificationSenderId { get; set; }
    public NotificationSender? NotificationSender { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public int? RecipientUserId { get; set; }
    public User? RecipientUser { get; set; }
    public string RecipientAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public string? ProviderKey { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderStatus { get; set; }
    public DateTime? ProviderStatusCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}

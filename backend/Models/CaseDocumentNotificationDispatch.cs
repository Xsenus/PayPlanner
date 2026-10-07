namespace PayPlanner.Api.Models;

public sealed class CaseDocumentNotificationDispatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int UserNotificationId { get; set; }
    public UserNotification UserNotification { get; set; } = null!;
    public string Channel { get; set; } = "realtime";
    public bool ToastEnabled { get; set; }
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

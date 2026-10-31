namespace PayPlanner.Api.Models;

/// <summary>A durable intent for one background transition into overdue.</summary>
public sealed class PaymentOverdueNotificationOutbox
{
    public long Id { get; set; }
    public int PaymentId { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string TransitionToken { get; set; } = string.Empty;
    public DocumentKind DocumentKind { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
}

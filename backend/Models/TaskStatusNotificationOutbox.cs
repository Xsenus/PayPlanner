namespace PayPlanner.Api.Models;

/// <summary>A persisted delivery request for one task status transition.</summary>
public sealed class TaskStatusNotificationOutbox
{
    public long Id { get; set; }
    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;
    public string EventKey { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string StatusTransitionToken { get; set; } = string.Empty;
    public int? ApprovalParticipantId { get; set; }
    public TaskJournalParticipant? ApprovalParticipant { get; set; }
    public int ActorUserId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
}

namespace PayPlanner.Api.Models;

public sealed class CaseCourtNotificationOutbox
{
    public long Id { get; set; }
    public int ClientCaseId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public bool JournalChanged { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? NotificationCompletedAt { get; set; }
    public DateTime? JournalCompletedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? BlockedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
    public string? LastError { get; set; }
}

namespace PayPlanner.Api.Models;

public class UserNotification
{
    public int Id { get; set; }
    public int RecipientUserId { get; set; }
    public User? RecipientUser { get; set; }
    public string Type { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
    public string Tone { get; set; } = "blue";
    public string ObjectType { get; set; } = string.Empty;
    public int? ObjectId { get; set; }
    public int? TaskJournalItemId { get; set; }
    public TaskJournalItem? TaskJournalItem { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

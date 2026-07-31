using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalComment
{
    public int Id { get; set; }

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    public int? AuthorUserId { get; set; }
    public User? AuthorUser { get; set; }

    [Required]
    [MaxLength(256)]
    public string AuthorName { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Kind { get; set; } = "comment";

    [Required]
    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    public bool IsExternalCalendarVisible { get; set; }

    [MaxLength(32)]
    public string? ExternalSource { get; set; }

    [MaxLength(256)]
    public string? ExternalCommentId { get; set; }

    [MaxLength(256)]
    public string? ExternalRevision { get; set; }

    [MaxLength(64)]
    public string? ExternalSyncHash { get; set; }

    [MaxLength(32)]
    public string? ExternalAuthorTrust { get; set; }

    public DateTime? ExternalDeletedAt { get; set; }
    public DateTime? ExternalSyncedAt { get; set; }

    public string MetadataJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

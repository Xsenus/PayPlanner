using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalDeadlineChangeRequest
{
    public int Id { get; set; }

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    public int? RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    [Required]
    [MaxLength(256)]
    public string RequestedByName { get; set; } = string.Empty;

    public int? ReviewerUserId { get; set; }
    public User? ReviewerUser { get; set; }

    [Required]
    [MaxLength(256)]
    public string ReviewerName { get; set; } = string.Empty;

    public DateTime CurrentDueAt { get; set; }

    public DateTime RequestedDueAt { get; set; }

    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public DateTime? DecisionDueAt { get; set; }

    [MaxLength(1000)]
    public string? DecisionComment { get; set; }

    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    [MaxLength(256)]
    public string? DecidedByName { get; set; }

    public DateTime? DecidedAt { get; set; }

    public int? CreatedCommentId { get; set; }
    public TaskJournalComment? CreatedComment { get; set; }

    public int? DecisionCommentId { get; set; }
    public TaskJournalComment? DecisionCommentEntry { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    // Pending reassignment notifications survive a crash after the task transaction commits.
    public bool ReviewerDispatchPending { get; set; }
    public string? ReviewerDispatchLeaseToken { get; set; }
    public DateTime? ReviewerDispatchLeaseUntil { get; set; }
    public int ReviewerDispatchAttempts { get; set; }
}

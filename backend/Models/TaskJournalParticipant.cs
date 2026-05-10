using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalParticipant
{
    public int Id { get; set; }

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    public int? UserId { get; set; }
    public User? User { get; set; }

    [Required]
    [MaxLength(32)]
    public string Role { get; set; } = "assignee";

    [Required]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    [MaxLength(32)]
    public string ApprovalStatus { get; set; } = "pending";

    [MaxLength(4000)]
    public string? Resolution { get; set; }

    public DateTime? RespondedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

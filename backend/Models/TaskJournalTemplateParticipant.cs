using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalTemplateParticipant
{
    public int Id { get; set; }

    public int TaskJournalTemplateId { get; set; }
    public TaskJournalTemplate TaskJournalTemplate { get; set; } = null!;

    public int? UserId { get; set; }
    public User? User { get; set; }

    [Required]
    [MaxLength(32)]
    public string Role { get; set; } = "observer";

    [Required]
    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public sealed class CasePositionTemplate
{
    public int Id { get; set; }

    [Required]
    [MaxLength(160)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    // Null means a shared template managed by an administrator.
    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

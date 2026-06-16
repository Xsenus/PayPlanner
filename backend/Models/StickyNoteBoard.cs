using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class StickyNoteBoardVisibility
{
    public const string Personal = "personal";
    public const string Shared = "shared";
}

public class StickyNoteBoard
{
    public int Id { get; set; }

    [Required]
    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(24)]
    public string Visibility { get; set; } = StickyNoteBoardVisibility.Personal;

    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    [MaxLength(256)]
    public string OwnerUserName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Background { get; set; } = "cork";

    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<StickyNote> Notes { get; set; } = new List<StickyNote>();
    public ICollection<StickyNoteBoardMember> Members { get; set; } = new List<StickyNoteBoardMember>();
}

public class StickyNoteBoardMember
{
    public int Id { get; set; }

    public int BoardId { get; set; }
    public StickyNoteBoard? Board { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    [Required]
    [MaxLength(24)]
    public string Role { get; set; } = "editor";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

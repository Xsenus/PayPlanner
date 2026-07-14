using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class StickyNote
{
    public int Id { get; set; }

    public int BoardId { get; set; }
    public StickyNoteBoard? Board { get; set; }

    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    [Required]
    [MaxLength(24)]
    public string Color { get; set; } = "yellow";

    public double X { get; set; } = 80;
    public double Y { get; set; } = 80;
    public double Width { get; set; } = 220;
    public double Height { get; set; } = 180;
    public int FontSize { get; set; } = 14;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public int Opacity { get; set; } = 100;
    public int ZIndex { get; set; } = 1;
    public int Version { get; set; } = 1;
    public bool IsPinned { get; set; }

    public int? AssignedUserId { get; set; }
    public User? AssignedUser { get; set; }

    [MaxLength(256)]
    public string AssignedUserName { get; set; } = string.Empty;

    public DateTime? ReminderAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }

    public int? AuthorUserId { get; set; }
    public User? AuthorUser { get; set; }

    [MaxLength(256)]
    public string AuthorUserName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

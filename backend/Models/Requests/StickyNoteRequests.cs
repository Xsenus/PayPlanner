using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public sealed class CreateStickyNoteBoardRequest
{
    [Required]
    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(24)]
    public string Visibility { get; set; } = StickyNoteBoardVisibility.Personal;

    [MaxLength(32)]
    public string Background { get; set; } = "cork";
}

public sealed class UpdateStickyNoteBoardRequest
{
    [Required]
    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(32)]
    public string Background { get; set; } = "cork";
}

public sealed class CreateStickyNoteRequest
{
    [MaxLength(4000)]
    public string? Text { get; set; }

    [MaxLength(24)]
    public string Color { get; set; } = "yellow";

    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public int? AssignedUserId { get; set; }
    public DateTime? ReminderAt { get; set; }
}

public sealed class UpdateStickyNoteRequest
{
    [MaxLength(4000)]
    public string Text { get; set; } = string.Empty;

    [MaxLength(24)]
    public string Color { get; set; } = "yellow";

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int ZIndex { get; set; }
    public bool IsPinned { get; set; }
    public int? AssignedUserId { get; set; }
    public DateTime? ReminderAt { get; set; }
    public int? Version { get; set; }
}

public sealed class UpdateStickyNoteLayoutRequest
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int ZIndex { get; set; }
    public int? Version { get; set; }
}

public sealed class UpsertStickyNoteBoardMemberRequest
{
    public int UserId { get; set; }

    [MaxLength(24)]
    public string Role { get; set; } = "viewer";
}

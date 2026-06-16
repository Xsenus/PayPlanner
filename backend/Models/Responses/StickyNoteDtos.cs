namespace PayPlanner.Api.Models.Responses;

public sealed class StickyNoteBoardDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = string.Empty;
    public int? OwnerUserId { get; set; }
    public string OwnerUserName { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int NotesCount { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanDeleteNotes { get; set; }
    public bool CanManageMembers { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class StickyNoteBoardMemberDto
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class StickyNoteBoardMemberCandidateDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class StickyNoteDto
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int ZIndex { get; set; }
    public int Version { get; set; }
    public bool IsPinned { get; set; }
    public int? AuthorUserId { get; set; }
    public string AuthorUserName { get; set; } = string.Empty;
    public int? LastUpdatedByUserId { get; set; }
    public string LastUpdatedByUserName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class StickyNoteRealtimeEventDto
{
    public string Type { get; set; } = string.Empty;
    public int BoardId { get; set; }
    public StickyNoteDto? Note { get; set; }
    public int? NoteId { get; set; }
    public int ActorUserId { get; set; }
    public string ActorUserName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace PayPlanner.Api.Models;

/// <summary>A personal interval of actual work; it does not create financial records.</summary>
public sealed class TaskTimeEntry
{
    public Guid Id { get; set; }
    public int TaskId { get; set; }
    public TaskJournalItem Task { get; set; } = null!;
    public int? UserId { get; set; }
    public User? User { get; set; }
    public string UserNameSnapshot { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public int? CorrectedSeconds { get; set; }
    public int Revision { get; set; }
}

/// <summary>Append-only record of a personal correction; original clock times remain unchanged.</summary>
public sealed class TaskTimeCorrection
{
    public long Id { get; set; }
    public Guid EntryId { get; set; }
    public TaskTimeEntry Entry { get; set; } = null!;
    public int? PreviousSeconds { get; set; }
    public int NewSeconds { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

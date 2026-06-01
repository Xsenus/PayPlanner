using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class GoogleCalendarTaskLink
{
    public int Id { get; set; }

    public int ConnectionId { get; set; }
    public GoogleCalendarConnection Connection { get; set; } = null!;

    public int CalendarSyncCalendarId { get; set; }
    public GoogleCalendarSyncCalendar CalendarSyncCalendar { get; set; } = null!;

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    [Required]
    [MaxLength(256)]
    public string GoogleCalendarId { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string GoogleEventId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? GoogleEtag { get; set; }

    [MaxLength(128)]
    public string? LastCrmHash { get; set; }

    [MaxLength(128)]
    public string? LastGoogleHash { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(32)]
    public string? LastDirection { get; set; }

    [MaxLength(32)]
    public string SyncStatus { get; set; } = "active";

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
